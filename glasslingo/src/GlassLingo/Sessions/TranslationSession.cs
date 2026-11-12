using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using GlassLingo.Capture;
using GlassLingo.Config;
using GlassLingo.Native;
using GlassLingo.Ocr;
using GlassLingo.Translate;

namespace GlassLingo.Sessions
{
    internal sealed record SessionFrame(OcrOutcome Ocr, Int32Rect Region, string Text, bool Final, bool Fullscreen, long Version, TranslateResult? Translation = null,
        IReadOnlyList<AnchoredTranslation>? Anchors = null);

    internal sealed class TranslationSession : IAsyncDisposable
    {
        private readonly AppConfig _cfg;
        private IOcrEngine _ocr;
        private readonly ITranslator? _translator;
        private readonly Func<Int32Rect, Bitmap> _capture;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Channel<SessionFrame> _latest = Channel.CreateBounded<SessionFrame>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        private readonly object _frameGate = new();
        private readonly object _requestLock = new();
        private CancellationTokenSource? _request;
        private Task? _run;
        private Task? _resumedFollow;
        private int _disposed;
        private long _version;
        private long _captureEpoch;
        private volatile bool _ocrFinished;
        private bool _sourceClosedReported;
        private volatile SessionFrame? _displayed;
        private sealed record LayoutSnapshot(OcrOutcome Ocr, Int32Rect Region);
        private volatile LayoutSnapshot? _layout;
        private volatile bool _paused;
        private readonly bool _watch, _fullscreen, _translate;
        private volatile bool _follow;
        private bool Monitor => _watch || _follow;
        private bool WholeCaption => _watch && _cfg.WatchDisplayMode == "inline" && _translator is IRowTranslator { SupportsRows: true };
        private readonly AnchoredTranslations _anchors = new();
        private readonly object _anchorGate = new(), _regionGate = new();
        private Int32Rect _region;
        private readonly IntPtr _target;
        internal IntPtr TargetWindow => _target;
        internal bool TranslationRequested => _translate;
        private readonly RECT _targetStart;
        private readonly Int32Rect _startRegion;
        private static readonly object CacheGate = new();
        private static readonly Dictionary<string, string> Cache = new();
        private static readonly Queue<string> CacheOrder = new();
        public event Action<SessionFrame>? Frame;
        public event Action<string>? Status;
        public event Action<string>? Error;
        internal event Action? OcrReleased;
        internal event Action? SourceClosed;
        internal bool NeedsOcr => !_ocrFinished;
        public bool IsPaused => _paused;
        public bool IsWatch => _watch;
        public bool IsFullscreen => _fullscreen;
        internal bool IsFollowing => Monitor && _cfg.OverlayFollowSource && !_ocrFinished;
        public Int32Rect Region { get { lock (_regionGate) return _region; } }
        public string LastState { get; private set; } = GlassLingo.Localization.UiText.T("准备中");
        public int OcrCalls { get; private set; }
        public int ApiCalls { get; private set; }
        private readonly Stopwatch _responseClock = Stopwatch.StartNew();
        private long _lastRecoveryAt = -3000;
        private volatile bool _translationFault;
        private long _firstOcrMs = -1, _firstTextMs = -1;
        public long FirstOcrMs => Interlocked.Read(ref _firstOcrMs);
        public long FirstTextMs => Interlocked.Read(ref _firstTextMs);
        public long CurrentVersion => Interlocked.Read(ref _version);
        public TranslationSession(AppConfig cfg, Int32Rect region, bool watch, bool fullscreen, bool translate, IntPtr target,
            IOcrEngine? engine = null, ITranslator? translator = null, Func<Int32Rect, Bitmap>? capture = null, bool follow = false)
        {
            _cfg = cfg; _region = _startRegion = region; _watch = watch; _fullscreen = fullscreen; _translate = translate;
            _follow = follow && !watch && cfg.OverlayFollowSource && cfg.OcrProvider != "openai-vision";
            _ocr = engine ?? cfg.CreateOcrEngine(); _translator = translator ?? cfg.CreateTranslator();
            _capture = capture ?? (r => ScreenCapture.CaptureRegion(r.X, r.Y, r.Width, r.Height));
            _target = target;
            NativeMethods.GetWindowRect(target, out _targetStart);
        }
        public void Start() => _run = Task.Run(RunAsync);
        internal void EnableFollowing(Func<IOcrEngine> acquireEngine)
        {
            if (_watch || _follow || !_cfg.OverlayFollowSource || _cfg.OcrProvider == "openai-vision" || _lifetime.IsCancellationRequested) return;
            _follow = true; _ocrFinished = false;
            if (_run != null) _resumedFollow = ResumeFollowingAsync(_run, acquireEngine);
        }
        private async Task ResumeFollowingAsync(Task previous, Func<IOcrEngine> acquireEngine)
        {
            IOcrEngine? resumed = null;
            try
            {
                await previous;
                _lifetime.Token.ThrowIfCancellationRequested();
                resumed = acquireEngine(); _ocr = resumed; _ocrFinished = false;
                await Task.Run(() => CaptureLoopAsync(true));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetStatus(Localization.UiText.T("识别失败：") + ex.Message); Error?.Invoke(LastState); }
            finally
            {
                _ocrFinished = true; (resumed as IDisposable)?.Dispose(); OcrReleased?.Invoke();
            }
        }
        public void PauseResume()
        {
            Interlocked.Increment(ref _captureEpoch);
            _paused = !_paused;
            if (_paused) { Invalidate(); Emit(new SessionFrame(new OcrOutcome(), _region, "", true, _fullscreen, _version)); }
            SetStatus(_paused ? GlassLingo.Localization.UiText.T("已暂停 · 再次按暂停快捷键恢复") : GlassLingo.Localization.UiText.T("正在恢复监测"));
        }
        private void SetStatus(string state) { LastState = state; Status?.Invoke(state); }
        private void Invalidate()
        {
            lock (_frameGate) Interlocked.Increment(ref _version);
            lock (_requestLock) _request?.Cancel();
        }
        private void Emit(SessionFrame frame, bool remember = false)
        {
            lock (_frameGate) EmitLocked(frame, remember);
        }
        private void EmitLocked(SessionFrame frame, bool remember)
        {
            if (_lifetime.IsCancellationRequested || frame.Version != Interlocked.Read(ref _version)) return;
            // Only a completed translation may teach the cache. A layout refresh or a
            // partial cache match is not evidence that old text translates new source rows.
            if (remember && !WholeCaption && _cfg.OverlayFollowSource && frame.Final && frame.Text.Length > 0)
                lock (_anchorGate) _anchors.Remember(frame.Ocr, frame.Text);
            var snapshot = _layout;
            var layout = snapshot?.Ocr;
            if (layout != null && !ReferenceEquals(layout, frame.Ocr) &&
                SameContent(layout, frame.Ocr)) frame = frame with { Ocr = layout, Region = snapshot!.Region,
                    Anchors = WholeCaption ? null : frame.Anchors?.Select(a => (Anchor: a, Index: frame.Ocr.Lines.ToList().IndexOf(a.Source)))
                        .Where(a => a.Index >= 0 && a.Index < layout.Lines.Count).Select(a => new AnchoredTranslation(layout.Lines[a.Index], a.Anchor.Text)).ToArray() };
            if (_cfg.OverlayFollowSource && Monitor && layout is { HasLayout: true } && !frame.Ocr.IsEmpty)
            {
                lock (_anchorGate)
                {
                    var matches = _anchors.Match(layout);
                    bool different = !SameContent(layout, frame.Ocr);
                    if (different || frame.Text.Length == 0 || (frame.Anchors == null && frame.Final && _anchors.TryAll(layout, out _)))
                        frame = frame with { Ocr = layout, Region = snapshot!.Region, Anchors = matches, Text = string.Join("\n", matches.Select(m => m.Text)) };
                    else if (frame.Anchors is { Count: > 0 } streamed && !frame.Final)
                    {
                        var merged = layout.Lines.Select(l => streamed.FirstOrDefault(a => ReferenceEquals(a.Source, l)) ??
                            matches.FirstOrDefault(a => ReferenceEquals(a.Source, l))).Where(a => a != null).Cast<AnchoredTranslation>().ToArray();
                        frame = frame with { Anchors = merged, Text = string.Join("\n", merged.Select(a => a.Text)) };
                    }
                }
            }
            if (frame.Text.Length > 0) Interlocked.CompareExchange(ref _firstTextMs, _responseClock.ElapsedMilliseconds, -1);
            _displayed = frame; Frame?.Invoke(frame);
        }
        internal static string RowKey(OcrOutcome ocr) => string.Join("\n", ocr.Lines.Select(l => WatchTextStabilizer.Canonical(l.Text)));
        private static bool SameRows(OcrOutcome a, OcrOutcome b) => a.LanguageTag == b.LanguageTag && RowKey(a) == RowKey(b);
        private bool SameContent(OcrOutcome a, OcrOutcome b) => WholeCaption
            ? a.LanguageTag == b.LanguageTag && WatchTextStabilizer.Canonical(a.PlainText) == WatchTextStabilizer.Canonical(b.PlainText) : SameRows(a, b);
        private async Task RunAsync()
        {
            var translation = TranslateLoopAsync();
            try { await CaptureLoopAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetStatus(GlassLingo.Localization.UiText.T("识别失败：") + ex.Message); Error?.Invoke(LastState); Logging.Log.Error(GlassLingo.Localization.UiText.T("会话识别失败"), ex); }
            finally
            {
                _latest.Writer.TryComplete(); _ocrFinished = true;
                (_ocr as IDisposable)?.Dispose(); OcrReleased?.Invoke();
            }
            try { await translation.ConfigureAwait(false); } catch (OperationCanceledException) { }
            if (!_watch) (_ocr as IDisposable)?.Dispose();
            if (!_watch && !_lifetime.IsCancellationRequested && !LastState.StartsWith(GlassLingo.Localization.UiText.T("识别失败"))) SetStatus(GlassLingo.Localization.UiText.T("本次翻译完成"));
        }
        internal bool RefreshPlacement() => !_lifetime.IsCancellationRequested && !_paused && TrackTarget(false);
        internal SessionFrame Reproject(SessionFrame frame)
        {
            if (!_cfg.OverlayFollowSource || _target == IntPtr.Zero || !Monitor) return frame;
            if (!RefreshPlacement()) return frame with { Ocr = new OcrOutcome(), Text = "", Anchors = [] };
            var region = Region;
            if (region == frame.Region) return frame;
            double sx = (double)region.Width / Math.Max(1, frame.Region.Width), sy = (double)region.Height / Math.Max(1, frame.Region.Height);
            OcrLine Project(OcrLine line) => new() { Text = line.Text, X = (int)Math.Round(line.X * sx), Y = (int)Math.Round(line.Y * sy),
                Width = (int)Math.Round(line.Width * sx), Height = (int)Math.Round(line.Height * sy), Confidence = line.Confidence };
            var ocr = Math.Abs(sx - 1) < .001 && Math.Abs(sy - 1) < .001 ? frame.Ocr :
                new OcrOutcome { Lines = frame.Ocr.Lines.Select(Project).ToArray(), HasLayout = frame.Ocr.HasLayout,
                    LanguageTag = frame.Ocr.LanguageTag, EngineName = frame.Ocr.EngineName, CharCount = frame.Ocr.CharCount, ElapsedMs = frame.Ocr.ElapsedMs };
            return frame with { Region = region, Ocr = ocr, Anchors = frame.Anchors?.Select(a => new AnchoredTranslation(Project(a.Source), a.Text)).ToArray() };
        }
        private bool TrackTarget(bool reportState = true)
        {
            if (_target == IntPtr.Zero || _targetStart.Width <= 0 || _targetStart.Height <= 0) return true;
            if (!NativeMethods.IsWindow(_target))
            {
                if (reportState)
                {
                    SetStatus(GlassLingo.Localization.UiText.T("目标窗口已关闭，请重新划定区域"));
                    if (!_sourceClosedReported) { _sourceClosedReported = true; SourceClosed?.Invoke(); }
                }
                return false;
            }
            if (NativeMethods.IsIconic(_target)) { if (reportState) SetStatus(GlassLingo.Localization.UiText.T("目标窗口已最小化 · 等待恢复")); return false; }
            if (!NativeMethods.GetWindowRect(_target, out var rect)) return false;
            double sx = (double)rect.Width / _targetStart.Width, sy = (double)rect.Height / _targetStart.Height;
            var next = new Int32Rect(rect.Left + (int)((_startRegion.X - _targetStart.Left) * sx),
                rect.Top + (int)((_startRegion.Y - _targetStart.Top) * sy),
                Math.Max(4, (int)(_startRegion.Width * sx)), Math.Max(4, (int)(_startRegion.Height * sy)));
            lock (_regionGate) _region = next;
            var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
            return desktop.Contains(new Rectangle(next.X, next.Y, next.Width, next.Height));
        }
        private async Task CaptureLoopAsync(bool trackingOnly = false)
        {
            byte[]? fingerprint = null;
            var stableText = new WatchTextStabilizer(_cfg.MovieMode);
            var clock = Stopwatch.StartNew();
            var lastOcr = Stopwatch.StartNew();
            bool first = true;
            bool fastAvailable = false;
            bool initialized = trackingOnly;
            long nextCheckAt = 0;
            int failures = 0;
            var ct = _lifetime.Token;
            do
            {
                ct.ThrowIfCancellationRequested();
                if (_paused || (Monitor && !TrackTarget()))
                {
                    fingerprint = null; stableText.Reset();
                    if (!_paused) { Invalidate(); Emit(new SessionFrame(new OcrOutcome(), _region, "", true, _fullscreen, _version)); }
                    await Task.Delay(500, ct).ConfigureAwait(false); continue;
                }
                bool fastCheck = stableText.NeedsConfirmation && fastAvailable && _cfg.OcrProvider != "openai-vision";
                if (Monitor && !first) await Task.Delay(fastCheck
                    ? stableText.ConfirmationDelayMs : (int)Math.Max(1, nextCheckAt - clock.ElapsedMilliseconds), ct).ConfigureAwait(false);
                if (_paused) continue;
                nextCheckAt = clock.ElapsedMilliseconds + (_cfg.MovieMode ? 50 : Math.Clamp(_cfg.PollIntervalMs, 250, 5000));
                var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
                var currentRegion = Region;
                var bounds = Rectangle.Intersect(new Rectangle(currentRegion.X, currentRegion.Y, currentRegion.Width, currentRegion.Height), desktop);
                if (bounds.Width < 4 || bounds.Height < 4) { SetStatus(GlassLingo.Localization.UiText.T("区域移出屏幕，等待返回")); await Task.Delay(500, ct).ConfigureAwait(false); continue; }
                var region = new Int32Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                try
                {
                long captureEpoch = Interlocked.Read(ref _captureEpoch);
                using var bmp = _capture(region);
                byte[] next = Fingerprint(bmp);
                bool changed = fingerprint == null || Difference(fingerprint, next) > (_cfg.MovieMode ? .0015 : .006);
                first = false;
                int interval = _cfg.OcrProvider == "openai-vision" ? 2500 : _cfg.MovieMode ? 50 : Math.Max(300, _cfg.OcrIntervalMs);
                if (fastCheck) interval = Math.Min(interval, stableText.ConfirmationDelayMs);
                if (_follow && _fullscreen) interval = Math.Max(interval, 1500);
                if (Monitor && ((!changed && !stableText.NeedsConfirmation && lastOcr.ElapsedMilliseconds < (_cfg.MovieMode ? 200 : 2000)) || (fingerprint != null && lastOcr.ElapsedMilliseconds < interval))) continue;
                fingerprint = next; lastOcr.Restart();
                if (!_watch || _displayed == null) SetStatus(GlassLingo.Localization.UiText.T("识别中"));
                OcrOutcome outcome = await RecognizeAsync(bmp, ct).ConfigureAwait(false); OcrCalls++;
                failures = 0;
                // At most one accelerated confirmation after each regular OCR. A typewriter
                // animation cannot keep the capture/OCR loop running at the fast interval.
                fastAvailable = !fastCheck;
                Interlocked.CompareExchange(ref _firstOcrMs, _responseClock.ElapsedMilliseconds, -1);
                ct.ThrowIfCancellationRequested();
                if (_paused || captureEpoch != Interlocked.Read(ref _captureEpoch)) { fingerprint = null; continue; }
                double trustThreshold = _cfg.MovieMode ? .9 : .95;
                bool trusted = outcome.Lines.Count > 0 && outcome.Lines.All(l => l.Confidence >= trustThreshold);
                string observed = WholeCaption ? WatchTextStabilizer.Canonical(outcome.PlainText) : outcome.PlainText;
                if (Monitor && !stableText.Observe(observed, clock.ElapsedMilliseconds, trusted))
                {
                    // Unconfirmed partial OCR must not replace the authoritative layout
                    // or steal an in-flight stream. Move only the exact same source rows.
                    if (!stableText.NeedsConfirmation && _displayed is { } displayed && SameContent(displayed.Ocr, outcome) &&
                        ((_watch && _cfg.WatchDisplayMode == "inline") || _follow || displayed.Region != region || GeometryChanged(displayed.Ocr, outcome)))
                    { _layout = new(outcome, region); Emit(displayed); }
                    if (!_translationFault) SetStatus(outcome.IsEmpty ? Localization.UiText.T("没有识别到可读文字，请调整区域或识别引擎。") : stableText.NeedsConfirmation ? GlassLingo.Localization.UiText.T("监测中 · 确认文字变化") : GlassLingo.Localization.UiText.T("监测中 · 文字未变")); continue;
                }
                _layout = new(outcome, region);
                if (_follow && initialized)
                {
                    IReadOnlyList<AnchoredTranslation> matches;
                    lock (_anchorGate) matches = _anchors.Match(outcome);
                    if (matches.Count == 0 && _request != null && _displayed is { Text.Length: > 0 } partial &&
                        WatchTextStabilizer.Canonical(partial.Ocr.PlainText) == WatchTextStabilizer.Canonical(outcome.PlainText))
                    { Emit(partial with { Ocr = outcome, Region = region }); continue; }
                    Emit(new SessionFrame(outcome, region, string.Join("\n", matches.Select(m => m.Text)), true, _fullscreen, _version, Anchors: matches));
                    SetStatus(Localization.UiText.T("跟随中 · 新内容请使用持续翻译"));
                    continue;
                }
                initialized = true;
                Invalidate();
                var frame = new SessionFrame(outcome, region, "", outcome.IsEmpty, _fullscreen, _version);
                Emit(frame);
                if (outcome.IsEmpty) { SetStatus(GlassLingo.Localization.UiText.T("监测中 · 等待文字")); continue; }
                _translationFault = false; _latest.Writer.TryWrite(frame);
                SetStatus(_translate || _cfg.AutoTranslate ? GlassLingo.Localization.UiText.T("翻译中") : GlassLingo.Localization.UiText.T("已识别文字"));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (Monitor)
                {
                    // A transient capture/worker failure must not complete the channel and
                    // silently end a long-running watch. Keep the last readable subtitle.
                    failures++;
                    SetStatus(Localization.UiText.T("识别失败：") + ex.Message);
                    if (failures == 1 || failures % 10 == 0) Logging.Log.Error("Continuous OCR will retry", ex);
                    await Task.Delay(Math.Min(5000, 500 * failures), ct).ConfigureAwait(false);
                    fingerprint = null;
                }
            } while (Monitor);
        }
        private static bool GeometryChanged(OcrOutcome before, OcrOutcome after)
        {
            if (before.HasLayout != after.HasLayout || before.Lines.Count != after.Lines.Count) return true;
            for (int i = 0; i < before.Lines.Count; i++)
            {
                var a = before.Lines[i]; var b = after.Lines[i];
                if (Math.Abs(a.X - b.X) > 4 || Math.Abs(a.Y - b.Y) > 4 || Math.Abs(a.Height - b.Height) > 4 || Math.Abs(a.Width - b.Width) > 8) return true;
            }
            return false;
        }
        private async Task<OcrOutcome> RecognizeAsync(Bitmap bmp, CancellationToken ct)
        {
            string lang = _cfg.SourceLanguage ?? _ocr.Languages.FirstOrDefault() ?? "en-US";
            if (!string.IsNullOrWhiteSpace(_cfg.SourceLanguage) && !_ocr.Supports(lang))
                throw new InvalidOperationException(GlassLingo.Localization.UiText.F("当前 OCR 不支持 {0}；Windows OCR 需安装对应语言包", lang));
            var lines = new List<OcrLine>();
            int tile = _cfg.OcrProvider == "openai-vision" ? Math.Max(64, Math.Max(bmp.Width, bmp.Height)) : 1536;
            var sw = Stopwatch.StartNew();
            for (int y = 0; y < Math.Max(1, bmp.Height - 48); y += tile - 48)
            for (int x = 0; x < Math.Max(1, bmp.Width - 48); x += tile - 48)
            {
                ct.ThrowIfCancellationRequested();
                using var part = bmp.Clone(new Rectangle(x, y, Math.Min(tile, bmp.Width - x), Math.Min(tile, bmp.Height - y)), PixelFormat.Format32bppArgb);
                var result = await RecognizeTileAsync(part, lang, ct).ConfigureAwait(false);
                if (_cfg.SourceLanguage == null && _ocr.UsesLanguageSpecificModels)
                {
                    var detection = LanguageDetector.Detect(result.PlainText, _ocr.Languages);
                    if (_ocr.Supports(detection.OcrLanguage) && !detection.OcrLanguage.Equals(lang, StringComparison.OrdinalIgnoreCase))
                        result = await _ocr.RecognizeAsync(part, detection.OcrLanguage, ct).ConfigureAwait(false);
                    else if (result.IsEmpty && _ocr.RetryAllLanguagesWhenEmpty)
                    {
                        foreach (string candidate in _ocr.Languages)
                        {
                            if (candidate == lang) continue;
                            result = await _ocr.RecognizeAsync(part, candidate, ct).ConfigureAwait(false);
                            if (!result.IsEmpty) break;
                        }
                    }
                }
                foreach (var line in result.Lines)
                {
                    if (_cfg.FilterNonText && !OcrNoiseFilter.IsLikelyText(line, result.HasLayout)) continue;
                    var translated = new OcrLine { Text = line.Text, X = x + line.X, Y = y + line.Y, Width = line.Width, Height = line.Height, Confidence = line.Confidence };
                    lines.Add(translated);
                }
            }
            // Keep OCR reading order within rows; tiles otherwise group text by tile rather than by line.
            // Vision OCR uses placeholder full-image boxes: spatial suppression would
            // incorrectly delete legitimate repeated/contained lines from its transcript.
            if (_cfg.OcrProvider != "openai-vision")
                lines = OcrLayout.Normalize(lines, !_fullscreen && (_watch || _follow || _cfg.MovieMode || bmp.Width >= bmp.Height * 3)).ToList();
            string text = string.Join("\n", lines.Select(l => l.Text));
            return new OcrOutcome { Lines = lines, CharCount = text.Length, EngineName = _ocr.Name,
                LanguageTag = _cfg.SourceLanguage ?? LanguageDetector.Detect(text, _ocr.Languages).TranslationLanguage,
                ElapsedMs = sw.ElapsedMilliseconds, HasLayout = _cfg.OcrProvider != "openai-vision" };
        }
        private async Task<OcrOutcome> RecognizeTileAsync(Bitmap bitmap, string language, CancellationToken ct)
        {
            var first = await _ocr.RecognizeAsync(bitmap, language, ct).ConfigureAwait(false);
            bool unreadable = first.IsEmpty || first.Lines.All(l => !OcrNoiseFilter.IsLikelyText(l, first.HasLayout));
            double threshold = double.IsFinite(_cfg.SmallTextRowHeight) ? Math.Clamp(_cfg.SmallTextRowHeight, 0, 200) : 24;
            bool small = first.Lines.Count > 0 && first.Lines.All(l => l.Height > 0 && l.Height < threshold);
            double clearThreshold = _cfg.MovieMode ? .85 : .95;
            if (!unreadable && first.Lines.All(l => l.Confidence >= clearThreshold)) return first;
            if (!_cfg.EnhanceSmallText || threshold == 0 || _cfg.OcrProvider == "openai-vision" || (!unreadable && !small) ||
                Environment.TickCount64 - _lastRecoveryAt < 2000 || bitmap.Width * 2 > 1536 || bitmap.Height * 2 > 1536 || (long)bitmap.Width * bitmap.Height * 4 > 1_500_000) return first;
            _lastRecoveryAt = Environment.TickCount64;
            using var enlarged = new Bitmap(bitmap.Width * 2, bitmap.Height * 2, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(enlarged))
            { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(bitmap, new Rectangle(0, 0, enlarged.Width, enlarged.Height)); }
            var retry = await _ocr.RecognizeAsync(enlarged, language, ct).ConfigureAwait(false);
            int Weight(OcrOutcome value) => value.Lines.Where(l => !_cfg.FilterNonText || OcrNoiseFilter.IsLikelyText(l, value.HasLayout)).Sum(l => l.Text.Length);
            if (Weight(retry) <= Weight(first)) return first;
            return new OcrOutcome { Lines = retry.Lines.Select(l => new OcrLine { Text = l.Text, X = l.X / 2, Y = l.Y / 2, Width = Math.Max(1, l.Width / 2), Height = Math.Max(1, l.Height / 2), Confidence = l.Confidence }).ToArray(),
                LanguageTag = retry.LanguageTag, EngineName = retry.EngineName, HasLayout = retry.HasLayout, CharCount = retry.CharCount, ElapsedMs = first.ElapsedMs + retry.ElapsedMs };
        }
        private async Task TranslateLoopAsync()
        {
            long retryVersion = -1; int retries = 0;
            await foreach (var frame in _latest.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (retryVersion != frame.Version) { retryVersion = frame.Version; retries = 0; }
                if (frame.Version != _version || _paused) continue;
                string anchored = "";
                bool allAnchored;
                lock (_anchorGate) allAnchored = !WholeCaption && _cfg.OverlayFollowSource && _anchors.TryAll(frame.Ocr, out anchored);
                if (allAnchored) { Emit(frame with { Text = anchored, Final = true }); continue; }
                if (!_translate && !_cfg.AutoTranslate) { Emit(frame with { Text = frame.Ocr.PlainText, Final = true }); continue; }
                if (_translator == null) { SetStatus(GlassLingo.Localization.UiText.T("翻译服务未配置，请打开设置")); continue; }
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                lock (_requestLock) _request = request;
                try
                {
                    if (frame.Version != CurrentVersion || _paused) continue;
                    string key = (WholeCaption ? "caption-v16|" : "subtitle-v13|") + _cfg.OcrProvider + "|" + _cfg.Translator.BaseUrl + "|" + _cfg.Translator.Model + "|" +
                        _cfg.TargetLanguage + "|" + frame.Ocr.LanguageTag + "|" + _cfg.Translator.SystemPrompt + "|" +
                        _cfg.Translator.ExplainTechnicalText + "|" + (WholeCaption ? WatchTextStabilizer.Canonical(frame.Ocr.PlainText) : RowKey(frame.Ocr));
                    string? cached; lock (CacheGate) Cache.TryGetValue(key, out cached);
                    if (cached != null) { Emit(frame with { Text = cached, Final = true, Translation = new TranslateResult { Text = cached, EngineName = "Cache" } }, true); SetStatus(GlassLingo.Localization.UiText.T("监测中 · 已复用译文")); continue; }
                    ApiCalls++;
                    bool captionRequest = WholeCaption;
                    string inputText = captionRequest ? string.Join(" ", frame.Ocr.Lines.Select(l => l.Text.Replace('\n', ' ').Replace('\r', ' '))) : frame.Ocr.PlainText;
                    var result = !captionRequest && frame.Ocr.HasLayout && frame.Ocr.Lines.Count > 1 && _translator is IRowTranslator { SupportsRows: true } rowTranslator
                        ? await rowTranslator.TranslateRowsAsync(frame.Ocr.Lines.Select(l => l.Text).ToArray(), frame.Ocr.LanguageTag,
                            _cfg.TargetLanguage, !(_cfg.EnableStreaming || _cfg.MovieMode) ? null : rows =>
                            {
                                if (rows.Count == 0) return;
                                var anchors = rows.Select(r => new AnchoredTranslation(frame.Ocr.Lines[r.Index], r.Text)).ToArray();
                                Emit(frame with { Text = string.Join("\n", rows.Select(r => r.Text)), Anchors = anchors });
                            }, request.Token).ConfigureAwait(false)
                        : (_cfg.EnableStreaming || _cfg.MovieMode)
                        ? await _translator.TranslateStreamingAsync(inputText, frame.Ocr.LanguageTag,
                            _cfg.TargetLanguage, value => Emit(frame with { Text = value }), request.Token).ConfigureAwait(false)
                        : await _translator.TranslateAsync(inputText, frame.Ocr.LanguageTag, _cfg.TargetLanguage, request.Token).ConfigureAwait(false);
                    request.Token.ThrowIfCancellationRequested();
                    lock (CacheGate)
                    {
                        if (!Cache.ContainsKey(key)) CacheOrder.Enqueue(key);
                        Cache[key] = result.Text;
                        while (Cache.Count > 128) Cache.Remove(CacheOrder.Dequeue());
                    }
                    Emit(frame with { Text = result.Text, Final = true, Translation = result }, true);
                    _translationFault = false;
                    SetStatus(_watch ? GlassLingo.Localization.UiText.F("监测中 · OCR {0} ms / 翻译 {1} ms", frame.Ocr.ElapsedMs, result.ElapsedMs) : GlassLingo.Localization.UiText.T("翻译完成"));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _translationFault = true;
                    Emit(frame with { Text = "", Final = false }); SetStatus(GlassLingo.Localization.UiText.T("翻译失败：") + ex.Message); Error?.Invoke(LastState); Logging.Log.Error(GlassLingo.Localization.UiText.T("会话翻译失败"), ex);
                    if (_watch && !request.IsCancellationRequested)
                    {
                        // Recover even after a longer outage. New source text interrupts the
                        // backoff immediately instead of waiting behind the failed sentence.
                        int delay = Math.Min(30000, 1000 << Math.Min(++retries, 5));
                        try
                        {
                            await Task.Delay(delay, request.Token).ConfigureAwait(false);
                            lock (_frameGate) if (frame.Version == _version && !_paused) _latest.Writer.TryWrite(frame);
                        }
                        catch (OperationCanceledException) { }
                    }
                }
                finally { lock (_requestLock) if (ReferenceEquals(_request, request)) _request = null; }
            }
        }
        public static byte[] Fingerprint(Bitmap bmp)
        {
            var bits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] value = new byte[96 * 32];
                for (int y = 0; y < 32; y++) for (int x = 0; x < 96; x++)
                {
                    IntPtr p = IntPtr.Add(bits.Scan0, (y * bmp.Height / 32) * bits.Stride + (x * bmp.Width / 96) * 4);
                    value[y * 96 + x] = (byte)((Marshal.ReadByte(p) + Marshal.ReadByte(p, 1) * 2 + Marshal.ReadByte(p, 2)) / 4);
                }
                return value;
            }
            finally { bmp.UnlockBits(bits); }
        }
        public static double Difference(byte[] a, byte[] b)
        { int count = 0; for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 20) count++; return (double)count / a.Length; }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel(); lock (_requestLock) _request?.Cancel();
            (_ocr as IDisposable)?.Dispose();
            if (_run != null) try { await _run.ConfigureAwait(false); } catch (OperationCanceledException) { }
            if (_resumedFollow != null) try { await _resumedFollow.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _lifetime.Dispose();
        }
    }
}
