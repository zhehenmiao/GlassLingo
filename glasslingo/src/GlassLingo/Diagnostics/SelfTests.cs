using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassLingo.Config;
using GlassLingo.Ocr;
using GlassLingo.Sessions;
using GlassLingo.Translate;

namespace GlassLingo.Diagnostics
{
    internal static partial class SelfTests
    {
        private static readonly List<object> Results = new();
        private static string Output => Path.Combine(AppContext.BaseDirectory, "diagnostics");
        private static void Check(bool passed, string name)
        { Results.Add(new { name, passed }); if (!passed) throw new InvalidOperationException("Check failed: " + name); }
        public static async Task RunAsync(bool paddle, bool soak = false, bool frameOnly = false)
        {
            Directory.CreateDirectory(Output);
            File.Delete(Path.Combine(AppContext.BaseDirectory, "self-test-error.txt"));
            try
            {
                if (frameOnly)
                {
                    var probe = new Shell.ShellWindow((App)Application.Current); probe.Refresh(new AppConfig());
                    await SnapshotAsync(probe, "frame-probe-content");
                    await NativeGlassSnapshotAsync(probe, "frame-probe");
                    await TestWhiteRimAsync(probe, "frame-probe");
                    await TestCornersAndFocusAsync(probe); await TestNoGhostCornersAsync(probe);
                    await TestDropDownMotionAsync();
                    return;
                }
                TestLocales();
                await TestResponsiveCaptionsAsync();
                await TestSingleRegionCaptionAsync();
                await TestCaptionRowsAsync();
                await TestSplitTailAsync();
                await TestExpandedOverlayAsync();
                await TestCenteredSubtitlesAsync();
                await TestMovieModeAsync();
                TestOptimizations();
                await TestAuditAsync();
                await TestContinuousAlignmentAsync();
                await TestWatchRepairsAsync();
                await TestNoiseAndOverlayAsync();
                await TestLiveInputAndMotionAsync();
                await TestFollowAndRegionsAsync();
                await TestCompactInputAsync();
                await TestInputTranslationAsync();
                await TestPlateAppearanceAsync();
                await TestShortcutCaptureAsync();
                await TestSmoothScrollAsync();
                await TestDropDownMotionAsync();
                await TestImmediatePopupAsync();
                await TestStreamAsync();
                await TestHttpAsync();
                Check(Hotkey.ShortcutParser.TryParse("Shift+Ctrl+T", out _, out _), "shortcut parser accepts reordered modifiers");
                Check(!Hotkey.ShortcutParser.TryParse("Escape", out _, out _), "unmodified game keys cannot be registered");
                var preferences = new AppConfig();
                DisplayPreferences.Set(preferences, "watch", "sidebar"); DisplayPreferences.Set(preferences, "full", "inline");
                Check(preferences.DisplayMode == "popup" && preferences.WatchDisplayMode == "sidebar" && preferences.FullscreenDisplayMode == "inline",
                    "display preferences preserve capture, fullscreen and watch independently");
                await TestSessionAsync();
                await TestMovingLayoutAsync();
                Localization.UiText.SetLanguage("en-US");
                await TestWatchNoiseAsync();
                Localization.UiText.SetLanguage("zh-CN");
                await TestFullscreenAsync();
                if (soak) { await TestStabilityAsync(); await TestContinuousChurnAsync(); }
                if (paddle)
                {
                    await TestMoviePaddleAsync();
                    await TestPaddleLayoutAsync();
                    using var bmp = JapaneseSample();
                    using var engine = new PaddleOcrEngine(2);
                    var clock = Stopwatch.StartNew();
                    var result = await engine.RecognizeAsync(bmp, "ja-JP", CancellationToken.None);
                    Check(result.PlainText.Contains("こんにちは"), "Paddle worker recognizes Japanese over raw BGRA pipe");
                    Results.Add(new { name = "Paddle cold initialization + OCR", ms = clock.ElapsedMilliseconds, text = result.PlainText });
                    var warm = Stopwatch.StartNew(); await engine.RecognizeAsync(bmp, "ja-JP", CancellationToken.None);
                    Results.Add(new { name = "Paddle warm OCR", ms = warm.ElapsedMilliseconds });
                    Results.Add(new { name = "Paddle worker working set after warm OCR", bytes = engine.WorkerWorkingSet });
                    using var preparedEngine = new PaddleOcrEngine(2);
                    var preparation = Stopwatch.StartNew(); await preparedEngine.WarmAsync(CancellationToken.None); preparation.Stop();
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await using (var preparedSession = new TranslationSession(new AppConfig { OcrProvider = "paddle", SourceLanguage = "ja-JP" },
                        new Int32Rect(0, 0, 800, 150), false, false, true, IntPtr.Zero, preparedEngine,
                        new FakeTranslator { FirstDelay = 100 }, _ => JapaneseSample()))
                    {
                        preparedSession.Frame += f => { if (f.Final && f.Text.Length > 0) ready.TrySetResult(); };
                        preparedSession.Start(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
                        Results.Add(new { name = "prepared Paddle selection-to-text (synthetic translator)", prepareMs = preparation.ElapsedMilliseconds,
                            firstOcrMs = preparedSession.FirstOcrMs, firstTextMs = preparedSession.FirstTextMs });
                        Check(preparedSession.FirstOcrMs >= 0 && preparedSession.FirstTextMs < 2500, "prepared Paddle reuses its worker instead of paying a second model initialization");
                    }
                    int worker = engine.WorkerId ?? 0;
                    using (var shared = new SharedOcrPool(engine))
                    {
                        var region1 = shared.Rent(); var region2 = shared.Rent();
                        await region1.RecognizeAsync(bmp, "ja-JP", CancellationToken.None);
                        int firstWorker = engine.WorkerId ?? 0;
                        var secondResult = await region2.RecognizeAsync(bmp, "ja-JP", CancellationToken.None);
                        Check(firstWorker == worker && engine.WorkerId == worker && secondResult.PlainText.Contains("こんにちは"),
                            "multiple real Paddle region leases reuse one model worker process");
                        ((IDisposable)region1).Dispose(); ((IDisposable)region2).Dispose();
                    }
                    engine.Dispose();
                    await Task.Delay(150);
                    bool exited;
                    try { using var process = Process.GetProcessById(worker); exited = process.HasExited; } catch (ArgumentException) { exited = true; }
                    Check(exited, "stopping Paddle engine terminates owned worker and releases its memory");
                }
                var cfg = new AppConfig { GlassTransparencyEnabled = true };
                var shell = new Shell.ShellWindow((App)Application.Current); shell.Refresh(cfg);
                await SnapshotAsync(shell, "control-panel");
                await NativeGlassSnapshotAsync(shell, "control-panel-native-glass");
                await TestLiveGlassAsync(shell);
                await TestCornersAndFocusAsync(shell);
                await TestNoGhostCornersAsync(shell);
                await TestWhiteRimAsync(shell, "main-window-rim");
                Check(!cfg.GlassEffects || shell.GlassActive || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast,
                    "native Acrylic is active on supported Windows, otherwise readable fallback is allowed");
                shell.Width = 840; await SnapshotAsync(shell, "control-panel-small");
                int windowsBeforeSettings = Application.Current.Windows.Count;
                var settings = shell.OpenSettings(cfg, true);
                Check(Application.Current.Windows.Count == windowsBeforeSettings, "settings are hosted inside the existing shell without a second HWND"); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var tabs = Find<TabControl>(settings) ?? throw new InvalidOperationException("No settings tabs");
                for (int i = 0; i < tabs.Items.Count; i++)
                { tabs.SelectedIndex = i; await SnapshotAsync(shell, "settings-" + i); }
                tabs.SelectedIndex = 3; await NativeGlassSnapshotAsync(shell, "settings-native-glass");
                await TestLiveGlassAsync(shell);
                foreach (string language in Localization.UiText.Languages)
                {
                    ((ComboBox)settings.FindName("UiLanguageCombo")).SelectedValue = language;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    shell.Refresh(cfg);
                    tabs.SelectedIndex = 3; await SnapshotAsync(shell, "appearance-" + language);
                    Check(Localization.UiText.Language == language && cfg.TargetLanguage == "zh-Hans-CN",
                        language + " selector previews UI without changing translation language");
                }
                settings.Close();
                foreach (string language in Localization.UiText.Languages)
                { Localization.UiText.SetLanguage(language); shell.Refresh(cfg); await SnapshotAsync(shell, "panel-" + language); }
                Localization.UiText.SetLanguage("zh-CN");
                Check(Localization.UiText.Language == "zh-CN", "closing unsaved settings restores original UI language");
                shell.Hide();
                var inline = new Overlay.InlineOverlayWindow(cfg);
                var ocr = new OcrOutcome { Lines = new[] { new OcrLine { Text = "Hello", Width = 600, Height = 100 } } };
                var backdrop = new Window { Width = 700, Height = 130, WindowStyle = WindowStyle.None,
                    Background = System.Windows.Media.Brushes.White, Topmost = true, ShowActivated = false };
                backdrop.Show(); Native.WindowUtilities.Position(backdrop, new Int32Rect(100, 180, 700, 130));
                Check(inline.Present(new SessionFrame(ocr, new Int32Rect(100, 180, 700, 130), "你好，世界。\n这是一条正在更新的游戏字幕。", true, false, 1)), "inline overlay can exclude itself from capture");
                await SnapshotAsync(inline, "inline-subtitles");
                inline.Present(new SessionFrame(ocr, new Int32Rect(100, 180, 700, 130), string.Join(" ", Enumerable.Repeat("这是一段较长的游戏对白，用于检查最小字号和溢出处理。", 300)), true, false, 2));
                Check(inline.TextClipped, "long inline dialogue reports overflow rather than shrinking below readable size");
                Check(inline.RenderedFontSize == cfg.OverlayFontSize && !inline.HasVisibleBorder && inline.VisualCount == 1,
                    "long inline text keeps configured font size and has no visible frame");
                await SnapshotAsync(inline, "inline-long-dialogue");
                inline.Present(new SessionFrame(ocr, new Int32Rect(100, 180, 700, 130), "短字幕", false, false, 2));
                Check(inline.RenderedFontSize == cfg.OverlayFontSize && inline.VisualCount == 1,
                    "streaming and short subtitles retain the same font and visual count");
                await Task.Delay(180);
                using (var behind = Capture.ScreenCapture.CaptureRegion(112, 192, 16, 16))
                    Check(behind.GetPixel(5, 5).R > 220, "GDI capture sees background rather than own translation plate");
                using (var process = Process.GetCurrentProcess())
                {
                    var before = process.TotalProcessorTime; var elapsed = Stopwatch.StartNew(); double captureMs = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        var tick = Stopwatch.StartNew(); using var sample = Capture.ScreenCapture.CaptureRegion(100, 180, 700, 130);
                        TranslationSession.Fingerprint(sample); captureMs += tick.Elapsed.TotalMilliseconds;
                        await Task.Delay(500);
                    }
                    Results.Add(new { name = "Synthetic 700x130 region sampling at 2Hz, no OCR/network", meanCaptureAndHashMs = captureMs / 10,
                        wholeProcessorCpuPercent = (process.TotalProcessorTime - before).TotalMilliseconds / elapsed.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100 });
                }
                inline.Close(); backdrop.Close();
                var fullOverlay = new Overlay.InlineOverlayWindow(cfg);
                var lineOcr = new OcrOutcome { Lines = new[] { new OcrLine { Text = "First", X = 20, Y = 20, Width = 200, Height = 40 }, new OcrLine { Text = "Second", X = 300, Y = 100, Width = 220, Height = 40 } } };
                Check(fullOverlay.Present(new SessionFrame(lineOcr, new Int32Rect(100, 180, 700, 260), "第一条译文\n第二条译文", true, true, 2)), "fullscreen line mapping accepts matching translated lines");
                await SnapshotAsync(fullOverlay, "fullscreen-overlay");
                Check(!fullOverlay.Present(new SessionFrame(lineOcr, new Int32Rect(100, 180, 700, 260), "不匹配的单行", true, true, 3)), "layout mismatch requests sidebar instead of attaching wrong translation");
                fullOverlay.Close();
                var smallSource = new OcrOutcome { Lines = new[] { new OcrLine { Text = "small source", X = 100, Y = 40, Width = 220, Height = 18 } } };
                var adaptive = new Overlay.InlineOverlayWindow(cfg);
                adaptive.Present(new SessionFrame(smallSource, new Int32Rect(100, 180, 700, 200), "小字", false, false, 30));
                double adaptedSize = adaptive.RenderedFontSize;
                Check(adaptedSize >= 10 && adaptedSize < cfg.OverlayFontSize && adaptive.FirstLeft > 0 && adaptive.FirstTop > 0,
                    "small source text produces smaller translation at the detected source position");
                adaptive.Present(new SessionFrame(smallSource, new Int32Rect(100, 180, 700, 200), "小字翻译持续输出", true, false, 30));
                Check(adaptive.RenderedFontSize == adaptedSize, "adaptive source font stays stable across streamed text updates");
                var jittered = new OcrOutcome { Lines = new[] { new OcrLine { Text = "small source", X = 102, Y = 41, Width = 221, Height = 19 } } };
                adaptive.Present(new SessionFrame(jittered, new Int32Rect(100, 180, 700, 200), "小字翻译", true, false, 30));
                Check(adaptive.RenderedFontSize == adaptedSize, "minor OCR box jitter does not make the translated font jump");
                await SnapshotAsync(adaptive, "adaptive-source-text"); adaptive.Close();
                var dense = new Overlay.InlineOverlayWindow(cfg);
                var denseLayout = new OcrOutcome { Lines = new[] {
                    new OcrLine { Text = "First label", X = 80, Y = 30, Width = 220, Height = 18 },
                    new OcrLine { Text = "Next label", X = 80, Y = 52, Width = 220, Height = 18 } } };
                Check(dense.Present(new SessionFrame(denseLayout, new Int32Rect(100, 180, 700, 200), "第一行\n下一行", true, false, 31)) && !dense.TextClipped,
                    "closely spaced small source rows remain individually readable without padding clipping");
                await SnapshotAsync(dense, "adaptive-dense-rows"); dense.Close();
                var popup = new Overlay.ResultOverlayWindow { Appearance = new AppConfig { GlassTransparencyEnabled = true } };
                popup.ShowResult(ocr, new Int32Rect(100, 200, 600, 100), App.ScaleFactor, "en-US");
                popup.SetStreamingText("你好，世界。\n这是一条可以随时复制的译文。");
                await SnapshotAsync(popup, "translation-popup"); await NativeGlassSnapshotAsync(popup, "popup-native-glass");
                ((Slider)popup.FindName("PopupOpacitySlider")).Value = 0.25;
                Check(Math.Abs(popup.Appearance.PopupTintOpacity - 0.25) < 0.001 && popup.Opacity == 1,
                    "floating opacity slider changes material tint without fading text");
                await NativeGlassSnapshotAsync(popup, "popup-native-glass-clear");
                await TestLiveGlassAsync(popup);
                await TestNoGhostCornersAsync(popup);
                await TestWhiteRimAsync(popup, "popup-window-rim");
                popup.Appearance.GlassEffects = false; ((Slider)popup.FindName("PopupOpacitySlider")).Value = 0.8;
                Check(((Border)popup.FindName("Card")).Background is System.Windows.Media.GradientBrush opaque && opaque.GradientStops.All(s => s.Color.A == 255),
                    "disabling glass switches to opaque readable fallback");
                popup.Close();
            }
            finally
            {
                string report = JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(Output, "report.json"), report);
                if (soak) File.WriteAllText(Path.Combine(Output, "stability-report.json"), report);
            }
        }
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        private static async Task TestCornersAndFocusAsync(Window shell)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(shell).Handle;
            var surface = shell.Content as Border ?? ((Grid)shell.Content).Children.OfType<Border>().First();
            IntPtr region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                int regionType = GetWindowRgn(hwnd, region);
                bool win11 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
                DwmGetWindowAttribute(hwnd, 33, out int preference, 4);
                bool clearClip = shell.AllowsTransparency && shell.Content is Grid { Clip: System.Windows.Media.RectangleGeometry contour } && contour.RadiusX == 8;
                Check(shell.AllowsTransparency ? regionType == 0 && clearClip : win11 ? regionType == 0 && preference == 2 : regionType != 0 && !PtInRegion(region, 1, 1),
                    "clear windows use one alpha corner contour without a square backing or custom native region");
                Check(surface.CornerRadius == new CornerRadius(0) && surface.Clip == null,
                    "glass tint and content have no second inner rounded plate or corner cutout");
            }
            finally { DeleteObject(region); }
            shell.WindowState = WindowState.Maximized;
            await Task.Delay(160);
            Native.NativeMethods.GetWindowRect(hwnd, out var maximized);
            var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
            Check(Math.Abs(maximized.Width - work.Width) <= 2 && Math.Abs(maximized.Height - work.Height) <= 2,
                "maximized shell respects the monitor work area with the native corner frame");
            shell.WindowState = WindowState.Normal;
            await Task.Delay(160);
            shell.Activate();
            var button = (Button)shell.FindName("SettingsButton"); button.Focus();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var border = (Border)button.Template.FindName("B", button);
            Check(button.IsKeyboardFocused && border.BorderThickness == button.BorderThickness,
                "focused title button does not latch the old two-pixel white border");
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
        private static async Task TestNoGhostCornersAsync(Window window)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            GetWindowDisplayAffinity(hwnd, out uint originalAffinity);
            Native.NativeMethods.GetWindowRect(hwnd, out var rect);
            var color = Color.FromArgb(230, 100, 170);
            var bg = new Window { WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R, color.G, color.B)) };
            bg.Show(); Native.WindowUtilities.Position(bg, new Int32Rect(rect.Left - 40, rect.Top - 40, rect.Width + 80, rect.Height + 80));
            var owner = window.Owner; window.Hide();
            await Task.Delay(150); Native.GlassMaterials.DwmFlush();
            using var baseline = Capture.ScreenCapture.CaptureRegion(rect.Left, rect.Top, rect.Width, rect.Height);
            window.Owner = bg; window.Show(); window.Topmost = true;
            Native.WindowUtilities.Position(window, new Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height));
            Native.NativeMethods.SetWindowDisplayAffinity(hwnd, 0); window.Activate();
            await Task.Delay(300); Native.GlassMaterials.DwmFlush();
            using var bitmap = Capture.ScreenCapture.CaptureRegion(rect.Left, rect.Top, rect.Width, rect.Height);
            var points = new[] { new System.Drawing.Point(1, 1), new System.Drawing.Point(rect.Width - 2, 1), new System.Drawing.Point(1, rect.Height - 2), new System.Drawing.Point(rect.Width - 2, rect.Height - 2) };
            int Delta(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            var deviations = points.Select(p => Delta(bitmap.GetPixel(p.X, p.Y), baseline.GetPixel(p.X, p.Y))).ToArray();
            // Windows may retain its soft black shadow outside an HRGN. Compare channel ratios
            // to distinguish that neutral shadow from a second square material/tint layer.
            var tintSpread = points.Select(p =>
            {
                var a = bitmap.GetPixel(p.X, p.Y); var b = baseline.GetPixel(p.X, p.Y);
                double[] ratios = { (double)a.R / b.R, (double)a.G / b.G, (double)a.B / b.B };
                return ratios.Max() - ratios.Min();
            }).ToArray();
            int inner = Delta(bitmap.GetPixel(rect.Width - 60, rect.Height / 2), baseline.GetPixel(rect.Width - 60, rect.Height / 2));
            Results.Add(new { name = "four outer-corner background deviations including native soft shadow", rgbDeviation = deviations, tintSpread, innerDeviation = inner,
                baseline = points.Select(p => baseline.GetPixel(p.X, p.Y).ToString()).ToArray() });
            bitmap.Save(Path.Combine(Output, "single-contour-corners.png"), System.Drawing.Imaging.ImageFormat.Png);
            Native.NativeMethods.SetWindowDisplayAffinity(hwnd, originalAffinity); window.Owner = owner; bg.Close();
            Check(tintSpread.All(value => value < 0.08) && inner > 30,
                "all four cutouts show backdrop with neutral native shadow rather than a square material plate");
        }
        internal static void OpenInputPreview()
        {
            // Taskbar presence lets UI inspection tools select this otherwise transient tool window.
            // Network and clipboard are isolated; production uses the real configured adapters.
            var window = new Overlay.InputTranslationWindow(new AppConfig(), _ => new InputTestTranslator(), _ => { })
            { ShowInTaskbar = true, Title = "输入翻译 · 界面验收" };
            window.Closed += (_, _) => Application.Current.Shutdown();
            window.Reveal();
        }
        private static async Task TestInputTranslationAsync()
        {
            var cfg = new AppConfig { SourceLanguage = "ja-JP", OcrProvider = "paddle", InputCompactMode = false, GlassTransparencyEnabled = true };
            var translator = new InputTestTranslator(); string copied = ""; bool copyFails = false; int savedTargets = 0;
            var window = new Overlay.InputTranslationWindow(cfg, _ => translator,
                value => { if (copyFails) throw new InvalidOperationException("simulated clipboard lock"); copied = value; }, () => savedTargets++);
            window.Reveal(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var input = (TextBox)window.FindName("InputBox"); var target = (ComboBox)window.FindName("TargetCombo");
            await window.TranslateInputAsync();
            Check(translator.Calls == 0 && !window.IsTranslating, "empty typed input never calls the translation provider");
            await SnapshotAsync(window, "input-translation-empty");
            input.Text = "Hello";
            var first = window.TranslateInputAsync(); await Task.Delay(90);
            Check(window.IsTranslating && window.TranslationText.Length > 0, "typed translation shows streaming progress before completion");
            await SnapshotAsync(window, "input-translation-streaming");
            await first;
            Check(window.TranslationText == "你好，世界。" && translator.LastFrom == null && translator.LastTarget == cfg.TargetLanguage && cfg.SourceLanguage == "ja-JP",
                "typed translation auto-detects source independently of OCR and uses the configured target");
            ((Button)window.FindName("CopyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(copied == "你好，世界。", "copy button copies exactly the translated text through the clipboard adapter");
            copyFails = true;
            Check(!window.CopyTranslation() && window.TranslationText == "你好，世界。", "clipboard failure keeps the translation available for retry");
            await SnapshotAsync(window, "input-translation-copy-error");
            copyFails = false; window.CopyTranslation();
            await SnapshotAsync(window, "input-translation-result");
            foreach (string language in Localization.UiText.Languages)
            { Localization.UiText.SetLanguage(language); await SnapshotAsync(window, "input-translation-" + language); }
            Localization.UiText.SetLanguage("zh-CN");
            target.SelectedValue = "en-US";
            Check(cfg.InputTargetLanguage == "en-US" && cfg.TargetLanguage == "zh-Hans-CN" && savedTargets == 1 && window.TranslationText == "",
                "typed target is remembered separately without changing the screen translation target");
            input.Text = "slow"; var slow = window.TranslateInputAsync(); await Task.Delay(30);
            input.Text = "fast";
            Check(!window.IsTranslating && window.TranslationText == "" && translator.Cancelled > 0,
                "editing input cancels its old request and clears stale translation");
            await window.TranslateInputAsync(); await slow; await Task.Delay(40);
            Check(window.TranslationText == "fast → en-US", "late cancelled streaming callbacks cannot overwrite the newer typed translation");
            input.Text = "slow"; var stopping = window.TranslateInputAsync(); await Task.Delay(30);
            window.StopTranslation(); string partial = window.TranslationText;
            await stopping;
            Check(!window.IsTranslating && window.TranslationText == partial && !partial.Contains("→"),
                "stopping typed translation retains the partial text without accepting a late final response");
            cfg.EnableStreaming = false;
            input.Text = "plain"; int streamCalls = translator.StreamCalls;
            await window.TranslateInputAsync();
            Check(translator.StreamCalls == streamCalls && window.TranslationText == "plain → en-US",
                "typed translation respects the non-streaming setting");
            input.Text = "failure"; await window.TranslateInputAsync();
            Check(!window.IsTranslating && ((Button)window.FindName("TranslateButton")).IsEnabled && window.TranslationText == "",
                "typed provider failure leaves retry available without showing a false result");
            await SnapshotAsync(window, "input-translation-error");
            input.Text = "empty"; await window.TranslateInputAsync();
            Check(!((Button)window.FindName("CopyButton")).IsEnabled && ((TextBlock)window.FindName("StatusText")).Text.Contains("未返回"),
                "an empty provider reply is reported instead of enabled for copying");
            input.Text = "Hello"; await window.TranslateInputAsync();
            await NativeGlassSnapshotAsync(window, "input-translation-native-glass");
            await TestWhiteRimAsync(window, "input-window-rim");
            await TestNoGhostCornersAsync(window);
            input.Text = "slow"; var closing = window.TranslateInputAsync(); await Task.Delay(25); int cancellations = translator.Cancelled;
            window.Close(); await closing; await Task.Delay(25);
            Check(translator.Cancelled > cancellations && !window.IsVisible, "closing the input popup cancels its active request and ignores late output");
            var missing = new Overlay.InputTranslationWindow(new AppConfig(), _ => null, _ => { });
            missing.Reveal(); ((TextBox)missing.FindName("InputBox")).Text = "Hello";
            await missing.TranslateInputAsync(); bool settingsOpened = false;
            missing.SettingsRequested += () => settingsOpened = true;
            Check(((Button)missing.FindName("SettingsButton")).Visibility == Visibility.Visible && !missing.IsTranslating,
                "missing translation credentials show a direct settings recovery action");
            ((Button)missing.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(settingsOpened && !missing.IsVisible, "settings recovery hides the input popup without discarding its draft");
            missing.Reveal();
            Check(((TextBox)missing.FindName("InputBox")).Text == "Hello", "reopening after settings retains the input draft");
            missing.Close();
            int before = Application.Current.Windows.OfType<Overlay.InputTranslationWindow>().Count();
            var app = (App)Application.Current; app.ShowInputTranslation(); app.ShowInputTranslation();
            var single = Application.Current.Windows.OfType<Overlay.InputTranslationWindow>().ToArray();
            Check(single.Length == before + 1, "repeated input-window invocation brings forward one popup instead of spawning duplicates");
            single.Last().Close();
            var settings = new Settings.SettingsView(new AppConfig()) { PreviewMode = true };
            var capture = (Hotkey.ShortcutCaptureBox)settings.FindName("CaptureKeyBox");
            Check(settings.ValidateShortcut(capture, "Ctrl+Shift+I") != null && ((Hotkey.ShortcutCaptureBox)settings.FindName("InputKeyBox")).Text == "Ctrl+Shift+I",
                "the input shortcut is configurable and protected against conflicts with existing actions");
            settings.Close();
        }
        private sealed class InputTestTranslator : ITranslator
        {
            public string Name => "typed-test";
            public int Calls, StreamCalls, Cancelled;
            public string? LastFrom, LastTarget;
            public Task<TranslateResult> TranslateAsync(string text, string? from, string to, CancellationToken ct) => Run(text, from, to, null, ct);
            public Task<TranslateResult> TranslateStreamingAsync(string text, string? from, string to, Action<string>? progress, CancellationToken ct)
            { StreamCalls++; return Run(text, from, to, progress, ct); }
            private async Task<TranslateResult> Run(string text, string? from, string to, Action<string>? progress, CancellationToken ct)
            {
                Calls++; LastFrom = from; LastTarget = to;
                using var cancel = ct.Register(() => Interlocked.Increment(ref Cancelled));
                progress?.Invoke("正在生成 " + text);
                // Deliberately ignore cancellation to exercise late callback/version guards.
                await Task.Delay(text == "slow" ? 300 : 120);
                if (text == "failure") throw new TimeoutException("simulated provider timeout");
                string result = text == "Hello" ? "你好，世界。" : text == "empty" ? "" : text + " → " + to;
                progress?.Invoke(result);
                return new TranslateResult { Text = result, ElapsedMs = 120 };
            }
        }
        private static async Task TestDropDownMotionAsync()
        {
            var combo = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Top };
            combo.Items.Add("First option"); combo.Items.Add("Second option"); combo.Items.Add("Third option");
            combo.SelectedIndex = 0;
            var window = new Window { Content = combo, Width = 330, Height = 180, ShowInTaskbar = false };
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
            var panel = (FrameworkElement)popup.Child;
            combo.IsDropDownOpen = true;
            await Task.Delay(35);
            Check(popup.IsOpen && panel.Opacity > 0 && panel.Opacity <= 1,
                "dropdown becomes visible immediately during its interruptible entrance");
            await Task.Delay(180);
            var arrow = (System.Windows.Shapes.Path)combo.Template.FindName("DropDownChevron", combo);
            Check(Math.Abs(((System.Windows.Media.RotateTransform)arrow.RenderTransform).Angle - 180) < .1,
                "dropdown arrow follows the expanded state");
            combo.SelectedIndex = 2; combo.IsDropDownOpen = false;
            Check(combo.SelectedIndex == 2 && (SystemParameters.ClientAreaAnimation ? popup.IsOpen && !panel.IsHitTestVisible : !popup.IsOpen),
                "selection commits immediately while only the noninteractive dropdown visual exits");
            await Task.Delay(170);
            Check(!popup.IsOpen, "dropdown releases its popup after the short exit transition");
            combo.IsDropDownOpen = true; await Task.Delay(25);
            combo.IsDropDownOpen = false; await Task.Delay(25);
            combo.IsDropDownOpen = true; await Task.Delay(220);
            Check(popup.IsOpen && combo.IsDropDownOpen && Math.Abs(panel.Opacity - 1) < .01,
                "rapid reopening cancels the stale close completion without flashing or disappearing");
            combo.IsDropDownOpen = false; window.Close(); await Task.Delay(170);
            Check(!popup.IsOpen, "closing the owning view during a transition leaves no orphan dropdown");
        }
        private static async Task TestWhiteRimAsync(Window window, string name)
        {
            await NativeGlassSnapshotAsync(window, name);
            using var bitmap = new Bitmap(Path.Combine(Output, name + ".png"));
            int white = 0, samples = 0;
            void Sample(int x, int y)
            { var color = bitmap.GetPixel(x, y); if (color.R > 235 && color.G > 235 && color.B > 235) white++; samples++; }
            for (int layer = 0; layer < 3; layer++)
            {
                for (int x = 30; x < bitmap.Width - 30; x++) { Sample(x, layer); Sample(x, bitmap.Height - 1 - layer); }
                for (int y = 30; y < bitmap.Height - 30; y++) { Sample(layer, y); Sample(bitmap.Width - 1 - layer, y); }
            }
            Results.Add(new { name = name + " bright rim pixels", white, samples });
            Check(white == 0, name + " has no white strip along any edge on a colored backdrop");
        }
        private static async Task TestPlateAppearanceAsync()
        {
            var cfg = new AppConfig();
            Check(cfg.OverlayBackgroundEnabled && cfg.OverlayOpacity >= .8, "translation backing is enabled and readable by default");
            var old = new AppConfig { AppearanceVersion = 1, OverlayOpacity = 0 };
            old.UpgradeOverlayAppearance();
            Check(old.OverlayOpacity >= .8 && old.OverlayBackgroundEnabled, "upgrade restores a visible backing for old transparent defaults");
            var custom = new AppConfig { AppearanceVersion = 1, OverlayOpacity = .47 };
            custom.UpgradeOverlayAppearance();
            Check(custom.OverlayOpacity == .47, "upgrade preserves custom backing opacity");
            var transparent = new AppConfig { AppearanceVersion = 2, OverlayOpacity = 0 };
            transparent.UpgradeOverlayAppearance();
            Check(transparent.OverlayOpacity == 0, "explicit transparency remains available after upgrading");
            var settings = new Settings.SettingsView(cfg) { PreviewMode = true };
            var color = (TextBox)settings.FindName("PlateColorBox");
            var slider = (Slider)settings.FindName("PlateTransparencySlider");
            var toggle = (CheckBox)settings.FindName("PlateEnabledCheck");
            var preview = (Border)settings.FindName("PlatePreview");
            color.Text = "#F0EADD"; slider.Value = 25;
            var painted = ((System.Windows.Media.SolidColorBrush)preview.Background).Color;
            Check(painted.R == 240 && painted.G == 234 && painted.B == 221 && painted.A == 191,
                "settings preview applies selected color and 25 percent transparency independently");
            Check(cfg.OverlayBackgroundColor == "#101820" && cfg.OverlayOpacity == .82,
                "backing preview does not mutate the saved settings draft before saving");
            color.Text = "#nope";
            Check(((TextBlock)settings.FindName("PlateColorError")).Visibility == Visibility.Visible,
                "invalid background color has an inline recovery message");
            toggle.IsChecked = false;
            Check(((System.Windows.Media.SolidColorBrush)preview.Background).Color.A == 0,
                "backing can be disabled without hiding translation text");
            settings.Close();
            cfg.OverlayBackgroundColor = "#F0EADD"; cfg.OverlayOpacity = .75;
            var inline = new Overlay.InlineOverlayWindow(cfg);
            var ocr = new OcrOutcome { Lines = new[] { new OcrLine { Text = "Hello", X = 40, Y = 20, Width = 250, Height = 28 } } };
            inline.Present(new SessionFrame(ocr, new Int32Rect(200, 200, 500, 100), "有底色的译文", true, false, 1));
            var plate = (Border)((Canvas)inline.Content).Children[0];
            var text = (TextBlock)plate.Child;
            Check(((System.Windows.Media.SolidColorBrush)plate.Background).Color.A == 191 &&
                ((System.Windows.Media.SolidColorBrush)text.Foreground).Color.R < 30,
                "actual overlay uses the selected translucent color and readable dark text on light backing");
            await SnapshotAsync(inline, "inline-color-backing");
            cfg.OverlayOpacity = 0;
            inline.Present(new SessionFrame(ocr, new Int32Rect(200, 200, 500, 100), "透明底色", true, false, 2));
            Check(((System.Windows.Media.SolidColorBrush)plate.Background).Color.A == 0 && text.Opacity == 1,
                "100 percent background transparency leaves translation text fully visible");
            inline.Close();
        }
        private static async Task TestShortcutCaptureAsync()
        {
            var cfg = new AppConfig();
            var settings = new Settings.SettingsView(cfg) { PreviewMode = true };
            var capture = (Hotkey.ShortcutCaptureBox)settings.FindName("CaptureKeyBox");
            string before = capture.Text; var states = new List<bool>();
            settings.ShortcutCaptureStateChanged += states.Add;
            capture.BeginRecording();
            capture.HandleDown(System.Windows.Input.Key.J, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt);
            Check(capture.IsRecording, "shortcut recording keeps global bindings suspended until the primary key is released");
            capture.HandleUp(System.Windows.Input.Key.J);
            Check(capture.Text == "Ctrl+Alt+J" && !capture.IsRecording && states.SequenceEqual(new[] { true, false }),
                "pressing a chord records a canonical shortcut and resumes bindings on release");
            capture.BeginRecording(); capture.HandleDown(System.Windows.Input.Key.K, System.Windows.Input.ModifierKeys.None);
            capture.HandleUp(System.Windows.Input.Key.K);
            Check(capture.IsRecording, "a bare game key cannot be committed as a global shortcut");
            capture.CancelRecording(); Check(capture.Text == "Ctrl+Alt+J", "cancelling a recording restores its last saved draft value");
            capture.BeginRecording();
            capture.HandleDown(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift);
            capture.HandleUp(System.Windows.Input.Key.F);
            Check(capture.IsRecording, "a shortcut already used by another action is rejected before saving");
            capture.CancelRecording(); capture.BeginRecording();
            capture.HandleDown(System.Windows.Input.Key.S, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift);
            capture.HandleUp(System.Windows.Input.Key.S);
            Check(capture.IsRecording, "the reserved OCR chord cannot be overwritten by the recorder");
            capture.CancelRecording(); capture.BeginRecording();
            Check(!capture.HandleDown(System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.None) && !capture.IsRecording,
                "Tab exits shortcut recording so keyboard navigation remains available");
            Check(Hotkey.ShortcutParser.TryFromKey(System.Windows.Input.Key.D9, System.Windows.Input.ModifierKeys.Control, out string digit) && digit == "Ctrl+9",
                "recorded number-row keys round-trip through the shortcut parser");
            settings.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        private static async Task TestSmoothScrollAsync()
        {
            var content = new StackPanel();
            for (int i = 0; i < 80; i++) content.Children.Add(new TextBlock { Text = "Item " + i, Height = 30 });
            var viewer = new ScrollViewer { Content = content, CanContentScroll = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var window = new Window { Content = viewer, Width = 340, Height = 240, ShowInTaskbar = false };
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Native.SmoothScroll.ScrollBy(viewer, 420);
            double start = viewer.VerticalOffset;
            await Task.Delay(70); double middle = viewer.VerticalOffset;
            await Task.Delay(170); double end = viewer.VerticalOffset;
            Check(end > 350 && (!SystemParameters.ClientAreaAnimation || (start < end && middle > start)),
                "wheel motion progresses over time and settles at the requested offset");
            Native.SmoothScroll.ScrollBy(viewer, 500); await Task.Delay(40); Native.SmoothScroll.Stop(viewer);
            double stopped = viewer.VerticalOffset; await Task.Delay(220);
            Check(Math.Abs(viewer.VerticalOffset - stopped) < 1, "pointer/keyboard interruption cancels wheel animation without a late jump");
            window.Close();
        }
        private static async Task TestImmediatePopupAsync()
        {
            using var presenter = new Overlay.TranslationPresenter(new AppConfig(), "popup", false);
            var clock = Stopwatch.StartNew(); presenter.Begin(new Int32Rect(150, 150, 400, 100), false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(presenter.PopupVisible, "floating mode acknowledges capture before any OCR or network result");
            Results.Add(new { name = "pending popup creation before OCR", ms = clock.ElapsedMilliseconds });
        }
        private static void TestLocales()
        {
            var packs = Localization.UiText.Catalogs;
            var source = packs["zh-CN"];
            foreach (var pack in packs)
            {
                Check(pack.Value.Keys.ToHashSet().SetEquals(source.Keys), pack.Key + " UI catalog has every source key");
                bool valid = true;
                foreach (var item in source)
                {
                    var placeholders = System.Text.RegularExpressions.Regex.Matches(item.Value, @"\{(\d+)(?:[^{}]*)\}").Select(m => m.Groups[1].Value).Order().ToArray();
                    var translated = System.Text.RegularExpressions.Regex.Matches(pack.Value[item.Key], @"\{(\d+)(?:[^{}]*)\}").Select(m => m.Groups[1].Value).Order().ToArray();
                    valid &= placeholders.SequenceEqual(translated);
                    try { string.Format(System.Globalization.CultureInfo.InvariantCulture, pack.Value[item.Key], Enumerable.Repeat<object>(123, 20).ToArray()); }
                    catch (FormatException) { valid = false; }
                }
                Check(valid, pack.Key + " UI formats preserve all placeholder indexes");
            }
            string target = ((App)Application.Current).Config.TargetLanguage;
            Localization.UiText.SetLanguage("en-US");
            Check(Localization.UiText.T("设置") == "Settings" && ((App)Application.Current).Config.TargetLanguage == target,
                "UI language is independent of translation target and updates without network");
            Localization.UiText.SetLanguage("zh-CN");
        }
        internal static async Task VerifyProviderAsync()
        {
            Directory.CreateDirectory(Output);
            var cfg = AppConfig.Load();
            var translator = cfg.CreateTranslator() ?? throw new InvalidOperationException("翻译服务未配置");
            var watch = Stopwatch.StartNew(); long first = -1; int updates = 0; bool labels = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var translated = await ((IRowTranslator)translator).TranslateRowsAsync(["Open the gate.", "Follow me to the harbor."], "en-US", cfg.TargetLanguage,
                rows => { if (rows.Count == 0) return; if (first < 0) first = watch.ElapsedMilliseconds; Interlocked.Increment(ref updates);
                    labels |= rows.Any(r => r.Text.Contains("[[GL") || r.Text.Contains("原文：") || r.Text.Contains("译文：") || r.Text.StartsWith("Translation:", StringComparison.OrdinalIgnoreCase)); }, timeout.Token);
            File.WriteAllText(Path.Combine(Output, "provider-check.json"), JsonSerializer.Serialize(new
            { provider = translator.Name, firstTextMs = first, totalMs = watch.ElapsedMilliseconds, updates,
                translatedCharacters = translated.Text.Length, sourceLines = 2, outputLines = translated.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
                unexpectedLabels = labels, successful = translated.Text.Length > 0 && !labels }, new JsonSerializerOptions { WriteIndented = true }));
            if (labels) throw new InvalidOperationException("Provider emitted unexpected subtitle labels");
        }
        private static async Task TestStreamAsync()
        {
            string sse = ": heartbeat\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"好🌏\"},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":3}}\n\ndata: [DONE]\n\n";
            using var bytes = new FragmentStream(Encoding.UTF8.GetBytes(sse));
            string preview = "";
            var result = await SseTranslationReader.ReadAsync(bytes, text => preview = text, CancellationToken.None);
            Check(result.Text == "你好🌏" && preview == result.Text && result.InputTokens == 9, "fragmented UTF-8 SSE, heartbeat, usage and DONE");
            bool failed = false;
            try { using var incomplete = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")); await SseTranslationReader.ReadAsync(incomplete, _ => { }, CancellationToken.None); }
            catch (IOException) { failed = true; }
            Check(failed, "truncated stream is rejected instead of cached as complete");
        }
        private static async Task TestHttpAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            bool firstStream = false, secondStream = true, promptContract = false;
            var server = Task.Run(async () =>
            {
                for (int round = 0; round < 2; round++)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    var header = new List<byte>(); var one = new byte[1];
                    while (true)
                    {
                        await stream.ReadExactlyAsync(one); header.Add(one[0]);
                        if (header.Count > 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                        if (header.Count > 16000) throw new IOException("oversized test header");
                    }
                    string headers = Encoding.ASCII.GetString(header.ToArray());
                    int length = int.Parse(headers.Split("\r\n").First(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim());
                    var body = new byte[length]; await stream.ReadExactlyAsync(body);
                    using var json = JsonDocument.Parse(body);
                    var messages = json.RootElement.GetProperty("messages");
                    promptContract = messages[1].GetProperty("content").GetString() == "Hello" &&
                        messages[0].GetProperty("content").GetString()!.Contains("禁止添加原文");
                    if (round == 0) firstStream = json.RootElement.GetProperty("stream").GetBoolean();
                    else secondStream = json.RootElement.GetProperty("stream").GetBoolean();
                    string payload = round == 0 ? "{\"error\":{\"message\":\"stream is not supported\"}}" : "{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}";
                    byte[] bytes = Encoding.UTF8.GetBytes(payload);
                    string response = $"HTTP/1.1 {(round == 0 ? "400 Bad Request" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response)); await stream.WriteAsync(bytes);
                }
            });
            try
            {
                var translator = new OpenAiCompatTranslator(new TranslatorConfig { BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "test", TimeoutSeconds = 5 }, "test-not-a-real-key");
                string shown = "";
                var translated = await translator.TranslateStreamingAsync("Hello", "en-US", "zh-Hans-CN", text => shown = text, CancellationToken.None);
                await server.WaitAsync(TimeSpan.FromSeconds(6));
                Check(firstStream && !secondStream && translated.Text == "你好" && shown == "你好", "HTTP streaming rejection falls back to non-stream JSON");
                Check(promptContract, "translation requests put raw source in the user message and enforce subtitle-only output in the system contract");
            }
            finally { listener.Stop(); }
        }
        private static async Task TestSessionAsync()
        {
            int state = 0, captures = 0;
            var engine = new FakeOcr(); var translator = new FakeTranslator { FirstDelay = 1800 };
            var cfg = new AppConfig { SourceLanguage = "en-US", PollIntervalMs = 250, OcrIntervalMs = 300 };
            cfg.Translator.Model = "cancel-v061";
            using var timeout = new CancellationTokenSource(8000);
            var latest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new List<string>();
            await using var session = new TranslationSession(cfg, new Int32Rect(0, 0, 300, 100), true, false, true, IntPtr.Zero,
                engine, translator, _ =>
                {
                    Interlocked.Increment(ref captures);
                    var bmp = new Bitmap(300, 100); using var graphics = Graphics.FromImage(bmp);
                    graphics.Clear(Volatile.Read(ref state) == 0 ? Color.White : Color.Black); return bmp;
                });
            session.Frame += frame => { if (frame.Final && frame.Text.Length > 0) { lock (completed) completed.Add(frame.Text); if (frame.Text == "second translated") latest.TrySetResult(frame.Text); } };
            session.Start();
            await Task.Delay(300, timeout.Token); Volatile.Write(ref state, 1);
            string final = await latest.Task.WaitAsync(timeout.Token);
            await Task.Delay(1100, timeout.Token);
            Check(completed.Last() == "second translated", "new text wins over cancelled older translation");
            Results.Add(new { name = "synthetic changed-text first output latency", firstOcrMs = session.FirstOcrMs, firstTextMs = session.FirstTextMs });
            Check(session.FirstTextMs < 1500, "changed dialogue uses one short confirmation without the old extra translation debounce");
            Check(engine.Calls == 3 && translator.Calls <= 2, "static image skips OCR and duplicate translation");
            session.PauseResume(); await Task.Delay(350); int pausedCount = captures; await Task.Delay(550);
            Check(captures == pausedCount, "paused monitoring performs no capture");
        }
        private static async Task TestMovingLayoutAsync()
        {
            int phase = 0, samples = 0;
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cfg = new AppConfig { SourceLanguage = "en-US", PollIntervalMs = 250, OcrIntervalMs = 300 };
            cfg.Translator.Model = "moving-layout-v071";
            await using var session = new TranslationSession(cfg, new Int32Rect(0, 0, 300, 100), true, false, true, IntPtr.Zero,
                new MovingOcr(() => Volatile.Read(ref phase) == 0 ? 10 : 60), new FakeTranslator { FirstDelay = 30 }, _ =>
                {
                    var bmp = new Bitmap(300, 100); using var g = Graphics.FromImage(bmp);
                    g.Clear(Interlocked.Increment(ref samples) % 2 == 0 ? Color.White : Color.Black); return bmp;
                });
            session.Frame += f =>
            {
                if (!f.Final || f.Text.Length == 0) return;
                first.TrySetResult();
                if (f.Ocr.Lines[0].X == 60) moved.TrySetResult();
            };
            session.Start(); await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            long version = session.CurrentVersion; Volatile.Write(ref phase, 1);
            await moved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(session.ApiCalls == 1 && session.CurrentVersion == version,
                "unchanged words moving on screen update overlay coordinates without retranslating");
        }
        private static async Task TestWatchNoiseAsync()
        {
            const string sentence = "Please open the gate and follow me.";
            const string next = "Wait here. The next train arrives soon.";
            var cfg = new AppConfig { SourceLanguage = "en-US", PollIntervalMs = 250, OcrIntervalMs = 300 };
            cfg.Translator.Model = "noise-v061";
            int phase = 0, samples = 0, calls = 0, blankAfterText = 0; bool seenText = false;
            var final = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new ScriptedOcr(() =>
            {
                int n = Interlocked.Increment(ref calls);
                if (Volatile.Read(ref phase) == 1) return next;
                return (n % 5) switch { 2 => "", 3 => sentence.Replace("gate", "gafe"),
                    4 => "Please open the gate\nand follow me.", _ => sentence };
            });
            await using var session = new TranslationSession(cfg, new Int32Rect(0, 0, 300, 100), true, false, true, IntPtr.Zero,
                engine, new FakeTranslator { FirstDelay = 30 }, _ =>
                {
                    var bmp = new Bitmap(300, 100); using var g = Graphics.FromImage(bmp);
                    g.Clear(Interlocked.Increment(ref samples) % 2 == 0 ? Color.White : Color.Black); return bmp;
                });
            session.Frame += f =>
            {
                if (f.Final && f.Text.Length > 0) { seenText = true; if (f.Text == next + " translated") final.TrySetResult(); }
                if (seenText && f.Ocr.IsEmpty) Interlocked.Increment(ref blankAfterText);
            };
            session.Start(); await Task.Delay(2800);
            Check(session.ApiCalls == 1 && blankAfterText == 0 && calls >= 4,
                "animated background, one-frame OCR errors, whitespace and brief misses preserve one translation");
            Volatile.Write(ref phase, 1);
            await final.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(session.ApiCalls == 2, "confirmed new dialogue translates once after noisy background");
            Check(WatchTextStabilizer.Canonical("now here") != WatchTextStabilizer.Canonical("nowhere"),
                "English word boundaries remain meaningful for deduplication and cache");
            Check(WatchTextStabilizer.Canonical("日本 語") == WatchTextStabilizer.Canonical("日本語"),
                "spurious spaces between Japanese glyphs do not cause regeneration");
            var stabilizer = new WatchTextStabilizer(); stabilizer.Observe("Score: 10", 0);
            stabilizer.Observe("Score: 11", 500);
            Check(stabilizer.Observe("Score: 11", 1000), "changed numbers are confirmed rather than discarded as OCR noise");
            var clears = new WatchTextStabilizer(); clears.Observe(sentence, 0);
            clears.Observe("", 500); clears.Observe("", 1000);
            Check(!clears.Observe("", 1500) && clears.Observe("", 2000),
                "subtitle disappearance needs sustained empty OCR rather than a one-frame miss");
        }
        private static async Task TestLiveGlassAsync(Window window)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            GetWindowDisplayAffinity(hwnd, out uint originalAffinity);
            Native.NativeMethods.GetWindowRect(hwnd, out var rect);
            var bg = new Window { WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true,
                Background = System.Windows.Media.Brushes.DarkRed };
            bg.Show(); Native.WindowUtilities.Position(bg, new Int32Rect(rect.Left - 25, rect.Top - 25, rect.Width + 50, rect.Height + 50));
            var originalOwner = window.Owner;
            window.Owner = bg;
            window.Hide(); window.Show();
            window.Topmost = true; Native.WindowUtilities.Position(window, new Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height));
            Native.NativeMethods.SetWindowDisplayAffinity(hwnd, 0); window.Activate();
            await Task.Delay(250); Native.GlassMaterials.DwmFlush();
            Color red;
            using (var first = Capture.ScreenCapture.CaptureRegion(rect.Right - 32, rect.Bottom - 40, 8, 8)) red = first.GetPixel(4, 4);
            bg.Background = System.Windows.Media.Brushes.RoyalBlue;
            // Give focus to the backdrop, then restack without activating the translation window.
            bg.Activate(); Native.WindowUtilities.Position(window, new Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height));
            await Task.Delay(300); Native.GlassMaterials.DwmFlush();
            Color blue;
            using (var second = Capture.ScreenCapture.CaptureRegion(rect.Right - 32, rect.Bottom - 40, 8, 8)) blue = second.GetPixel(4, 4);
            Results.Add(new { name = window.GetType().Name + " live glass active red / inactive blue pixel", red = red.ToString(), blue = blue.ToString() });
            Check(red.R - red.B > 25 && blue.B - blue.R > 25,
                window.GetType().Name + " glass visibly transmits changed desktop colors even after losing focus");
            if (window.AllowsTransparency)
            {
                var drawing = new System.Windows.Media.DrawingGroup();
                drawing.Children.Add(new System.Windows.Media.GeometryDrawing(System.Windows.Media.Brushes.Black, null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 8, 8))));
                drawing.Children.Add(new System.Windows.Media.GeometryDrawing(System.Windows.Media.Brushes.White, null, new System.Windows.Media.RectangleGeometry(new Rect(4, 0, 4, 8))));
                bg.Background = new System.Windows.Media.DrawingBrush(drawing) { TileMode = System.Windows.Media.TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute };
                await Task.Delay(200); Native.GlassMaterials.DwmFlush();
                using var sharp = Capture.ScreenCapture.CaptureRegion(rect.Right - 80, rect.Bottom - 40, 48, 8);
                var values = Enumerable.Range(0, sharp.Width).Select(x => (int)sharp.GetPixel(x, 4).R).ToArray();
                Check(values.Max() - values.Min() > 110, window.GetType().Name + " preserves sharp underlying stripes instead of blurring them");
            }
            Native.NativeMethods.SetWindowDisplayAffinity(hwnd, originalAffinity); window.Owner = originalOwner; bg.Close();
        }
        private static Bitmap JapaneseSample()
        {
            var bitmap = new Bitmap(800, 150);
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.White);
            using var font = new Font("Yu Gothic UI", 40, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
            graphics.DrawString("こんにちは世界", font, Brushes.Black, 20, 30); return bitmap;
        }
        private static async Task TestFullscreenAsync()
        {
            var cfg = new AppConfig { SourceLanguage = "en-US", OcrProvider = "windows" };
            var finished = new TaskCompletionSource<SessionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var session = new TranslationSession(cfg, new Int32Rect(0, 0, 2560, 1600), false, true, true,
                IntPtr.Zero, new WindowsOcrEngine(), new FakeTranslator { FirstDelay = 100 }, _ =>
                {
                    var bitmap = new Bitmap(2560, 1600); using var graphics = Graphics.FromImage(bitmap);
                    graphics.Clear(Color.White); using var font = new Font("Segoe UI", 36);
                    graphics.DrawString("Hello world", font, Brushes.Black, 80, 80);
                    graphics.DrawString("Second text", font, Brushes.Black, 1800, 1100); return bitmap;
                });
            session.Frame += frame => { if (frame.Final && frame.Text.Length > 0) finished.TrySetResult(frame); };
            session.Start(); var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check(result.Ocr.Lines.Count >= 2 && result.Ocr.Lines.Any(l => l.X > 1500 && l.Y > 900),
                "fullscreen tiled Windows OCR retains coordinates across tile boundaries");
        }
        private static async Task TestStabilityAsync()
        {
            var cfg = new AppConfig { SourceLanguage = "en-US", PollIntervalMs = 500, OcrIntervalMs = 750 };
            cfg.Translator.Model = "synthetic-stability-test";
            var clock = Stopwatch.StartNew();
            using var process = Process.GetCurrentProcess();
            GC.Collect(); long before = GC.GetTotalMemory(true); var cpu = process.TotalProcessorTime;
            await using var session = new TranslationSession(cfg, new Int32Rect(0, 0, 300, 100), true, false, true, IntPtr.Zero,
                new FakeOcr(), new FakeTranslator { FirstDelay = 100 }, _ =>
                {
                    var bmp = new Bitmap(300, 100); using var graphics = Graphics.FromImage(bmp);
                    graphics.Clear((clock.ElapsedMilliseconds / 1000) % 2 == 0 ? Color.White : Color.Black); return bmp;
                });
            session.Start(); await Task.Delay(TimeSpan.FromSeconds(60));
            await session.DisposeAsync();
            long after = GC.GetTotalMemory(true);
            Results.Add(new { name = "60s synthetic changing-region stability (fake OCR/translator)", ocrCalls = session.OcrCalls,
                translationCalls = session.ApiCalls, managedHeapDeltaBytes = after - before,
                wholeProcessorCpuPercent = (process.TotalProcessorTime - cpu).TotalMilliseconds / clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100 });
            Check(session.OcrCalls <= 130 && session.ApiCalls <= 2, "60s monitoring stays bounded and caches repeated dialogue");
        }
        private static async Task SnapshotAsync(Window window, string name)
        {
            if (!window.IsVisible) window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var image = new RenderTargetBitmap(Math.Max(1, (int)window.ActualWidth), Math.Max(1, (int)window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(Output, name + ".png")); encoder.Save(output);
        }
        private static async Task NativeGlassSnapshotAsync(Window window, string name)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            GetWindowDisplayAffinity(hwnd, out uint originalAffinity);
            Native.NativeMethods.GetWindowRect(hwnd, out var bounds);
            var pattern = new Window
            {
                WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, Topmost = true,
                Content = new Border { Background = new System.Windows.Media.LinearGradientBrush(
                    System.Windows.Media.Color.FromRgb(202, 108, 79), System.Windows.Media.Color.FromRgb(44, 118, 156), 25),
                    Child = new TextBlock { Text = "GLASS\n\nLIGHT & COLOR\n\nGLASSLINGO", FontSize = 60, Margin = new Thickness(60),
                        Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold } }
            };
            pattern.Show(); Native.WindowUtilities.Position(pattern, new Int32Rect(bounds.Left - 20, bounds.Top - 20, bounds.Width + 40, bounds.Height + 40));
            var originalOwner = window.Owner;
            window.Owner = pattern;
            // STARTUPINFO's hidden show state can leave WPF's first test HWND hidden while
            // IsVisible is already true. A fresh WPF show also restores the render target.
            window.Hide(); window.Show();
            bool topmost = window.Topmost; window.Topmost = true;
            Native.WindowUtilities.Position(window, new Int32Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
            window.Activate(); Native.NativeMethods.SetForegroundWindow(hwnd);
            Native.NativeMethods.SetWindowDisplayAffinity(hwnd, 0);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(300); Native.GlassMaterials.DwmFlush();
            using (var bitmap = Capture.ScreenCapture.CaptureRegion(bounds.Left, bounds.Top, bounds.Width, bounds.Height))
                bitmap.Save(Path.Combine(Output, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            Native.NativeMethods.SetWindowDisplayAffinity(hwnd, originalAffinity); window.Topmost = topmost; window.Owner = originalOwner; pattern.Close();
        }
        private static T? Find<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            { var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i); if (child is T found) return found; var next = Find<T>(child); if (next != null) return next; }
            return null;
        }
        private sealed class FragmentStream(byte[] bytes) : MemoryStream(bytes)
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
        }
        private sealed class ScriptedOcr(Func<string> text) : IOcrEngine
        {
            public string Name => "scripted";
            public IReadOnlyList<string> Languages => new[] { "en-US" };
            public bool RetryAllLanguagesWhenEmpty => false;
            public bool UsesLanguageSpecificModels => false;
            public bool Supports(string tag) => true;
            public Task<OcrOutcome> RecognizeAsync(Bitmap bitmap, string tag, CancellationToken ct)
            {
                string value = text();
                return Task.FromResult(new OcrOutcome { Lines = value.Length == 0 ? Array.Empty<OcrLine>() :
                    new[] { new OcrLine { Text = value, Width = 250, Height = 30 } }, LanguageTag = "en-US" });
            }
        }
        private sealed class MovingOcr(Func<int> x) : IOcrEngine
        {
            public string Name => "moving-layout-test";
            public IReadOnlyList<string> Languages => new[] { "en-US" };
            public bool RetryAllLanguagesWhenEmpty => false;
            public bool UsesLanguageSpecificModels => false;
            public bool Supports(string tag) => true;
            public Task<OcrOutcome> RecognizeAsync(Bitmap bitmap, string tag, CancellationToken ct)
                => Task.FromResult(new OcrOutcome { Lines = new[] { new OcrLine { Text = "Follow this label", X = x(), Y = 20, Width = 180, Height = 18 } }, LanguageTag = "en-US" });
        }
        private sealed class FakeOcr : IOcrEngine
        {
            public int Calls;
            public string Name => "test";
            public IReadOnlyList<string> Languages => new[] { "en-US" };
            public bool RetryAllLanguagesWhenEmpty => false;
            public bool UsesLanguageSpecificModels => false;
            public bool Supports(string tag) => true;
            public Task<OcrOutcome> RecognizeAsync(Bitmap bitmap, string tag, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls); string text = bitmap.GetPixel(0, 0).R > 128 ? "first" : "second";
                return Task.FromResult(new OcrOutcome { Lines = new[] { new OcrLine { Text = text, Width = 100, Height = 30 } }, LanguageTag = "en-US" });
            }
        }
        private sealed class FakeTranslator : ITranslator
        {
            public int Calls;
            public int FirstDelay = 900;
            public string Name => "test";
            public Task<TranslateResult> TranslateAsync(string text, string? from, string to, CancellationToken ct)
                => TranslateStreamingAsync(text, from, to, null, ct);
            public async Task<TranslateResult> TranslateStreamingAsync(string text, string? from, string to, Action<string>? progress, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls); await Task.Delay(text == "first" ? FirstDelay : 100, ct);
                progress?.Invoke(text + " translated"); return new TranslateResult { Text = text + " translated" };
            }
        }
    }
}
