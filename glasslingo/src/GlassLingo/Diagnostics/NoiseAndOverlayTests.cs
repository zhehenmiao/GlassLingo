using System;
using System.Linq;
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
        private static async Task TestNoiseAndOverlayAsync()
        {
            OcrLine Line(string text, double? score = null, int width = 160, int height = 20) => new() { Text = text, Confidence = score, Width = width, Height = height };
            Check(new[] { "◉ ▷ ◇", "||| / /", "-----", "[?]", "IIIIII" }.All(s => !OcrNoiseFilter.IsLikelyText(Line(s), true)),
                "symbol-only icons, uncertain placeholders and repeated Latin strokes are rejected");
            Check(!OcrNoiseFilter.IsLikelyText(Line("Open gate", .4), true) && !OcrNoiseFilter.IsLikelyText(Line("口", .5, 20), true),
                "low-confidence OCR and uncertain short glyphs are filtered before translation");
            Check(!OcrNoiseFilter.IsLikelyText(Line("一", null, 160, 3), true) && !OcrNoiseFilter.IsLikelyText(Line("O", null, 200, 20), true),
                "thin line art and implausibly wide single-glyph boxes are rejected");
            Check(new[] { "こんにちは世界", "한국어", "确认", "12345", "11111", "App.xaml.cs", "D:/games/save", "C++" }.All(s => OcrNoiseFilter.IsLikelyText(Line(s, .96, Math.Max(20, s.Length * 10)), true)) &&
                OcrNoiseFilter.IsLikelyText(Line("E", .97, 14), true) && OcrNoiseFilter.IsLikelyText(Line("和", null, 20), true),
                "real multilingual text, key labels, numbers and technical names remain eligible");
            var cfg = new AppConfig { SourceLanguage = "en-US" }; cfg.Translator.Model = "noise-filter-v011";
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var noise = new TranslationSession(cfg, new Int32Rect(0, 0, 400, 180), false, false, true, IntPtr.Zero,
                new LayoutEngine(() => new OcrOutcome { Lines = [Line("◉ ◇ >"), Line("Open gate", .3)] }), new RowTranslator(), _ => RegionSample(0)))
            {
                noise.Frame += f => { if (f.Final && f.Ocr.IsEmpty) done.TrySetResult(); };
                noise.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(noise.ApiCalls == 0, "a graphic-only OCR result causes no translation API request");
            }
            foreach (bool coordinates in new[] { true, false })
            {
                using var presenter = new TranslationPresenter(new AppConfig { EnableStreaming = false }, "inline", false);
                int before = Application.Current.Windows.OfType<ResultOverlayWindow>().Count();
                string notice = ""; presenter.Notice += value => notice = value;
                var ocr = new OcrOutcome { Lines = Layout(("Open gate", 10, 10), ("Follow me", 240, 50)).Lines, HasLayout = coordinates };
                presenter.Present(new SessionFrame(ocr, new Int32Rect(50, 50, 400, 180), "one merged result", true, false, 1));
                presenter.Present(new SessionFrame(ocr, new Int32Rect(50, 50, 400, 180), "another merged result", true, false, 2));
                Check(!presenter.PopupVisible && Application.Current.Windows.OfType<ResultOverlayWindow>().Count() == before && notice.Length > 0,
                    coordinates ? "inline layout mismatch never opens a fallback popup" : "coordinate-free OCR never opens a popup while inline mode is selected");
                presenter.SetMode("sidebar");
                Check(presenter.PopupVisible, "the complete result remains available after manually selecting sidebar");
            }
        }
    }
}
