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

namespace GlassLingo.Diagnostics
{
    internal static partial class SelfTests
    {
        private static async Task TestResponsiveCaptionsAsync()
        {
            var fast = new WatchTextStabilizer(true); fast.Observe("Please open the gate now", 0);
            Check(fast.Observe("Follow the team to the harbor", 100, true), "trusted substantially changed cinema text advances without a second inference");
            var trustedGame = new WatchTextStabilizer(); trustedGame.Observe("Please open the gate now", 0);
            Check(trustedGame.Observe("Follow the team to the harbor", 100, true), "very confident game caption changes also avoid redundant confirmation without faster polling");
            var uncertain = new WatchTextStabilizer(true); uncertain.Observe("Please open the gate now", 0);
            Check(!uncertain.Observe("Follow the team to the harbor", 100, false), "uncertain cinema OCR still requires confirmation");
            fast.Observe("", 200, true);
            Check(!fast.Observe("", 300, true), "trusted fast path never treats a brief missing subtitle as confirmed disappearance");
            var stabilizer = new CaptionStability(); var region = new Int32Rect(100, 100, 800, 200);
            var initial = stabilizer.Observe(new object(), region, 1, 30, 400, 60);
            bool stable = true;
            for (int i = 0; i < 20; i++)
            {
                var next = stabilizer.Observe(new object(), region, 1, 28 + i % 5, 397 + i % 7, 58 + i % 5);
                stable &= next == initial;
            }
            Check(stable, "minor OCR height and box-center jitter keeps caption font and anchor fixed");
            var evidence = new object(); stabilizer.Observe(evidence, region, 1, 50, 560, 100);
            for (int i = 0; i < 40; i++) stabilizer.Observe(evidence, region, 1, 50, 560, 100);
            Check(stabilizer.Observe(evidence, region, 1, 50, 560, 100) == initial,
                "streaming repaint frames do not count as repeated OCR evidence for a geometry change");
            stabilizer.Observe(new object(), region, 1, 50, 560, 100);
            Check(stabilizer.Observe(new object(), region, 1, 50, 560, 100).Font == 50,
                "a real sustained font/layout change is adopted after three independent OCR layouts");
            foreach (string mode in new[] { "game", "movie" })
            {
                var overlay = new InlineOverlayWindow(new AppConfig { PerformanceMode = mode }, captionMode: true);
                try
                {
                    var source = new OcrOutcome { Lines = [new OcrLine { Text = "A stable source subtitle", X = 120, Y = 60, Width = 500, Height = 36 }] };
                    var f = new SessionFrame(source, new Int32Rect(500, 200, 1000, 300), "字幕", false, false, 1);
                    overlay.Present(f);
                    double font = overlay.RenderedFontSize, center = overlay.FirstLeft + overlay.FirstWidth / 2, top = overlay.FirstTop;
                    bool fixedLayout = true;
                    for (int i = 3; i <= 45; i++)
                    {
                        overlay.Present(f with { Text = new string('字', i) });
                        fixedLayout &= overlay.RenderedFontSize == font && Math.Abs(overlay.FirstLeft + overlay.FirstWidth / 2 - center) < .1 && Math.Abs(overlay.FirstTop - top) < .1;
                    }
                    Check(fixedLayout && overlay.VisualCount == 1, mode + " growing streamed captions retain font, center and first-line baseline without splitting");
                    overlay.Present(f with { Text = new string('字', 100) });
                    Check(Math.Abs(overlay.FirstTop - top) < .1 && Math.Abs(overlay.FirstLeft + overlay.FirstWidth / 2 - center) < .1 && overlay.RenderedFontSize == font && !overlay.TextClipped,
                        mode + " a streamed second line grows downward without shifting the first-line baseline");
                    await SnapshotAsync(overlay, mode + "-stable-stream-layout");
                    overlay.Present(new SessionFrame(Layout(("Open gate", 20, 20), ("Follow me", 320, 100)), new Int32Rect(100, 100, 800, 250), "打开城门\n跟我来", true, true, 2));
                    Check(overlay.VisibleBounds.Count == 2 && Disjoint(overlay.VisibleBounds),
                        mode + " fullscreen positional overlays keep independent anchors instead of inheriting the single-caption stabilizer");
                }
                finally { overlay.Close(); }
            }
            var report = new List<object>();
            foreach (string mode in new[] { "game", "movie" })
            {
                var engine = new ResponsiveOcr(); var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var cfg = new AppConfig { SourceLanguage = "en-US", PerformanceMode = mode, SmallTextRowHeight = 24 };
                cfg.Translator.Model = "responsive-caption-" + mode;
                int captures = 0, acceptedCalls = 0;
                await using var session = new TranslationSession(cfg, new Int32Rect(0, 0, 400, 180), true, false, true, IntPtr.Zero,
                    engine, new RowTranslator(), _ => RegionSample(++captures));
                session.Frame += f => { if (f.Text.Length == 0 && f.Ocr.PlainText == "Follow me") acceptedCalls = engine.Calls;
                    if (f.Final && f.Text == "开门") first.TrySetResult(); if (f.Final && f.Text == "跟我来") changed.TrySetResult(); };
                session.Start(); await first.Task.WaitAsync(TimeSpan.FromSeconds(4));
                int before = engine.Calls;
                var watch = Stopwatch.StartNew(); engine.Next = true;
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(4));
                report.Add(new { mode, changeToTranslationMs = watch.ElapsedMilliseconds, engineCallsUntilAccepted = acceptedCalls - before, totalEngineCalls = engine.Calls, session.OcrCalls });
                if (mode == "movie") Check(engine.Calls <= session.OcrCalls + 1 && acceptedCalls - before <= 2,
                    "clear small cinema text skips enlargement and does not require two recognition passes to translate a new sentence");
            }
            File.WriteAllText(Path.Combine(Output, "responsive-caption.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            await TestCaptionReflowAsync();
        }
        private static async Task TestCaptionReflowAsync()
        {
            int phase = 0, samples = 0;
            var cfg = new AppConfig { SourceLanguage = "en-US", PerformanceMode = "movie", EnhanceSmallText = false };
            cfg.Translator.Model = "reflow-caption-test";
            var translator = new ReflowTranslator(); var final = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var session = new TranslationSession(cfg, new Int32Rect(0, 0, 400, 180), true, false, true, IntPtr.Zero,
                new LayoutEngine(() => new OcrOutcome { LanguageTag = "en-US", Lines = [new OcrLine { Text = phase == 0 ? "Open the gate\nand follow me" : "Open the gate and follow me", X = 20, Y = 20, Width = 300, Height = 30, Confidence = .98 }] }),
                translator, _ => RegionSample(++samples));
            session.Frame += f => { if (f.Final && f.Text == "打开城门，跟我来。") final.TrySetResult(); };
            session.Start(); await translator.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); phase = 1;
            await final.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(translator.Calls == 1 && session.ApiCalls == 1, "unchanged caption reflow does not cancel or restart an in-flight whole-sentence translation");
        }
        private sealed class ReflowTranslator : GlassLingo.Translate.ITranslator, GlassLingo.Translate.IRowTranslator
        {
            internal int Calls;
            internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public string Name => "reflow";
            public bool SupportsRows => true;
            public Task<GlassLingo.Translate.TranslateResult> TranslateAsync(string text, string? from, string to, CancellationToken ct) => TranslateStreamingAsync(text, from, to, null, ct);
            public async Task<GlassLingo.Translate.TranslateResult> TranslateStreamingAsync(string text, string? from, string to, Action<string>? onText, CancellationToken ct)
            {
                Calls++; Started.TrySetResult(); await Task.Delay(650, ct);
                onText?.Invoke("打开城门，跟我来。"); return new GlassLingo.Translate.TranslateResult { Text = "打开城门，跟我来。" };
            }
            public Task<GlassLingo.Translate.TranslateResult> TranslateRowsAsync(IReadOnlyList<string> rows, string? from, string to, Action<IReadOnlyList<GlassLingo.Translate.TranslatedRow>>? onRows, CancellationToken ct)
                => throw new InvalidOperationException("caption must not use row protocol");
        }
        private sealed class ResponsiveOcr : IOcrEngine
        {
            internal int Calls; internal volatile bool Next;
            public string Name => "responsive-test";
            public IReadOnlyList<string> Languages => ["en-US"];
            public bool RetryAllLanguagesWhenEmpty => false;
            public bool UsesLanguageSpecificModels => false;
            public bool Supports(string tag) => true;
            public async Task<OcrOutcome> RecognizeAsync(Bitmap bitmap, string language, CancellationToken ct)
            {
                Calls++; bool next = Next; await Task.Delay(60, ct);
                return new OcrOutcome { LanguageTag = "en-US", Lines = [new OcrLine { Text = next ? "Follow me" : "Open gate", X = 20, Y = 20, Width = 180, Height = 20, Confidence = .97 }] };
            }
        }
    }
}
