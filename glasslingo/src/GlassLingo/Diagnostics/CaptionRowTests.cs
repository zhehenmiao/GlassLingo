using System;
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
        private static async Task TestCaptionRowsAsync()
        {
            OcrLine Box(string text, int x, int width, int y = 20, int height = 60) => new() { Text = text, X = x, Y = y, Width = width, Height = height, Confidence = .97 };
            var fragments = new[] { Box("I know,", 100, 230), Box("but that hurts, right?", 370, 520), Box("right?", 980, 190, 25, 55) };
            Check(OcrLayout.Normalize(fragments).Count == 3, "screenshot-shaped subtitle reproduces the three boxes missed by the old short-tail heuristic");
            var sentence = OcrLayout.Normalize(fragments, captionRows: true);
            Check(sentence.Count == 1 && sentence[0].Text == "I know, but that hurts, right? right?" && sentence[0].Width == 1070,
                "caption row groups all fragments even after punctuation and a larger inter-word gap");
            Check(OcrLayout.Normalize([fragments[0], Box("Next line", 370, 520, 110)], true).Count == 2 &&
                OcrLayout.Normalize([fragments[0], Box("Distant", 1000, 190)], true).Count == 2,
                "caption grouping preserves separate baselines and genuinely distant columns");
            foreach (string mode in new[] { "game", "movie" })
            {
                var cfg = new AppConfig { SourceLanguage = "en-US", EnhanceSmallText = false, PerformanceMode = mode, OverlayBackgroundColor = "#185986", OverlayOpacity = 1 };
                cfg.Translator.Model = "caption-row-" + mode;
                var translator = new CaptionTranslator(); var done = new TaskCompletionSource<SessionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var session = new TranslationSession(cfg, new Int32Rect(100, 300, 1280, 143), true, false, true, IntPtr.Zero,
                    new LayoutEngine(() => new OcrOutcome { Lines = fragments, LanguageTag = "en-US" }), translator,
                    _ => { var bmp = new Bitmap(1280, 143, System.Drawing.Imaging.PixelFormat.Format32bppArgb); using var g = Graphics.FromImage(bmp); g.Clear(Color.Black); return bmp; });
                session.Frame += f => { if (f.Final && f.Text.Length > 0) done.TrySetResult(f); };
                session.Start(); var final = await done.Task.WaitAsync(TimeSpan.FromSeconds(4));
                Check(final.Ocr.Lines.Count == 1 && translator.Source == "I know, but that hurts, right? right?" && session.ApiCalls == 1,
                    mode + " horizontal watch subtitle is translated as a whole sentence instead of three independent rows");
                var overlay = new InlineOverlayWindow(cfg);
                try
                {
                    overlay.Present(final); await Task.Delay(130);
                    Check(overlay.VisualCount == 1 && overlay.VisibleBounds.Count == 1 && !overlay.TextClipped,
                        mode + " repeated final phrase shares one centered translation and continuous backing");
                    await SnapshotAsync(overlay, mode + "-caption-one-backing");
                }
                finally { overlay.Close(); }
                var cachedOverlay = new InlineOverlayWindow(cfg);
                try
                {
                    var old = new OcrOutcome { Lines = fragments, LanguageTag = "en-US" };
                    var anchors = new[] { new AnchoredTranslation(fragments[0], "我知道，"), new AnchoredTranslation(fragments[1], "但那很伤人，对吧"), new AnchoredTranslation(fragments[2], "对吧？") };
                    var anchored = new SessionFrame(old, new Int32Rect(100, 300, 1280, 143), string.Join("\n", anchors.Select(a => a.Text)), true, false, 2, Anchors: anchors);
                    cachedOverlay.Present(anchored);
                    Check(cachedOverlay.VisualCount == 1 && cachedOverlay.VisibleBounds.Count == 1,
                        mode + " legacy fragmented anchors are rendered as one complete caption backing");
                    cachedOverlay.Present(anchored with { Final = false, Anchors = anchors.Take(2).ToArray(), Text = "我知道，但那很伤人，对吧" });
                    Check(cachedOverlay.VisualCount == 1 && cachedOverlay.VisibleBounds.Count == 1,
                        mode + " incomplete streamed anchors cannot recreate a drifting tail plate");
                    cachedOverlay.Present(anchored with { Final = false, Anchors = anchors.Skip(2).ToArray(), Text = "对吧？" });
                    double dpi = Native.NativeMethods.ScaleForDpi(Native.NativeMethods.GetDpiForWindow(new System.Windows.Interop.WindowInteropHelper(cachedOverlay).Handle));
                    Check(cachedOverlay.FirstWidth >= 1070 / dpi && cachedOverlay.VisualCount == 1,
                        mode + " even a suffix-only cache hit remains attached to the whole caption instead of floating at its old endpoint");
                }
                finally { cachedOverlay.Close(); }
            }
        }
        private sealed class CaptionTranslator : ITranslator
        {
            internal string Source = "";
            public string Name => "caption-layout";
            public Task<TranslateResult> TranslateAsync(string text, string? from, string to, CancellationToken ct) => TranslateStreamingAsync(text, from, to, null, ct);
            public Task<TranslateResult> TranslateStreamingAsync(string text, string? from, string to, Action<string>? onText, CancellationToken ct)
            { Source = text; const string output = "我知道，但那很伤人，对吧，对吧？"; onText?.Invoke(output); return Task.FromResult(new TranslateResult { Text = output }); }
        }
        internal static async Task InspectCaptionAsync(string path)
        {
            Directory.CreateDirectory(Output);
            using var bitmap = new Bitmap(path); using var engine = new PaddleOcrEngine(4);
            var raw = await engine.RecognizeAsync(bitmap, "auto", CancellationToken.None);
            var grouped = OcrLayout.Normalize(raw.Lines, true);
            File.WriteAllText(Path.Combine(Output, "attached-caption-ocr.json"), JsonSerializer.Serialize(new { inputWidth = bitmap.Width, inputHeight = bitmap.Height,
                raw = raw.Lines, grouped, localOnly = true }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
