using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GlassLingo.Config;
using GlassLingo.Ocr;
using GlassLingo.Overlay;
using GlassLingo.Sessions;
using GlassLingo.Translate;

namespace GlassLingo.Diagnostics
{
    internal static partial class SelfTests
    {
        private static async Task TestWatchRepairsAsync()
        {
            await TestCleanStreamTransportAsync();
            Check(TranslationOutput.Clean("原", "Hello", false) == "" && TranslationOutput.Clean("原文：Hello\n译文：你好", "Hello", false) == "你好",
                "streamed source labels are withheld and only their translated body is exposed");
            Check(TranslationOutput.Clean("翻译结果：\n你好", "Hello", true) == "你好" && TranslationOutput.Clean("<think>analysis", "Hello", false) == "" &&
                TranslationOutput.Clean("<think>analysis</think>你好", "Hello", true) == "你好",
                "output formatting labels and reasoning blocks do not appear as subtitle text");
            Check(TranslationOutput.Clean("原文：Hello", "Hello", true) == "" && TranslationOutput.Clean("Translation: literal", "Translation: literal", true) == "Translation: literal",
                "source-only echoes are rejected without deleting a literal header from the actual source");
            Check(TranslationOutput.Clean("**原", "Hello", false) == "" && TranslationOutput.Clean("**原文：** Hello\n**译文：** 你好", "Hello", true) == "你好",
                "bold markdown labels are withheld and removed without flashing a partial source heading");
            var cfg = new AppConfig { EnableStreaming = false };
            using (var presenter = new TranslationPresenter(cfg, "inline", true))
            {
                var first = new SessionFrame(Layout(("Open gate", 20, 20)), new Int32Rect(100, 100, 400, 180), "打开城门", true, false, 1);
                presenter.Present(first);
                presenter.Present(new SessionFrame(Layout(("Follow me", 20, 20)), first.Region, "", false, false, 2, Anchors: []));
                Check(presenter.InlineVisible, "a new dialogue waiting for translation does not blank the previous readable subtitle");
                var paragraph = Layout(("Open gate", 20, 20), ("Follow me", 20, 60));
                presenter.Present(new SessionFrame(paragraph, first.Region, "打开城门，跟我来。", true, false, 2));
                Check(presenter.InlineVisible && !presenter.PopupVisible, "merged dialogue translation stays visible as one nearby paragraph instead of flashing then disappearing");
                presenter.Present(new SessionFrame(new OcrOutcome(), first.Region, "", true, false, 3));
                Check(!presenter.InlineVisible, "confirmed disappearance still clears the subtitle");
                presenter.FollowRegion(first.Region, true);
                Check(!presenter.InlineVisible, "position polling cannot resurrect a subtitle after confirmed empty OCR");
            }
            var growth = new WatchTextStabilizer(); growth.Observe("Previous dialogue", 0);
            growth.Observe("I am", 500); growth.Observe("I am ready", 1000);
            Check(growth.Observe("I am ready to go", 1800), "a steadily growing typewriter sentence makes bounded progress without waiting forever for an identical OCR frame");
            Check(OcrNoiseFilter.IsLikelyText(new OcrLine { Text = "口", Width = 18, Height = 20, Confidence = .78 }, true),
                "moderately confident real short text is retained by the balanced noise filter");

            var engine = new RecoveryOcr(); var recovered = new TaskCompletionSource<SessionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            var recoveryCfg = new AppConfig { SourceLanguage = "ja-JP", OcrProvider = "paddle" }; recoveryCfg.Translator.Model = "recovery-v12";
            await using (var session = new TranslationSession(recoveryCfg, new Int32Rect(0, 0, 400, 180), false, false, true, IntPtr.Zero, engine, new RowTranslator(), _ => RegionSample(0)))
            {
                session.Frame += f => { if (f.Final && f.Text.Length > 0) recovered.TrySetResult(f); };
                session.Start(); var frame = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(engine.Calls == 2 && frame.Ocr.PlainText == "こんにちは" && frame.Ocr.Lines[0].X == 40 && session.ApiCalls == 1,
                    "one bounded enlarged OCR retry recovers small text and restores coordinates before a single translation request");
            }
            var empty = new RecoveryOcr { Empty = true }; int samples = 0;
            await using (var session = new TranslationSession(new AppConfig { SourceLanguage = "en-US", PollIntervalMs = 300, OcrIntervalMs = 300 },
                new Int32Rect(0, 0, 400, 180), true, false, true, IntPtr.Zero, empty, new RowTranslator(), _ => RegionSample(Interlocked.Increment(ref samples))))
            {
                session.Start(); await Task.Delay(1300);
                Check(empty.Calls <= session.OcrCalls + 1 && session.ApiCalls == 0, "empty-region OCR recovery is throttled instead of retrying at every poll");
            }
            var shell = new Shell.ShellWindow((App)Application.Current); shell.Refresh(new AppConfig());
            shell.Show(); await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await NativeGlassSnapshotAsync(shell, "default-opaque-window");
            var surface = ((Grid)shell.Content).Children.OfType<Border>().First();
            Check(surface.Background is GradientBrush background && background.GradientStops.All(s => s.Color.A == 255),
                "new windows have an opaque background even when remembered transparency values are high");
            shell.Hide();
        }
        private static async Task TestCleanStreamTransportAsync(bool rowProtocol = false)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(); using var connection = client.GetStream();
                var header = new List<byte>(); var one = new byte[1];
                while (true)
                {
                    await connection.ReadExactlyAsync(one); header.Add(one[0]);
                    if (header.Count >= 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                    if (header.Count > 16000) throw new InvalidOperationException("oversized request");
                }
                int size = int.Parse(Encoding.ASCII.GetString(header.ToArray()).Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                await connection.ReadExactlyAsync(new byte[size]);
                await connection.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"));
                foreach (string part in rowProtocol ? new[] { "[[G", "L1]]跟我", "来\n[[", "GL0]]开", "门" } : new[] { "**原", "文：** Open gate\n", "**译", "文：** 打开城门" })
                {
                    string data = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = part } } } });
                    await connection.WriteAsync(Encoding.UTF8.GetBytes("data: " + data + "\n\n")); await connection.FlushAsync(); await Task.Delay(85);
                }
                await connection.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
            });
            try
            {
                var translator = new OpenAiCompatTranslator(new TranslatorConfig { BaseUrl = $"http://127.0.0.1:{port}", Model = "format-test", TimeoutSeconds = 5 }, "test-only");
                var visible = new List<string>();
                var result = rowProtocol ? await new LimitedTranslator(translator, new SemaphoreSlim(1)).TranslateRowsAsync(["Open gate", "Follow me"], "en-US", "zh-Hans-CN",
                    rows => visible.Add(string.Join("\n", rows.Select(r => r.Text))), CancellationToken.None) :
                    await translator.TranslateStreamingAsync("Open gate", "en-US", "zh-Hans-CN", s => visible.Add(s), CancellationToken.None);
                await server.WaitAsync(TimeSpan.FromSeconds(6));
                Check(result.Text == (rowProtocol ? "开门\n跟我来" : "打开城门") && visible.Count > 0 && visible.All(s => !s.Contains("原文") && !s.Contains("译文") && !s.Contains("Open gate") && !s.Contains("[[")),
                    rowProtocol ? "HTTP SSE row protocol survives fragmented markers and reordered replies through the global concurrency limiter" :
                    "real HTTP SSE fragments never expose source-heading text before the cleaned final subtitle");
            }
            finally { listener.Stop(); }
        }
        private sealed class RecoveryOcr : IOcrEngine
        {
            internal int Calls; internal bool Empty;
            public string Name => "recovery-test";
            public IReadOnlyList<string> Languages => ["en-US", "ja-JP"];
            public bool RetryAllLanguagesWhenEmpty => false;
            public bool UsesLanguageSpecificModels => false;
            public bool Supports(string language) => true;
            public Task<OcrOutcome> RecognizeAsync(Bitmap bitmap, string tag, CancellationToken ct)
            {
                Calls++;
                return Task.FromResult(Empty ? new OcrOutcome() : bitmap.Width >= 800 ?
                    new OcrOutcome { Lines = [new OcrLine { Text = "こんにちは", X = 80, Y = 40, Width = 200, Height = 30, Confidence = .95 }], LanguageTag = tag } :
                    new OcrOutcome { Lines = [new OcrLine { Text = "こん", X = 40, Y = 20, Width = 30, Height = 12, Confidence = .4 }], LanguageTag = tag });
            }
        }
    }
}
