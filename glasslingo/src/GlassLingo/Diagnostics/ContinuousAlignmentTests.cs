using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GlassLingo.Config;
using GlassLingo.Ocr;
using GlassLingo.Overlay;
using GlassLingo.Sessions;
using GlassLingo.Translate;

namespace GlassLingo.Diagnostics
{
    internal static partial class SelfTests
    {
        private static bool Disjoint(IReadOnlyList<Rect> bounds) => !bounds.SelectMany((a, i) => bounds.Skip(i + 1).Select(b => Rect.Intersect(a, b)))
            .Any(r => !r.IsEmpty && r.Width > 0 && r.Height > 0);

        private static async Task TestContinuousAlignmentAsync()
        {
            await TestCleanStreamTransportAsync(true);
            string[] rows = ["Open gate", "Follow me"];
            var parsed = RowTranslation.Parse("[[GL1]]跟我来\n[[GL0]]开门", rows, true);
            Check(parsed.Count == 2 && parsed[0].Text == "开门" && parsed[1].Text == "跟我来", "row IDs restore source association even when provider replies in another order");
            Check(RowTranslation.Parse("[[GL0]]开门\n[[GL", rows, false).Single().Text == "开门", "partial protocol markers never appear on a subtitle");
            bool invalid = false; try { RowTranslation.Parse("[[GL0]]开门\n[[GL0]]跟我来", rows, true); } catch (InvalidOperationException) { invalid = true; }
            Check(invalid, "duplicate or incomplete row IDs are rejected instead of attaching translations by guessed order");
            var duplicate = new[] {
                new OcrLine { Text = "Open gate", X = 20, Y = 20, Width = 180, Height = 24, Confidence = .95 },
                new OcrLine { Text = "Open gate", X = 22, Y = 21, Width = 179, Height = 24, Confidence = .91 },
                new OcrLine { Text = "Open gate", X = 20, Y = 52, Width = 180, Height = 24, Confidence = .95 }
            };
            Check(OcrLayout.Normalize(duplicate).Count == 2, "overlapping Paddle copies collapse but repeated labels on separate rows survive");
            var cfg = new AppConfig { EnableStreaming = false };
            var window = new InlineOverlayWindow(cfg);
            var region = new Int32Rect(100, 160, 600, 200);
            var tight = new OcrOutcome { Lines = [
                new OcrLine { Text = "First", X = 20, Y = 20, Width = 160, Height = 24 },
                new OcrLine { Text = "Second", X = 20, Y = 40, Width = 160, Height = 24 },
                new OcrLine { Text = "Right label", X = 190, Y = 21, Width = 180, Height = 24 }] };
            window.Present(new SessionFrame(tight, region, "第一行较长的中文译文\n第二行译文也有一些长度\n右侧独立文字", true, false, 1));
            Check(window.VisibleBounds.Count == 3 && Disjoint(window.VisibleBounds), "overlapping source row heights and adjacent columns produce disjoint translation plates");
            await SnapshotAsync(window, "continuous-dense-alignment");
            var single = Layout(("Open gate", 20, 20));
            window.Present(new SessionFrame(single, region, "开", false, false, 2)); double width = window.FirstWidth;
            window.Present(new SessionFrame(single, region, "开门然后前往港口继续寻找同伴", true, false, 2));
            Check(window.FirstWidth >= width && !window.TextClipped, "longer subtitle expands into available space without truncation");
            window.FollowRegion(region, false); window.FollowRegion(region, true);
            Check(window.IsVisible, "a temporarily hidden source restores its subtitle without requiring changed OCR text");
            window.FollowRegion(new Int32Rect(100, 160, 720, 240), true);
            Check(window.IsVisible && window.FirstWidth > 0, "source resize reprojects the overlay instead of leaving it permanently hidden");
            window.Close();

            int phase = 0, samples = 0; var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new LayoutEngine(() => phase == 1 ? throw new IOException("synthetic temporary worker failure") : Layout((phase == 2 ? "Follow me" : "Open gate", 20, 20)));
            var recoveryCfg = new AppConfig { SourceLanguage = "en-US", EnhanceSmallText = false, PollIntervalMs = 250, OcrIntervalMs = 300 };
            recoveryCfg.Translator.Model = "recover-v13";
            await using (var session = new TranslationSession(recoveryCfg, region, true, false, true, IntPtr.Zero, engine, new RowTranslator(), _ => RegionSample(++samples)))
            {
                session.Frame += f => { if (f.Final && f.Text == "开门") first.TrySetResult(); if (f.Final && f.Text == "跟我来") recovered.TrySetResult(); };
                session.Start(); await first.Task.WaitAsync(TimeSpan.FromSeconds(4));
                phase = 1; await Task.Delay(1100); phase = 2;
                await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(session.NeedsOcr && session.ApiCalls == 2, "continuous capture resumes after worker failures without restarting the watch region");
            }
            // Three failed requests used to exhaust retry forever on unchanged text.
            var retry = new RecoveringTranslator(); var final = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            recoveryCfg.Translator.Model = "retry-v13";
            await using (var session = new TranslationSession(recoveryCfg, region, true, false, true, IntPtr.Zero,
                new LayoutEngine(() => Layout(("Open gate", 20, 20))), retry, _ => RegionSample(++samples)))
            {
                session.Frame += f => { if (f.Final && f.Text == "恢复完成") final.TrySetResult(); };
                session.Start(); await final.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Check(retry.Calls == 4 && session.NeedsOcr, "unchanged dialogue recovers after three service errors with bounded automatic backoff");
            }
            await TestCandidateDoesNotStealStreamAsync();
        }
        private static async Task TestCandidateDoesNotStealStreamAsync()
        {
            int phase = 0, samples = 0; var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var partialObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new List<SessionFrame>(); var translator = new SlowRows();
            var cfg = new AppConfig { SourceLanguage = "en-US", EnhanceSmallText = false, PollIntervalMs = 250, OcrIntervalMs = 300 };
            cfg.Translator.Model = "partial-layout-v13";
            var engine = new LayoutEngine(() =>
            {
                if (phase == 2) { partialObserved.TrySetResult(); return Layout(("Open gate", 20, 20)); }
                return phase == 0 ? Layout(("Open gate", 20, 20), ("Follow me", 20, 60)) : Layout(("Open gate", 20, 20), ("New line", 20, 60));
            });
            await using var session = new TranslationSession(cfg, new Int32Rect(0, 0, 400, 180), true, false, true, IntPtr.Zero, engine, translator, _ => RegionSample(++samples));
            session.Frame += f => { lock (observed) observed.Add(f); if (f.Final && f.Text.Contains("新内容")) completed.TrySetResult(); };
            session.Start(); await translator.First.Task.WaitAsync(TimeSpan.FromSeconds(3)); phase = 1;
            await translator.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            phase = 2; await partialObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)); await Task.Delay(35); phase = 1;
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (observed)
                Check(observed.Any(f => !f.Final && f.Text.Contains("新")) && observed.Last(f => f.Final).Text == "开门\n新内容",
                    "a transient partial OCR layout cannot steal or suppress the active new-line translation stream");
        }
        private sealed class RecoveringTranslator : ITranslator
        {
            internal int Calls;
            public string Name => "recovering";
            public Task<TranslateResult> TranslateAsync(string text, string? from, string to, CancellationToken ct) => TranslateStreamingAsync(text, from, to, null, ct);
            public Task<TranslateResult> TranslateStreamingAsync(string text, string? from, string to, Action<string>? onText, CancellationToken ct)
            { if (++Calls <= 3) throw new IOException("synthetic service outage"); onText?.Invoke("恢复完成"); return Task.FromResult(new TranslateResult { Text = "恢复完成" }); }
        }
        private sealed class SlowRows : ITranslator
        {
            internal readonly TaskCompletionSource First = new(TaskCreationOptions.RunContinuationsAsynchronously), SecondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public string Name => "slow-rows";
            public Task<TranslateResult> TranslateAsync(string text, string? from, string to, CancellationToken ct) => TranslateStreamingAsync(text, from, to, null, ct);
            public async Task<TranslateResult> TranslateStreamingAsync(string text, string? from, string to, Action<string>? onText, CancellationToken ct)
            {
                bool second = text.Contains("New line");
                if (second) { SecondStarted.TrySetResult(); await Task.Delay(700, ct); onText?.Invoke("开门\n新"); await Task.Delay(150, ct); }
                string output = second ? "开门\n新内容" : "开门\n跟我来";
                if (!second) First.TrySetResult(); return new TranslateResult { Text = output };
            }
        }
        private static async Task TestContinuousChurnAsync()
        {
            var clock = Stopwatch.StartNew(); int samples = 0, finals = 0, stale = 0, overlaps = 0, errors = 0;
            var cfg = new AppConfig { SourceLanguage = "en-US", EnhanceSmallText = false, PollIntervalMs = 250, OcrIntervalMs = 300 };
            cfg.Translator.Model = "churn-v13";
            using var presenter = new TranslationPresenter(cfg, "inline", true);
            var process = Process.GetCurrentProcess(); long heap = GC.GetTotalMemory(true), handles = process.HandleCount;
            var engine = new LayoutEngine(() =>
            {
                int tick = (int)(clock.ElapsedMilliseconds / 750);
                if (tick > 0 && tick % 31 == 0) throw new IOException("synthetic intermittent OCR failure");
                return Layout(($"Dialogue {tick}", 20 + tick % 3, 20), ("Open gate", 20, 48));
            });
            await using var session = new TranslationSession(cfg, new Int32Rect(120, 180, 400, 180), true, false, true, IntPtr.Zero, engine, new ChurnTranslator(), _ => RegionSample(++samples));
            session.Frame += frame => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (frame.Version != session.CurrentVersion) return;
                try
                {
                    presenter.Present(frame);
                    if (!Disjoint(presenter.InlineBounds)) overlaps++;
                    if (frame.Final && frame.Text.Length > 0)
                    {
                        finals++;
                        if (frame.Anchors?.Any(a => a.Source.Text.StartsWith("Dialogue ") && a.Text != a.Source.Text.Replace("Dialogue", "对白")) == true) stale++;
                    }
                }
                catch { errors++; }
            });
            session.Start(); await Task.Delay(TimeSpan.FromSeconds(180)); await session.DisposeAsync();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            long heapDelta = GC.GetTotalMemory(true) - heap; process.Refresh();
            var report = new { durationSeconds = clock.Elapsed.TotalSeconds, finals, stale, overlaps, errors, session.OcrCalls, session.ApiCalls,
                managedHeapDeltaBytes = heapDelta, handleDelta = process.HandleCount - handles, synthetic = true };
            File.WriteAllText(Path.Combine(Output, "continuous-churn.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Check(finals > 80 && stale == 0 && overlaps == 0 && errors == 0 && session.ApiCalls < 300 && heapDelta < 32_000_000,
                "180s continuous dialogue churn recovers OCR faults, rejects stale output, keeps disjoint subtitles and bounded managed memory");
        }
        private sealed class ChurnTranslator : ITranslator
        {
            private int _calls;
            public string Name => "churn";
            public Task<TranslateResult> TranslateAsync(string text, string? from, string to, CancellationToken ct) => TranslateStreamingAsync(text, from, to, null, ct);
            public async Task<TranslateResult> TranslateStreamingAsync(string text, string? from, string to, Action<string>? onText, CancellationToken ct)
            {
                // Deliberately ignore cancellation occasionally to exercise stale-result rejection.
                await Task.Delay(++_calls % 9 == 0 ? 1100 : 90);
                string output = text.Replace("Dialogue", "对白").Replace("Open gate", "开门"); onText?.Invoke(output);
                return new TranslateResult { Text = output };
            }
        }
        private static async Task TestPaddleLayoutAsync()
        {
            using var engine = new PaddleOcrEngine(2);
            var inline = new InlineOverlayWindow(new AppConfig());
            var region = new Int32Rect(100, 100, 800, 180);
            int collisions = 0, recognized = 0;
            try
            {
                for (int i = 0; i < 24; i++)
                {
                    using var bitmap = new Bitmap(800, 180);
                    using (var graphics = Graphics.FromImage(bitmap))
                    using (var font = new Font("Segoe UI", 30, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        graphics.Clear(Color.FromArgb(24, 30, 40));
                        graphics.DrawString("Open the gate", font, System.Drawing.Brushes.White, 24 + i % 3, 18);
                        graphics.DrawString("Follow me to the harbor", font, System.Drawing.Brushes.White, 24 + i % 3, 63);
                        graphics.DrawString($"HP {100 + i}", font, System.Drawing.Brushes.White, 560, 19);
                    }
                    var result = await engine.RecognizeAsync(bitmap, "en-US", CancellationToken.None);
                    var layout = new OcrOutcome { Lines = OcrLayout.Normalize(result.Lines), LanguageTag = "en-US" };
                    if (layout.Lines.Count >= 3) recognized++;
                    var anchors = layout.Lines.Select(l => new AnchoredTranslation(l,
                        l.Text.Contains("gate", StringComparison.OrdinalIgnoreCase) ? "打开城门" :
                        l.Text.Contains("harbor", StringComparison.OrdinalIgnoreCase) ? "跟我前往港口" : "生命值 " + (100 + i))).ToArray();
                    inline.Present(new SessionFrame(layout, region, string.Join("\n", anchors.Select(a => a.Text)), true, false, i, Anchors: anchors));
                    if (!Disjoint(inline.VisibleBounds)) collisions++;
                    if (i == 23)
                    {
                        bitmap.Save(Path.Combine(Output, "paddle-alignment-source.png"));
                        await SnapshotAsync(inline, "paddle-alignment-overlay");
                        // Capture only a synthetic source owned by this diagnostic, with
                        // the diagnostic overlay temporarily visible to screen capture.
                        var image = new System.Windows.Media.Imaging.BitmapImage();
                        image.BeginInit(); image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        image.UriSource = new Uri(Path.Combine(Output, "paddle-alignment-source.png")); image.EndInit(); image.Freeze();
                        var backdrop = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, ShowActivated = false, ShowInTaskbar = false,
                            Content = new System.Windows.Controls.Image { Source = image, Stretch = System.Windows.Media.Stretch.Fill }, Topmost = true };
                        backdrop.Show(); Native.WindowUtilities.Position(backdrop, region); inline.Owner = backdrop;
                        inline.Hide(); inline.Show(); inline.FollowRegion(region, true);
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(inline).Handle;
                        Native.NativeMethods.SetWindowDisplayAffinity(hwnd, 0);
                        await Task.Delay(180); Native.GlassMaterials.DwmFlush();
                        using (var screenshot = Capture.ScreenCapture.CaptureRegion(region.X, region.Y, region.Width, region.Height))
                            screenshot.Save(Path.Combine(Output, "paddle-alignment-composite.png"));
                        Native.NativeMethods.SetWindowDisplayAffinity(hwnd, 0x11); inline.Owner = null; backdrop.Close();
                    }
                }
                Check(recognized == 24 && collisions == 0, "24 real Paddle changing three-label frames produce positioned non-overlapping subtitles");
                File.WriteAllText(Path.Combine(Output, "paddle-alignment.json"), JsonSerializer.Serialize(new { frames = 24, recognized, collisions, syntheticSource = true }));
            }
            finally { inline.Close(); }
        }
    }
}
