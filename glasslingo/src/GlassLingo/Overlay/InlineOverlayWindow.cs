using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using GlassLingo.Config;
using GlassLingo.Native;
using GlassLingo.Ocr;
using GlassLingo.Sessions;

namespace GlassLingo.Overlay
{
    internal sealed class InlineOverlayWindow : Window
    {
        private readonly Canvas _canvas = new() { ClipToBounds = true };
        private readonly List<(Border Plate, TextBlock Text)> _blocks = new();
        private readonly AppConfig _config;
        private static readonly List<InlineOverlayWindow> Peers = new();
        private readonly bool _coordinatePeers;
        private readonly bool _captionMode;
        private double _captionTextHeight;
        private bool _singleCaptionLayout;
        private readonly CaptionStability _captionStability = new();
        private Rect?[] _eligible = [];
        private Int32Rect _presentedRegion;
        private Int32Rect _displayRegion;
        private double _offsetX, _offsetY;
        private SessionFrame? _sourceFrame;

        private OcrOutcome? _fontLayout;
        private double _fontScale;
        private double[] _fonts = [];
        private List<(double X, double Y, double Height, double Size)> _previousFonts = new();
        public bool CaptureSafe { get; private set; }
        public bool TextClipped { get; private set; }
        internal double RenderedFontSize => _blocks.Count > 0 ? _blocks[0].Text.FontSize : 0;
        internal bool HasVisibleBorder => _blocks.Exists(b => b.Plate.BorderThickness != new Thickness(0));
        internal int VisualCount => _canvas.Children.Count;
        internal double FirstLeft => _blocks.Count > 0 ? Canvas.GetLeft(_blocks[0].Plate) + _offsetX : 0;
        internal double FirstTop => _blocks.Count > 0 ? Canvas.GetTop(_blocks[0].Plate) + _offsetY : 0;
        internal double FirstWidth => _blocks.Count > 0 ? _blocks[0].Plate.Width : 0;
        internal bool PeerSuppressed => _coordinatePeers && _eligible.Count(r => r != null) > VisibleBounds.Count;
        internal IReadOnlyList<Rect> VisibleBounds => _blocks.Where(b => b.Plate.Visibility == Visibility.Visible)
            .Select(b => new Rect(Canvas.GetLeft(b.Plate) + _offsetX, Canvas.GetTop(b.Plate) + _offsetY, b.Plate.Width, b.Plate.Height)).ToArray();
        public InlineOverlayWindow(AppConfig config, bool coordinatePeers = false, bool captionMode = false)
        {
            _captionMode = captionMode;
            _coordinatePeers = coordinatePeers;
            if (coordinatePeers) { Peers.Add(this); IsVisibleChanged += (_, _) => ReconcilePeers(); }
            _config = config; Content = _canvas;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
        }
        protected override void OnClosed(EventArgs e)
        { Peers.Remove(this); ReconcilePeers(); base.OnClosed(e); }
        private static void ReconcilePeers()
        {
            var occupied = new List<Rect>();
            // Earlier regions retain ownership. Hidden/closed peers immediately release
            // their space; suppressed rows are kept so they can reappear without an API call.
            foreach (var peer in Peers)
            {
                if (!peer.IsVisible) continue;
                double scale = peer._fontScale > 0 ? peer._fontScale : 1;
                for (int i = 0; i < peer._blocks.Count; i++)
                {
                    Rect? local = i < peer._eligible.Length ? peer._eligible[i] : null;
                    if (local == null) { peer._blocks[i].Plate.Visibility = Visibility.Collapsed; continue; }
                    var r = local.Value;
                    var physical = new Rect(peer._presentedRegion.X + r.X * scale, peer._presentedRegion.Y + r.Y * scale, r.Width * scale, r.Height * scale);
                    bool conflict = occupied.Any(other => Rect.Intersect(other, physical) is var overlap && !overlap.IsEmpty && overlap.Width > 0 && overlap.Height > 0);
                    peer._blocks[i].Plate.Visibility = conflict ? Visibility.Collapsed : Visibility.Visible;
                    if (!conflict) occupied.Add(physical);
                }
            }
        }
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle,
                NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW);
            CaptureSafe = WindowUtilities.ExcludeFromCapture(this);
        }
        public bool Present(SessionFrame frame)
        {
            bool result = PresentCore(frame);
            _sourceFrame = result && IsVisible ? frame : null;
            return result;
        }
        private bool PresentCore(SessionFrame frame)
        {
            _singleCaptionLayout = _captionMode && !frame.Fullscreen;
            TextClipped = false;
            if (frame.Anchors is { Count: 0 }) { Hide(); return true; }
            if (string.IsNullOrWhiteSpace(frame.Text)) { Hide(); return true; }
            if (!frame.Ocr.HasLayout || frame.Ocr.Lines.Count == 0) { Hide(); return false; }
            var layout = frame.Anchors == null ? frame.Ocr : new OcrOutcome { Lines = frame.Anchors.Select(a => a.Source).ToArray() };
            string[] translations = frame.Anchors?.Select(a => a.Text).ToArray() ?? frame.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int sources = layout.Lines.Count;
            bool paragraph = false;
            bool caption = false;
            if (_singleCaptionLayout)
            {
                // A continuous subtitle region is ONE paragraph. There is deliberately
                // no distance, baseline, punctuation or fragment-count gate here.
                var rows = frame.Ocr.Lines;
                int l = rows.Min(r => r.X), t = rows.Min(r => r.Y);
                int r = rows.Max(r => r.X + r.Width), b = rows.Max(r => r.Y + r.Height);
                _captionTextHeight = rows.Select(row => row.Height).Where(h => h > 0).Order().DefaultIfEmpty(20).ElementAt(rows.Count(row => row.Height > 0) / 2);
                layout = new OcrOutcome { LanguageTag = frame.Ocr.LanguageTag, Lines = [new OcrLine { Text = frame.Ocr.PlainText, X = l, Y = t, Width = r - l, Height = b - t }] };
                translations = [frame.Anchors == null ? frame.Text.Replace('\r', ' ').Replace('\n', ' ') : JoinCaption(frame.Anchors.Select(a => a.Text))];
                sources = 1; paragraph = caption = true;
            }
            else if (!frame.Fullscreen && frame.Ocr.Lines.Count > 1 && (_config.MovieMode || frame.Region.Width >= frame.Region.Height * 3))
            {
                var sentence = OcrLayout.Normalize(frame.Ocr.Lines, captionRows: true);
                if (sentence.Count == 1)
                {
                    // Also coalesce already anchored/cached fragments at presentation.
                    // This prevents partial stream or old row mapping from recreating
                    // separate backing plates after source-side grouping.
                    layout = new OcrOutcome { Lines = sentence, LanguageTag = frame.Ocr.LanguageTag };
                    translations = [string.Join(" ", translations.Select(t => t.Replace('\n', ' ').Replace('\r', ' ')))];
                    sources = 1; paragraph = caption = true;
                }
            }
            if (frame.Anchors == null && frame.Final && sources > 1 && translations.Length != sources)
            {
                if (!TryParagraph(layout, out var grouped)) { Hide(); return false; }
                layout = grouped; sources = 1; translations = [frame.Text]; paragraph = true;
            }
            if (frame.Fullscreen && !frame.Final) return true;
            int count = frame.Anchors != null ? sources : sources == 1 ? 1 : frame.Final ? translations.Length : Math.Min(sources, translations.Length);
            // The current partial row is useful immediately; final row-count validation remains authoritative.
            if (count == 0) return true;
            bool entering = !IsVisible;
            if (!IsVisible) Show();
            if (!CaptureSafe) { Hide(); return false; }
            if (_presentedRegion != frame.Region || entering) WindowUtilities.Position(this, frame.Region);
            _presentedRegion = frame.Region;
            double scale = NativeMethods.ScaleForDpi(NativeMethods.GetDpiForWindow(new WindowInteropHelper(this).Handle));
            var screen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(frame.Region.X, frame.Region.Y, frame.Region.Width, frame.Region.Height)).Bounds;
            UpdateFonts(layout, scale, paragraph ? layout : frame.Ocr);
            EnsureBlocks(count);
            _eligible = new Rect?[count];

            var occupied = new List<Rect>();

            for (int i = 0; i < count; i++)
            {
                var line = layout.Lines[i];
                double size = _fonts[i];
                double x, y;
                double left = (screen.Left - frame.Region.X) / scale, top = (screen.Top - frame.Region.Y) / scale;
                double right = (screen.Right - frame.Region.X) / scale, bottom = (screen.Bottom - frame.Region.Y) / scale;
                double centerX = (line.X + line.Width / 2d) / scale;
                double centerY = (line.Y + line.Height / 2d) / scale;
                if (_singleCaptionLayout)
                {
                    var stable = _captionStability.Observe(frame.Ocr, frame.Region, scale, size, centerX, centerY);
                    size = stable.Font; centerX = stable.X; centerY = stable.Y;
                }
                foreach (var other in paragraph ? layout.Lines : frame.Ocr.Lines)
                {
                    if (ReferenceEquals(other, line)) continue;
                    double otherX = (other.X + other.Width / 2d) / scale, otherY = (other.Y + other.Height / 2d) / scale;
                    bool sameBand = Math.Abs(centerY - otherY) < Math.Min(line.Height, other.Height) / scale * .45;
                    if (sameBand)
                    {
                        if (otherX > centerX) right = Math.Min(right, (centerX + otherX) / 2);
                        else if (otherX < centerX) left = Math.Max(left, (centerX + otherX) / 2);
                    }
                    else if (other.X < line.X + line.Width && other.X + other.Width > line.X)
                    {
                        if (otherY > centerY) bottom = Math.Min(bottom, (centerY + otherY) / 2);
                        else if (otherY < centerY) top = Math.Max(top, (centerY + otherY) / 2);
                    }
                }
                string text = caption ? translations[0] : frame.Anchors == null && sources == 1 ? frame.Text : translations[i];
                var measure = _blocks[i].Text;
                measure.FontSize = size; measure.Text = text; measure.Width = double.NaN;
                measure.MaxHeight = double.PositiveInfinity; measure.TextWrapping = TextWrapping.NoWrap;
                measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double limit = Math.Max(1, right - left);
                if (_singleCaptionLayout)
                {
                    double centeredRoom = Math.Min(centerX - left, right - centerX) * 2;
                    if (centeredRoom >= Math.Max(240, line.Width / scale + 6)) limit = Math.Min(limit, centeredRoom);
                }
                double width = Math.Min(limit, Math.Max(line.Width / scale + 6, measure.DesiredSize.Width + 6));
                x = Math.Clamp(centerX - width / 2, left, Math.Max(left, right - width));
                double height = Math.Max(1, bottom - top);
                measure.LineHeight = Math.Ceiling(size * 1.25);
                measure.TextWrapping = TextWrapping.Wrap; measure.Width = Math.Max(1, width - 6);
                measure.Measure(new Size(Math.Max(1, width - 6), double.PositiveInfinity));
                double naturalHeight = Math.Ceiling(measure.DesiredSize.Height) + 4;
                double preferredY = _singleCaptionLayout ? centerY - Math.Ceiling(size * 1.25) / 2 - 2 : centerY - naturalHeight / 2;
                y = Math.Clamp(preferredY, top, Math.Max(top, bottom - Math.Min(height, naturalHeight)));
                SetText(i, text, x, y, width, height, size);
                var plate = _blocks[i].Plate;
                var bounds = new Rect(x, y, plate.Width, plate.Height);
                // Last-resort collision guard for ambiguous Paddle boxes. Never paint
                // two target strings on top of each other, even for cross-fading sources.
                bool collision = height + .01 < Math.Ceiling(size * 1.25) || occupied.Any(r => Rect.Intersect(r, bounds) is var overlap && !overlap.IsEmpty && overlap.Width > 0 && overlap.Height > 0);
                plate.Visibility = collision ? Visibility.Collapsed : Visibility.Visible;
                _eligible[i] = collision ? null : bounds;
                if (collision) TextClipped = true; else occupied.Add(bounds);
            }

            FitDisplay(frame.Region, scale, screen);
            if (_coordinatePeers) ReconcilePeers();
            if (entering) OverlayMotion.Reveal(_canvas, subtitle: true);
            return true;
        }
        private void FitDisplay(Int32Rect source, double scale, System.Drawing.Rectangle screen)
        {
            Rect union = Rect.Empty;
            foreach (var r in _eligible) if (r != null) union.Union(r.Value);
            if (union.IsEmpty) union = new Rect(0, 0, 1, 1);
            int left = Math.Max(screen.Left, source.X + (int)Math.Floor(union.Left * scale) - 2);
            int top = Math.Max(screen.Top, source.Y + (int)Math.Floor(union.Top * scale) - 2);
            int right = Math.Min(screen.Right, source.X + (int)Math.Ceiling(union.Right * scale) + 2);
            int bottom = Math.Min(screen.Bottom, source.Y + (int)Math.Ceiling(union.Bottom * scale) + 2);
            var display = new Int32Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
            bool changed = display != _displayRegion || !NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var actual) ||
                actual.Left != display.X || actual.Top != display.Y || actual.Width != display.Width || actual.Height != display.Height;
            _displayRegion = display;
            _offsetX = (left - source.X) / scale; _offsetY = (top - source.Y) / scale;
            for (int i = 0; i < _blocks.Count; i++)
            {
                Canvas.SetLeft(_blocks[i].Plate, Canvas.GetLeft(_blocks[i].Plate) - _offsetX);
                Canvas.SetTop(_blocks[i].Plate, Canvas.GetTop(_blocks[i].Plate) - _offsetY);
            }
            if (changed)
            {
                Width = _displayRegion.Width / scale; Height = _displayRegion.Height / scale;
                WindowUtilities.Position(this, _displayRegion);
                UpdateLayout();
            }
        }
        private static bool TryParagraph(OcrOutcome source, out OcrOutcome grouped)
        {
            grouped = source;
            if (source.Lines.Count is < 2 or > 8) return false;
            var rows = source.Lines.OrderBy(l => l.Y).ToArray();
            int height = rows.Select(l => l.Height).Order().ElementAt(rows.Length / 2);
            if (height < 5) return false;
            for (int i = 1; i < rows.Length; i++)
            {
                var a = rows[i - 1]; var b = rows[i];
                bool aligned = Math.Abs(a.X - b.X) <= height * 3 || Math.Abs((a.X + a.Width / 2) - (b.X + b.Width / 2)) <= height * 3;
                if (!aligned || b.Y <= a.Y + height * .4 || b.Y - (a.Y + a.Height) > height * 1.8) return false;
            }
            int left = rows.Min(l => l.X), right = rows.Max(l => l.X + l.Width);
            grouped = new OcrOutcome { Lines = [new OcrLine { Text = source.PlainText, X = left, Y = rows[0].Y, Width = right - left, Height = height }], LanguageTag = source.LanguageTag };
            return true;
        }
        private static string JoinCaption(IEnumerable<string> parts)
        {
            var output = new System.Text.StringBuilder();
            bool Cjk(char c) => c is >= '\u3000' and <= '\u9fff' or >= '\uac00' and <= '\ud7af' or >= '\uff00' and <= '\uffef';
            foreach (string part in parts)
            {
                string text = part.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (text.Length == 0) continue;
                if (output.Length > 0 && !Cjk(output[^1]) && !Cjk(text[0]) && !char.IsPunctuation(text[0])) output.Append(' ');
                output.Append(text);
            }
            return output.ToString();
        }
        internal void FollowRegion(Int32Rect region, bool visible)
        {
            if (!visible) { OverlayMotion.Settle(_canvas); Hide(); return; }
            if (_sourceFrame is not { } frame) return;
            if (IsVisible && region == _presentedRegion) return;
            var targetScreen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height));
            var movedDisplay = new System.Drawing.Rectangle(_displayRegion.X + region.X - _presentedRegion.X, _displayRegion.Y + region.Y - _presentedRegion.Y, _displayRegion.Width, _displayRegion.Height);
            if (!IsVisible || region.Width != _presentedRegion.Width || region.Height != _presentedRegion.Height ||
                !targetScreen.Bounds.Contains(movedDisplay) || targetScreen.DeviceName != System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).DeviceName)
            {
                double sx = (double)region.Width / Math.Max(1, frame.Region.Width), sy = (double)region.Height / Math.Max(1, frame.Region.Height);
                OcrLine Project(OcrLine l) => new() { Text = l.Text, X = (int)Math.Round(l.X * sx), Y = (int)Math.Round(l.Y * sy),
                    Width = Math.Max(1, (int)Math.Round(l.Width * sx)), Height = Math.Max(1, (int)Math.Round(l.Height * sy)), Confidence = l.Confidence };
                var layout = new OcrOutcome { Lines = frame.Ocr.Lines.Select(Project).ToArray(), HasLayout = frame.Ocr.HasLayout, LanguageTag = frame.Ocr.LanguageTag };
                PresentCore(frame with { Region = region, Ocr = layout, Anchors = frame.Anchors?.Select(a => new AnchoredTranslation(Project(a.Source), a.Text)).ToArray() });
                return;
            }
            _displayRegion = new Int32Rect(_displayRegion.X + region.X - _presentedRegion.X, _displayRegion.Y + region.Y - _presentedRegion.Y,
                _displayRegion.Width, _displayRegion.Height);
            WindowUtilities.Position(this, _displayRegion);
            _presentedRegion = region;
            if (_coordinatePeers) ReconcilePeers();
        }
        internal void Clear() { _sourceFrame = null; OverlayMotion.Settle(_canvas); Hide(); }
        private double FontFor(OcrLine line, OcrOutcome neighbors, double scale)
        {
            if (_singleCaptionLayout && _captionTextHeight > 0)
                return _config.MovieMode ? Math.Clamp(Math.Round(_captionTextHeight / scale), 8, 72) : Math.Clamp(Math.Round(_captionTextHeight / scale * .82), 10, Math.Clamp(_config.OverlayFontSize, 12, 40));
            if (_config.MovieMode && neighbors.Lines.Count == 1) return Math.Clamp(Math.Round(line.Height / scale), 8, 72);
            double maximum = _config.MovieMode ? 72 : Math.Clamp(_config.OverlayFontSize, 12, 40);
            double size = Math.Clamp(Math.Round(line.Height / scale * .82), 10, maximum);
            foreach (var other in neighbors.Lines)
            {
                double distance = Math.Abs(other.Y + other.Height / 2d - line.Y - line.Height / 2d);
                if (distance < Math.Min(line.Height, other.Height) * .45 || other.X >= line.X + line.Width || other.X + other.Width <= line.X) continue;
                size = Math.Min(size, Math.Max(10, Math.Floor(distance / scale / 1.25)));
            }
            return size;
        }
        private void UpdateFonts(OcrOutcome layout, double scale, OcrOutcome neighbors)
        {
            if (ReferenceEquals(_fontLayout, layout) && Math.Abs(_fontScale - scale) < 0.001) return;
            double maximum = _config.MovieMode ? 72 : Math.Clamp(_config.OverlayFontSize, 12, 40);
            var next = new List<(double X, double Y, double Height, double Size)>();
            _fonts = new double[layout.Lines.Count];
            for (int i = 0; i < layout.Lines.Count; i++)
            {
                var line = layout.Lines[i];
                double height = Math.Max(1, line.Height / scale), x = line.X / scale, y = line.Y / scale;
                double size = FontFor(line, neighbors, scale);
                var previous = _previousFonts.FirstOrDefault(p => Math.Abs(p.X - x) < 8 && Math.Abs(p.Y - y) < 8 && Math.Abs(p.Height - height) < p.Height * 0.22);
                if (!_singleCaptionLayout && previous.Size > 0) size = Math.Min(size, Math.Clamp(previous.Size, _config.MovieMode ? 8 : 10, maximum));
                _fonts[i] = size; next.Add((x, y, height, size));
            }
            _fontLayout = layout; _fontScale = scale; _previousFonts = next;
        }
        private void EnsureBlocks(int count)
        {
            if (_blocks.Count == count) return;
            _canvas.Children.Clear(); _blocks.Clear();
            for (int i = 0; i < count; i++)
            {
                var text = new TextBlock
                {
                    Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                    Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 2, ShadowDepth = 1, Opacity = 0.8 },
                    TextTrimming = TextTrimming.None,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight
                };
                TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);
                var plate = new Border { Child = text, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                    Padding = new Thickness(3, 2, 3, 2), ClipToBounds = true };
                _blocks.Add((plate, text)); _canvas.Children.Add(plate);
            }
        }
        private void SetText(int index, string text, double x, double y, double width, double height, double size)
        {
            var (plate, body) = _blocks[index];
            double paddingY = Math.Clamp((height - Math.Ceiling(size * 1.25)) / 2, 0, 2);
            plate.Padding = new Thickness(3, paddingY, 3, paddingY);
            double contentWidth = Math.Max(1, width - 6), available = Math.Max(1, height - paddingY * 2);
            body.Text = text; body.FontSize = size; body.LineHeight = Math.Ceiling(size * 1.25);
            body.TextWrapping = TextWrapping.Wrap; body.Width = contentWidth; body.MaxHeight = double.PositiveInfinity;
            body.Measure(new Size(contentWidth, double.PositiveInfinity));
            TextClipped |= body.DesiredSize.Height > available;
            body.MaxHeight = available; plate.Width = Math.Max(1, width);
            plate.Height = Math.Min(Math.Max(1, height), Math.Ceiling(body.DesiredSize.Height) + paddingY * 2);
            Color background = OverlayAppearance.Background(_config);
            bool darkText = OverlayAppearance.UseDarkText(background);
            body.Foreground = darkText ? new SolidColorBrush(Color.FromRgb(16, 24, 32)) : Brushes.White;
            if (body.Effect is DropShadowEffect shadow) shadow.Color = darkText ? Colors.White : Colors.Black;
            plate.Background = new SolidColorBrush(background);
            Canvas.SetLeft(plate, x); Canvas.SetTop(plate, y);
        }
    }
}
