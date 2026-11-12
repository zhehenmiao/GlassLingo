using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
        private static async Task TestAuditAsync()
        {
            Check(App.AppVersion == System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion?.Split('+')[0],
                "displayed version matches the shipped executable version");
            var cfg = new AppConfig { OcrProvider = "openai-vision", SourceLanguage = "en-US" };
            var completed = new TaskCompletionSource<SessionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var session = new TranslationSession(cfg, new Int32Rect(0, 0, 400, 180), false, false, false, IntPtr.Zero,
                new LayoutEngine(() => new OcrOutcome { HasLayout = false, Lines = new[] { "Open gate", "Open gate", "Open gate now" }
                    .Select(s => new OcrLine { Text = s, Width = 400, Height = 180 }).ToArray() }), new RowTranslator(), _ => RegionSample(0)))
            {
                session.Frame += f => { if (f.Final && f.Text.Length > 0) completed.TrySetResult(f); };
                session.Start(); var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(4));
                Check(result.Ocr.Lines.Count == 3 && result.Text == "Open gate\nOpen gate\nOpen gate now",
                    "vision OCR preserves repeated and contained lines despite placeholder geometry");
            }
            foreach (double threshold in new[] { 0d, 24d })
            {
                var engine = new AuditOcr();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var session = new TranslationSession(new AppConfig { SourceLanguage = "en-US", SmallTextRowHeight = threshold },
                    new Int32Rect(0, 0, 400, 180), false, false, false, IntPtr.Zero, engine, new RowTranslator(), _ => RegionSample(0));
                session.Frame += f => { if (f.Final && f.Text.Length > 0) ready.TrySetResult(); };
                session.Start(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(4));
                Check(engine.Calls == (threshold == 0 ? 1 : 2), threshold == 0 ? "zero small-text threshold disables enlarged retry" : "configured 24px threshold retries a 20px source row");
            }
            var gated = new AuditOcr { GateFirst = true };
            var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int stale = 0, captures = 0;
            await using (var session = new TranslationSession(new AppConfig { SourceLanguage = "en-US", EnhanceSmallText = false, PollIntervalMs = 250 },
                new Int32Rect(0, 0, 400, 180), true, false, false, IntPtr.Zero, gated, new RowTranslator(), _ => RegionSample(++captures)))
            {
                session.Frame += f => { if (f.Text == "old") stale++; if (f.Final && f.Text == "fresh") fresh.TrySetResult(); };
                session.Start(); await gated.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                session.PauseResume(); session.PauseResume(); gated.Release.TrySetResult();
                await fresh.Task.WaitAsync(TimeSpan.FromSeconds(4));
                Check(stale == 0, "OCR captured before pause is discarded even after an immediate resume");
            }
            var presenter = new TranslationPresenter(new AppConfig { EnableStreaming = false }, "inline", true);
            presenter.Dispose(); presenter.Present(new SessionFrame(Layout(("Open gate", 20, 20)), new Int32Rect(0, 0, 400, 180), "开门", true, false, 1));
            Check(!presenter.InlineVisible && !presenter.PopupVisible, "late frames cannot recreate disposed overlay windows");
        }
        private sealed class AuditOcr : IOcrEngine
        {
            internal int Calls; internal bool GateFirst;
            internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public string Name => "audit";
            public IReadOnlyList<string> Languages => ["en-US"];
            public bool RetryAllLanguagesWhenEmpty => false;
            public bool UsesLanguageSpecificModels => false;
            public bool Supports(string tag) => true;
            public async Task<OcrOutcome> RecognizeAsync(Bitmap bitmap, string tag, CancellationToken ct)
            {
                int call = ++Calls;
                if (GateFirst && call == 1) { Started.TrySetResult(); await Release.Task.WaitAsync(ct); }
                return new OcrOutcome { Lines = [new OcrLine { Text = GateFirst ? call == 1 ? "old" : "fresh" : "Sample text", Width = 180, Height = 20 }], LanguageTag = tag };
            }
        }
    }
}
