using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using GlassLingo.Config;
using GlassLingo.Native;
using GlassLingo.Sessions;
using GlassLingo.Translate;

namespace GlassLingo.Overlay
{
    internal sealed class TranslationPresenter : IDisposable
    {
        private readonly AppConfig _cfg;
        private readonly bool _watch;
        private InlineOverlayWindow? _inline;
        private ResultOverlayWindow? _popup;
        private SessionFrame? _last;
        private long _popupVersion = -1;
        private long _contentVersion = -1;
        private bool _hidden, _closing;
        private bool _disposed;
        private long _overflowNotice = -1;
        private int _peerCount = 1;
        private readonly StreamTextMotion _flow;
        private SessionFrame? _streamFrame;
        public string Mode { get; private set; }
        public event Action<string>? Notice;
        public event Action? Dismissed;
        public TranslationPresenter(AppConfig config, string mode, bool watch)
        {
            _cfg = config; Mode = mode; _watch = watch;
            _flow = new StreamTextMotion((text, complete) =>
            {
                if (_streamFrame is not { } current) return;
                var rows = text.Split('\n');
                var anchors = current.Anchors?.Take(rows.Length).Select((a, i) => new AnchoredTranslation(a.Source, rows[i]))
                    .Where(a => a.Text.Length > 0).ToArray();
                Render(complete ? current : current with { Text = text, Final = false, Anchors = anchors, Translation = null });
                if (complete) _streamFrame = null;
            }, config.MovieMode ? 64 : 80);
        }
        public void SetMode(string mode)
        {
            if (_disposed) return;
            if (Mode == mode) return;
            var pending = _streamFrame ?? _last;
            _flow.Reset(); _streamFrame = null;
            CloseWindows(); Mode = mode;
            if (pending != null) Present(pending);
        }
        public void ToggleVisibility()
        {
            if (_disposed) return;
            _hidden = !_hidden;
            if (_hidden) { _inline?.Hide(); _popup?.Hide(); }
            else if (_last != null) Present(_last);
        }
        internal bool PopupVisible => _popup?.IsVisible == true;
        internal bool InlineVisible => _inline?.IsVisible == true;
        internal System.Collections.Generic.IReadOnlyList<Rect> InlineBounds => _inline?.VisibleBounds ?? [];
        internal bool IsHidden => _hidden;
        internal void SetPeerCount(int count)
        {
            if (_peerCount == count) return; _peerCount = count;
            if (_watch && count > 1 && _popup?.IsVisible == true && _last != null && Mode != "popup") _popup.Reposition(_last.Region, App.ScaleFactor);
        }
        internal void FollowRegion(Int32Rect region, bool visible)
        { if (_cfg.OverlayFollowSource && Mode == "inline") _inline?.FollowRegion(region, visible && !_hidden); }
        private void EnsurePopup()
        {
            if (_popup != null) return;
                _popup = new ResultOverlayWindow { ActivateOnShow = !_watch, ShowActivated = !_watch, Appearance = _cfg };
                _popup.TintChanged += value => _cfg.PopupTintOpacity = value;
                _popup.TintCommitted += () => _cfg.Save();
                _popup.Closed += (_, _) => { _popup = null; _popupVersion = -1; if (!_closing) Dismissed?.Invoke(); };
        }
        internal void Begin(Int32Rect region, bool fullscreen)
        {
            if (_disposed) return;
            if (Mode == "inline" || _hidden) return;
            EnsurePopup(); _popup!.ShowPending(region, App.ScaleFactor);
            if (Mode == "sidebar") PlaceSidebar(region);
        }
        private void PlaceSidebar(Int32Rect region)
        {
            if (_watch && _peerCount > 1) return; // Multi-region fallbacks stay next to their own source, not stacked in one sidebar slot.
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(region.X, region.Y)).WorkingArea;
            var hwnd = new WindowInteropHelper(_popup!).EnsureHandle();
            if (System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea != screen)
                NativeMethods.SetWindowPos(hwnd, new IntPtr(-1), screen.Left, screen.Top, 0, 0, 0x51);
            double scale = NativeMethods.ScaleForDpi(NativeMethods.GetDpiForWindow(hwnd));
            WindowUtilities.Position(_popup!, new Int32Rect(screen.Right - (int)(440 * scale) - 16,
                screen.Top + 24, (int)(440 * scale), Math.Min(screen.Height - 48, (int)(620 * scale))));
        }
        internal void Fail(string message)
        {
            if (_popup != null && !_hidden) _popup.SetFailure(message);
            Notice?.Invoke(message);
        }
        public void Present(SessionFrame frame)
        {
            if (_disposed) return;
            bool stream = !frame.Final && frame.Text.Length > 0;
            bool finishing = frame.Final && _streamFrame is { } previous && previous.Version == frame.Version &&
                Sessions.WatchTextStabilizer.Canonical(previous.Ocr.PlainText) == Sessions.WatchTextStabilizer.Canonical(frame.Ocr.PlainText) &&
                (frame.Anchors == null || frame.Anchors.Count == frame.Ocr.Lines.Count);
            if ((_cfg.EnableStreaming || _cfg.MovieMode) && (stream || finishing))
            {
                if (_streamFrame?.Version != frame.Version) _flow.Reset();
                _streamFrame = frame; _flow.Push(frame.Text, frame.Final); return;
            }
            _flow.Reset(); _streamFrame = null; Render(frame);
        }
        private void Render(SessionFrame frame)
        {
            if (!_cfg.OverlayFollowSource && _last != null && frame.Version == _last.Version)
                frame = frame with { Ocr = _last.Ocr, Region = _last.Region };
            // Keep the last readable subtitle while a confirmed new sentence is being translated.
            // Empty OCR (confirmed disappearance) still clears the surface immediately.
            if (_watch && (frame.Anchors == null || frame.Anchors.Count == 0) && !frame.Ocr.IsEmpty && string.IsNullOrWhiteSpace(frame.Text) && _last is { Text.Length: > 0 }) return;
            _last = frame;
            if (_hidden) return;
            if (frame.Ocr.IsEmpty)
            {
                _inline?.Clear();
                if (!_watch && Mode != "inline") { EnsurePopup(); _popup!.ShowPending(frame.Region, App.ScaleFactor); _popup.SetFailure(Localization.UiText.T("这块区域没认到文字。可能是：区域太小、字太淡、或者不是文字。")); }
                else _popup?.Hide();
                return;
            }
            if (Mode == "inline")
            {
                _inline ??= new InlineOverlayWindow(_cfg, coordinatePeers: true, captionMode: _watch);
                if (_inline.Present(frame))
                {
                    if (_inline.PeerSuppressed && _overflowNotice != frame.Version)
                    { _overflowNotice = frame.Version; Notice?.Invoke(Localization.UiText.T("区域重叠，部分译文已避让；隐藏前一区域可显示。")); }
                    if (_inline.TextClipped && _overflowNotice != frame.Version)
                    { _overflowNotice = frame.Version; Notice?.Invoke(GlassLingo.Localization.UiText.T("译文超出选区；可扩大区域或切换浮窗查看完整译文")); }
                    _popup?.Hide(); return;
                }
                if (_overflowNotice != frame.Version)
                {
                    _overflowNotice = frame.Version;
                    Notice?.Invoke(Localization.UiText.T(_inline.CaptureSafe ? "文字布局无法对应，已隐藏覆盖；可手动切换侧栏查看。" : "系统未能排除译文捕获，已隐藏覆盖；可手动切换浮窗查看。"));
                }
                _inline.Hide(); _popup?.Hide(); return;
            }
            _inline?.Hide();
            EnsurePopup();
            double scale = App.ScaleFactor;
            if (_popupVersion != frame.Version)
            {
                _popupVersion = frame.Version;
                _popup!.ShowResult(frame.Ocr, frame.Region, scale, frame.Ocr.LanguageTag, GlassLingo.Localization.UiText.T("正在翻译…"));
            }
            else if (!_popup!.IsVisible) _popup.Show();
            if (frame.Text.Length > 0)
            {
                if (frame.Final) _popup.UpdateTranslation(frame.Translation ?? new TranslateResult { Text = frame.Text, EngineName = GlassLingo.Localization.UiText.T("翻译完成") }, null);
                else _popup.SetStreamingText(frame.Text);
                if (_contentVersion != frame.Version) { _contentVersion = frame.Version; _popup.RevealTranslation(); }
            }
            if (Mode == "sidebar" || Mode == "inline")
            {
                PlaceSidebar(frame.Region);
            }
        }
        private void CloseWindows()
        {
            _closing = true;
            _popup?.Close(); _popup = null; _inline?.Close(); _inline = null; _popupVersion = _contentVersion = -1;
            _closing = false;
        }
        public void Dispose() { if (_disposed) return; _disposed = true; _flow.Dispose(); _streamFrame = null; _last = null; CloseWindows(); }
    }
}
