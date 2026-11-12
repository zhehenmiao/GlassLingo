using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GlassLingo.Config;
using GlassLingo.Native;
using GlassLingo.Ocr;
using GlassLingo.Overlay;
using GlassLingo.Sessions;

namespace GlassLingo.Diagnostics
{
    internal static partial class SelfTests
    {
        private static async Task TestMovieModeAsync()
        {
            var cfg = new AppConfig { PerformanceMode = "movie", EnableStreaming = false };
            Check(cfg.MovieMode && cfg.EffectivePaddleThreads == 4 && new AppConfig().EffectivePaddleThreads == 2, "cinema uses the maximum supported four OCR threads while game retains its configured budget");
            var fast = new WatchTextStabilizer(true); var game = new WatchTextStabilizer();
            fast.Observe("Open gate", 0); game.Observe("Open gate", 0);
            fast.Observe("Follow me", 100); game.Observe("Follow me", 100);
            Check(fast.Observe("Follow me", 160) && !game.Observe("Follow me", 160), "cinema confirms a changed subtitle sooner while game stability remains unchanged");
            var movie = new InlineOverlayWindow(cfg); var regular = new InlineOverlayWindow(new AppConfig());
            try
            {
                var ocr = new OcrOutcome { Lines = [new OcrLine { Text = "Follow me to the harbor", X = 80, Y = 50, Width = 500, Height = 48 }] };
                var frame = new SessionFrame(ocr, new Int32Rect(100, 100, 900, 200), "跟我前往港口", true, false, 1);
                movie.Present(frame); regular.Present(frame);
                double scale = NativeMethods.ScaleForDpi(NativeMethods.GetDpiForWindow(new WindowInteropHelper(movie).Handle));
                double size = movie.RenderedFontSize;
                Check(Math.Abs(size - Math.Round(48 / scale)) < 1 && size > regular.RenderedFontSize, "cinema overlay matches source pixel height rather than the game font cap");
                movie.Present(frame with { Text = "跟我前往港口，寻找同行的伙伴", Final = false });
                Check(movie.RenderedFontSize == size && movie.VisibleBounds.Count == 1, "cinema keeps one stable-size subtitle as translation grows");
                movie.Present(frame with { Ocr = new OcrOutcome { Lines = [new OcrLine { Text = "Follow me to the harbor", X = 81, Y = 50, Width = 500, Height = 48 }] } });
                Check(movie.RenderedFontSize == size, "fresh OCR geometry does not restore the game font cap during cinema playback");
                await Task.Delay(150); await SnapshotAsync(movie, "movie-source-size-subtitle");
            }
            finally { movie.Close(); regular.Close(); }
            long low = await MovieLatencyAsync(false), high = await MovieLatencyAsync(true);
            File.WriteAllText(Path.Combine(Output, "movie-response.json"), JsonSerializer.Serialize(new { gameMs = low, movieMs = high, syntheticOcrAndTranslator = true }, new JsonSerializerOptions { WriteIndented = true }));
            Check(high < low && high < 600, "cinema detects and translates a synthetic subtitle change faster than the game profile");
            var shell = new Shell.ShellWindow((App)Application.Current); shell.Refresh(cfg);
            await SnapshotAsync(shell, "movie-mode-home");
            Check((string?)((ComboBox)shell.FindName("PerformanceModeCombo")).SelectedValue == "movie", "home surface clearly shows the active cinema mode");
            var settings = shell.OpenSettings(cfg, true);
            Check((string?)((ComboBox)settings.FindName("PerformanceModeCombo")).SelectedValue == "movie", "settings preserve the cinema preference");
            settings.Close(); shell.Hide();
            var app = (App)Application.Current;
            await app.ChangePerformanceModeAsync("movie");
            Check(app.Config.MovieMode, "main performance switch applies cinema mode without a settings dialog");
            await app.ChangePerformanceModeAsync("game");
            Check(!app.Config.MovieMode && app.Config.EffectivePaddleThreads == 2, "switching back restores the saved low-usage game thread budget");
        }
        private static async Task<long> MovieLatencyAsync(bool movie)
        {
            int phase = 0, samples = 0;
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cfg = new AppConfig { SourceLanguage = "en-US", EnhanceSmallText = false, PerformanceMode = movie ? "movie" : "game" };
            cfg.Translator.Model = "mode-latency-" + Guid.NewGuid().ToString("N");
            await using var session = new TranslationSession(cfg, new Int32Rect(0, 0, 400, 180), true, false, true, IntPtr.Zero,
                new LayoutEngine(() => Layout((Volatile.Read(ref phase) == 0 ? "Open gate" : "Follow me", 20, 20))), new RowTranslator(), _ => RegionSample(++samples));
            session.Frame += f => { if (f.Final && f.Text == "开门") first.TrySetResult(); if (f.Final && f.Text == "跟我来") changed.TrySetResult(); };
            session.Start(); await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var clock = Stopwatch.StartNew(); Volatile.Write(ref phase, 1);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(3)); return clock.ElapsedMilliseconds;
        }
        private static async Task TestMoviePaddleAsync()
        {
            var cfg = new AppConfig { PerformanceMode = "movie", OcrProvider = "paddle" };
            using var engine = (PaddleOcrEngine)cfg.CreateOcrEngine(); using var bmp = JapaneseSample();
            var result = await engine.RecognizeAsync(bmp, "ja-JP", CancellationToken.None);
            using var process = Process.GetProcessById(engine.WorkerId!.Value);
            Check(result.PlainText.Contains("こんにちは") && process.PriorityClass == ProcessPriorityClass.Normal, "real cinema Paddle recognizes Japanese with normal process priority");
        }
    }
}
