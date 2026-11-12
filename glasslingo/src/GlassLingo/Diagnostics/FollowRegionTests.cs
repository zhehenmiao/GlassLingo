using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassLingo.Config;
using GlassLingo.Ocr;
using GlassLingo.Overlay;
using GlassLingo.Sessions;
using GlassLingo.Translate;

namespace GlassLingo.Diagnostics
{
    internal static partial class SelfTests
    {
        private static OcrOutcome Layout(params (string Text, int X, int Y)[] rows) => new()
        { Lines = rows.Select(r => new OcrLine { Text = r.Text, X = r.X, Y = r.Y, Width = 200, Height = 20 }).ToArray(), LanguageTag = "en-US" };
        private static Bitmap RegionSample(int value)
        { var bitmap = new Bitmap(400, 180); using var g = Graphics.FromImage(bitmap); g.Clear(value % 2 == 0 ? Color.White : Color.Black); return bitmap; }
        private static async Task TestFollowAndRegionsAsync()
        {
            var anchors = new AnchoredTranslations();
            anchors.Remember(Layout(("Open gate", 20, 20), ("Follow me", 20, 60)), "开门\n跟我来");
            var movedLayout = Layout(("Follow me", 80, 10), ("Open gate", 80, 45), ("New line", 80, 85));
            var mapped = anchors.Match(movedLayout);
            Check(mapped.Count == 2 && mapped[0].Text == "跟我来" && mapped[0].Source.Y == 10 && mapped[1].Text == "开门",
                "translated rows follow exact source identities after reorder/movement without attaching to a new line");
            var otherLanguage = new OcrOutcome { Lines = movedLayout.Lines, LanguageTag = "ja-JP" };
            Check(anchors.Match(otherLanguage).Count == 0, "source-language changes do not reuse an ambiguous same-spelling line");
            var inline = new InlineOverlayWindow(new AppConfig());
            Check(inline.Present(new SessionFrame(movedLayout, new Int32Rect(200, 200, 400, 180), "跟我来\n开门", false, false, 1, Anchors: mapped)) && inline.VisualCount == 2,
                "partial known-line overlays render at their own coordinates while a new line is untranslated");
            await SnapshotAsync(inline, "follow-reordered-lines"); inline.Close();

            int phase = 0, samples = 0;
            var translator = new RowTranslator();
            var cfg = new AppConfig { SourceLanguage = "en-US", PollIntervalMs = 300, OcrIntervalMs = 300 };
            cfg.Translator.Model = "follow-rows-v090";
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reordered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var partial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var session = new TranslationSession(cfg, new Int32Rect(0, 0, 400, 180), true, false, true, IntPtr.Zero,
                new LayoutEngine(() => phase == 0 ? Layout(("Open gate", 20, 20), ("Follow me", 20, 60)) : phase == 1 ? Layout(("Follow me", 80, 10), ("Open gate", 80, 45)) : movedLayout),
                translator, _ => RegionSample(Interlocked.Increment(ref samples))))
            {
                session.Frame += f =>
                {
                    if (f.Final && f.Text == "开门\n跟我来") ready.TrySetResult();
                    if (phase == 1 && f.Anchors?.FirstOrDefault()?.Source.X == 80) reordered.TrySetResult();
                    if (phase == 2 && f.Ocr.Lines.Count == 3 && f.Anchors?.Count == 2) partial.TrySetResult();
                    if (phase == 2 && f.Final && f.Text.Contains("新内容")) updated.TrySetResult();
                };
                session.Start(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                phase = 1; await reordered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(700);
                Check(session.ApiCalls == 1, "scroll/reorder of already translated lines makes no additional translation request");
                phase = 2; await partial.Task.WaitAsync(TimeSpan.FromSeconds(5)); await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(session.ApiCalls == 2, "continuous translation keeps moved known rows and translates newly confirmed content once");
            }
            phase = 0; samples = 0; var onceReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var onceMoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var onceHidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var onceReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cfg.Translator.Model = "follow-once-v090";
            await using (var once = new TranslationSession(cfg, new Int32Rect(0, 0, 400, 180), false, false, true, IntPtr.Zero,
                new LayoutEngine(() => Layout((phase == 2 ? "Other text" : "Open gate", phase == 0 ? 20 : 100, 20))), new RowTranslator(),
                _ => RegionSample(Interlocked.Increment(ref samples)), follow: true))
            {
                once.Frame += f =>
                {
                    if (f.Final && f.Text.Length > 0) onceReady.TrySetResult();
                    if (phase == 1 && f.Anchors?.FirstOrDefault()?.Source.X == 100) onceMoved.TrySetResult();
                    if (phase == 2 && f.Anchors is { Count: 0 }) onceHidden.TrySetResult();
                    if (phase == 3 && f.Anchors is { Count: 1 }) onceReturned.TrySetResult();
                };
                once.Start(); await onceReady.Task.WaitAsync(TimeSpan.FromSeconds(5)); phase = 1;
                await onceMoved.Task.WaitAsync(TimeSpan.FromSeconds(5)); phase = 2;
                await onceHidden.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(once.ApiCalls == 1, "one-shot inline capture follows existing translated text but never auto-translates replacement content");
                phase = 3; await onceReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(once.ApiCalls == 1, "a translated source returning into view reuses its anchored text without a new API call");
            }
            cfg.Translator.Model = "follow-switch-v090";
            int acquired = 0;
            var switchedReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var switchedMoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var switched = new TranslationSession(cfg, new Int32Rect(0, 0, 400, 180), false, false, true, IntPtr.Zero,
                new LayoutEngine(() => Layout(("Open gate", 20, 20))), new RowTranslator(),
                _ => RegionSample(Interlocked.Increment(ref samples))))
            {
                switched.Frame += f =>
                {
                    if (f.Final && f.Text == "开门") switchedReady.TrySetResult();
                    if (f.Anchors?.FirstOrDefault()?.Source.X == 120) switchedMoved.TrySetResult();
                };
                switched.Start(); await switchedReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(100);
                switched.EnableFollowing(() => { acquired++; return new LayoutEngine(() => Layout(("Open gate", 120, 20))); });
                await switchedMoved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                switched.EnableFollowing(() => throw new InvalidOperationException("duplicate engine"));
                Check(acquired == 1 && switched.ApiCalls == 1,
                    "switching a completed capture to inline resumes geometry tracking once without translating again");
            }
            await TestSharedOcrAsync();
            await TestRegionControlsAsync();
            await TestWindowPlacementAsync();
        }
        private static async Task TestSharedOcrAsync()
        {
            var engine = new LayoutEngine(() => Layout(("Shared", 20, 20))) { Delay = 100 };
            var pool = new SharedOcrPool(engine); var first = pool.Rent(); var second = pool.Rent();
            using var bitmap1 = RegionSample(0); using var bitmap2 = RegionSample(1);
            using var cancel = new CancellationTokenSource();
            var call1 = first.RecognizeAsync(bitmap1, "en-US", cancel.Token); await Task.Delay(20);
            var call2 = second.RecognizeAsync(bitmap2, "en-US", CancellationToken.None); cancel.Cancel();
            bool cancelled = false; try { await call1; } catch (OperationCanceledException) { cancelled = true; }
            await call2;
            Check(cancelled && engine.MaxActive == 1 && engine.Disposals == 0 && engine.Calls == 2,
                "shared OCR serializes region work and cancelling one region leaves the common engine usable");
            ((IDisposable)first).Dispose(); pool.Dispose();
            Check(engine.Disposals == 0, "an outstanding OCR lease survives owner release for an active capture");
            ((IDisposable)second).Dispose();
            Check(engine.Disposals == 1, "the final region/capture lease releases the common OCR engine exactly once");
        }
        private static async Task TestRegionControlsAsync()
        {
            var app = (App)Application.Current;
            var engine = new LayoutEngine(() => Layout(("Open gate", 20, 20)));
            using var pool = new SharedOcrPool(engine);
            for (int i = 0; i < 8; i++)
            {
                var session = new TranslationSession(new AppConfig { SourceLanguage = "en-US" }, new Int32Rect(50 + i * 10, 100, 400, 180), true, false, true,
                    IntPtr.Zero, pool.Rent(), new RowTranslator(), _ => RegionSample(0));
                var region = app.AttachWatchRegion(session, new TranslationPresenter(new AppConfig(), "inline", true));
                region.SetTranslation(i % 2 == 0 ? "打开城门" : "跟随原文移动"); region.SetStatus("监测中");
            }
            Check(app.WatchRegions.Count == 8, "eight independent watch regions coexist without replacing earlier selections");
            var first = app.WatchRegions[0]; var second = app.WatchRegions[1];
            app.ToggleRegionPause(first); app.ToggleRegionVisibility(second);
            Check(first.Session.IsPaused && !second.Session.IsPaused && second.Presenter.IsHidden && !first.Presenter.IsHidden,
                "pause and visibility controls affect only their own region");
            app.PauseResume(); Check(app.WatchRegions.All(r => r.Session.IsPaused), "global pause pauses all current regions consistently");
            app.PauseResume(); Check(app.WatchRegions.All(r => !r.Session.IsPaused), "global resume restores all region monitoring");
            foreach (var region in app.WatchRegions) { region.SetTranslation("已翻译的文字会跟随原文位置"); region.SetStatus("监测中"); }
            var shell = new Shell.ShellWindow(app); shell.Refresh(new AppConfig()); shell.SetRegionCount(8); shell.OpenRegions();
            await SnapshotAsync(shell, "region-manager-eight");
            Check(Find<Shell.RegionManagerView>(shell) != null, "region management is hosted in the main window instead of another dialog");
            app.SelectRegion(true); await Task.Delay(40);
            Check(app.WatchRegions.Count == 8 && !Application.Current.Windows.OfType<RegionSelectWindow>().Any(),
                "the region capacity guard preserves active regions and does not start an extra selection");
            await app.RemoveRegionAsync(first);
            Check(app.WatchRegions.Count == 7 && app.WatchRegions.Contains(second), "removing one region preserves the other seven");
            foreach (var panel in Application.Current.Windows.OfType<Shell.ShellWindow>()) panel.Hide();
            await app.StopAsync();
            Check(app.WatchRegions.Count == 0, "stop clears every region and its presenter");
        }
        private static async Task TestWindowPlacementAsync()
        {
            var source = new Window { WindowStyle = WindowStyle.None, Width = 500, Height = 300, ShowInTaskbar = false };
            source.Show(); var hwnd = new WindowInteropHelper(source).Handle;
            Native.WindowUtilities.Position(source, new Int32Rect(100, 100, 500, 300));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var original = new Int32Rect(140, 160, 300, 100);
            await using var session = new TranslationSession(new AppConfig { SourceLanguage = "en-US" }, original, true, false, true, hwnd,
                new LayoutEngine(() => Layout(("Open gate", 20, 20))), new RowTranslator(), _ => RegionSample(0));
            var frame = new SessionFrame(Layout(("Open gate", 20, 20)), original, "开门", true, false, 1);
            Native.WindowUtilities.Position(source, new Int32Rect(180, 155, 500, 300)); await Task.Delay(50);
            var projected = session.Reproject(frame);
            Check(projected.Region.X == original.X + 80 && projected.Region.Y == original.Y + 55 && projected.Ocr.Lines[0].X == 20,
                "moving a source window repositions existing translated geometry without capture or translation");
            source.Close();
            Check(!session.RefreshPlacement(), "a closed source window is never treated as a valid overlay anchor");
        }
        internal static void OpenRegionsPreview()
        {
            var app = (App)Application.Current;
            for (int i = 0; i < 3; i++)
            {
                var cfg = new AppConfig();
                var session = new TranslationSession(cfg, new Int32Rect(100 + i * 50, 200, 400, 180), true, false, true, IntPtr.Zero,
                    new LayoutEngine(() => Layout(("Open gate", 20, 20))), new RowTranslator(), _ => RegionSample(0));
                var region = app.AttachWatchRegion(session, new TranslationPresenter(cfg, "inline", true));
                region.SetTranslation(new[] { "打开城门，继续前进。", "跟随原文位置，复用已有译文。", "下一个任务：抵达港口。" }[i]);
                region.SetStatus("监测中");
            }
            app.ShowPanel();
            var shell = Application.Current.Windows.OfType<Shell.ShellWindow>().First();
            shell.Title = "区域管理 · 界面验收"; shell.SetRegionCount(3); shell.OpenRegions();
        }
        private static async Task TestCompactInputAsync()
        {
            string copied = ""; var cfg = new AppConfig();
            var window = new InputTranslationWindow(cfg, _ => new InputTestTranslator(), value => copied = value);
            window.Reveal(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(window.IsCompact && window.Width <= 400 && window.Height <= 276 && ((FrameworkElement)window.FindName("ActionsRow")).Visibility == Visibility.Collapsed,
                "compact input defaults to a small window without extra action rows");
            ((TextBox)window.FindName("InputBox")).Text = "Hello"; await window.TranslateInputAsync();
            ((Button)window.FindName("CompactCopyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(copied == "你好，世界。", "compact input copy control uses the same translation clipboard path");
            await SnapshotAsync(window, "input-compact-result");
            window.ApplyMode(false, true);
            Check(!window.IsCompact && window.TranslationText == "你好，世界。" && !cfg.InputCompactMode,
                "switching to full input mode preserves text/result and remembers the preference");
            window.ApplyMode(true, true);
            Check(window.TranslationText == "你好，世界。" && cfg.InputCompactMode, "returning to compact mode preserves the translation");
            await TestWhiteRimAsync(window, "compact-input-rim"); window.Close();
        }
        private sealed class LayoutEngine(Func<OcrOutcome> layout) : IOcrEngine, IDisposable
        {
            public int Delay, Calls, MaxActive, Disposals; private int _active;
            public string Name => "layout-test";
            public IReadOnlyList<string> Languages => new[] { "en-US" };
            public bool RetryAllLanguagesWhenEmpty => false;
            public bool UsesLanguageSpecificModels => false;
            public bool Supports(string tag) => true;
            public async Task<OcrOutcome> RecognizeAsync(Bitmap bitmap, string tag, CancellationToken ct)
            {
                Calls++; int active = Interlocked.Increment(ref _active); MaxActive = Math.Max(MaxActive, active);
                try { if (Delay > 0) await Task.Delay(Delay, ct); return layout(); }
                finally { Interlocked.Decrement(ref _active); }
            }
            public void Dispose() => Disposals++;
        }
        private sealed class RowTranslator : ITranslator
        {
            public string Name => "row-test";
            public Task<TranslateResult> TranslateAsync(string text, string? from, string to, CancellationToken ct) => TranslateStreamingAsync(text, from, to, null, ct);
            public async Task<TranslateResult> TranslateStreamingAsync(string text, string? from, string to, Action<string>? onText, CancellationToken ct)
            {
                await Task.Delay(100, ct);
                string result = string.Join("\n", text.Split('\n').Select(row => row switch { "Open gate" => "开门", "Follow me" => "跟我来", "New line" => "新内容", _ => row }));
                onText?.Invoke(result); return new TranslateResult { Text = result };
            }
        }
    }
}
