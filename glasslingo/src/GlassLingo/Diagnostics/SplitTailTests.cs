using System;
using System.Linq;
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
        private static async Task TestSplitTailAsync()
        {
            OcrLine Body(string text, int x, int y, int width) => new() { Text = text, X = x, Y = y, Width = width, Height = 20, Confidence = .96 };
            var head = Body("请跟随队伍前往港", 20, 20, 200);
            var tail = Body("口。", 225, 21, 40);
            var source = new[] { head, tail };
            var joined = OcrLayout.Normalize(source);
            Check(joined.Count == 1 && joined[0].Text == "请跟随队伍前往港口。" && joined[0].X == 20 && joined[0].Width == 245,
                "adjacent aligned short CJK suffix is reunited with its sentence before translation");
            var latin = OcrLayout.Normalize([Body("Follow me to the", 10, 10, 200), Body("harbor", 217, 10, 60)]);
            Check(latin.Count == 1 && latin[0].Text == "Follow me to the harbor", "Latin suffix joining preserves a word boundary");
            Check(OcrLayout.Normalize([head, Body("口。", 300, 20, 40)]).Count == 2 &&
                OcrLayout.Normalize([head, Body("口。", 225, 45, 40)]).Count == 2 &&
                OcrLayout.Normalize([Body("Delete saved game", 20, 20, 200), Body("OK", 225, 20, 40)]).Count == 2,
                "spaced labels, another baseline and independent action buttons are never merged by the short-tail rule");
            Check(OcrLayout.Normalize([Body("This is complete.", 20, 20, 200), Body("Next", 225, 20, 40)]).Count == 2,
                "a completed sentence and the next label remain distinct");
            foreach (string mode in new[] { "game", "movie" })
            {
                var cfg = new AppConfig { SourceLanguage = "zh-Hans-CN", EnhanceSmallText = false, PerformanceMode = mode };
                cfg.Translator.Model = "tail-regression-" + mode;
                var done = new TaskCompletionSource<SessionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
                var translator = new TailTranslator();
                await using var session = new TranslationSession(cfg, new Int32Rect(400, 300, 400, 100), false, false, true, IntPtr.Zero,
                    new LayoutEngine(() => new OcrOutcome { Lines = source, LanguageTag = "zh-Hans-CN" }), translator, _ => RegionSample(0));
                session.Frame += f => { if (f.Final && f.Text.Length > 0) done.TrySetResult(f); };
                session.Start(); var final = await done.Task.WaitAsync(TimeSpan.FromSeconds(4));
                Check(final.Ocr.Lines.Count == 1 && !translator.Source.Contains('\n') && translator.Source == "请跟随队伍前往港口。",
                    mode + " split-tail source reaches the translator as one complete sentence");
                var overlay = new InlineOverlayWindow(cfg);
                try
                {
                    overlay.Present(final);
                    Check(overlay.VisualCount == 1 && overlay.VisibleBounds.Count == 1 && !overlay.TextClipped &&
                        final.Anchors?.Count is null or 1,
                        mode + " complete translation and suffix share one centered visual and one backing plate");
                    await Task.Delay(120); await SnapshotAsync(overlay, mode + "-reunited-tail");
                }
                finally { overlay.Close(); }
            }
        }
        private sealed class TailTranslator : ITranslator
        {
            internal string Source = "";
            public string Name => "tail-regression";
            public Task<TranslateResult> TranslateAsync(string text, string? from, string to, CancellationToken ct)
                => TranslateStreamingAsync(text, from, to, null, ct);
            public Task<TranslateResult> TranslateStreamingAsync(string text, string? from, string to, Action<string>? onText, CancellationToken ct)
            {
                Source = text; const string output = "请跟随队伍一起前往港口，不要掉队。";
                onText?.Invoke(output); return Task.FromResult(new TranslateResult { Text = output });
            }
        }
    }
}
