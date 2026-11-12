using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
        private static async Task TestSingleRegionCaptionAsync()
        {
            foreach (string mode in new[] { "game", "movie" })
            {
                var cfg = new AppConfig { PerformanceMode = mode, OverlayBackgroundColor = "#185986", OverlayOpacity = .85 };
                var overlay = new InlineOverlayWindow(cfg, captionMode: true);
                try
                {
                    var random = new Random(412);
                    bool single = true;
                    for (int sample = 0; sample < 80; sample++)
                    {
                        var lines = Enumerable.Range(0, 2 + sample % 7).Select(i => new OcrLine { Text = "fragment " + i, X = 10 + i * 120,
                            Y = random.Next(0, 120), Width = 80, Height = random.Next(20, 50) }).ToArray();
                        var anchors = lines.Select((line, i) => new AnchoredTranslation(line, i == 0 ? "那个身上有" : i == lines.Length - 1 ? "苍白小孩。" : "披萨面团气味的")).ToArray();
                        overlay.Present(new SessionFrame(new OcrOutcome { Lines = lines }, new Int32Rect(100, 300, 1280, 240), string.Join("\n", anchors.Select(a => a.Text)),
                            sample % 2 == 0, false, sample + 1, Anchors: anchors));
                        single &= overlay.VisualCount == 1 && overlay.VisibleBounds.Count == 1;
                    }
                    Check(single, mode + " one continuous region always renders exactly one caption and backing across 80 fragmented layouts");
                    var imageLayout = new OcrOutcome { Lines = [
                        new OcrLine { Text = "that pale kid", X = 40, Y = 15, Width = 290, Height = 54 },
                        new OcrLine { Text = "who smells like pizza dough", X = 615, Y = 31, Width = 620, Height = 32 }] };
                    var parts = new[] { new AnchoredTranslation(imageLayout.Lines[0], "那个身上有"), new AnchoredTranslation(imageLayout.Lines[1], "披萨面团气味的苍白小孩。") };
                    Check(OcrLayout.Normalize(imageLayout.Lines, true).Count == 2, "new screenshot-shaped geometry deliberately bypasses the previous row-merge heuristic");
                    overlay.Present(new SessionFrame(imageLayout, new Int32Rect(100, 300, 1280, 190), "那个身上有披萨面团气味的苍白小孩。", true, false, 90, Anchors: parts));
                    await Task.Delay(140); await SnapshotAsync(overlay, mode + "-single-region-caption");
                    Check(overlay.VisualCount == 1 && overlay.VisibleBounds.Count == 1 && !overlay.TextClipped,
                        mode + " offset baselines and different glyph heights still share one complete caption surface");
                }
                finally { overlay.Close(); }
                await TestWholeCaptionTransportAsync(mode);
            }
        }
        private static async Task TestWholeCaptionTransportAsync(string mode)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; string received = "";
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(); using var stream = client.GetStream();
                var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 16000)
                {
                    await stream.ReadExactlyAsync(one); header.Add(one[0]);
                    if (header.Count >= 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                }
                int count = int.Parse(Encoding.ASCII.GetString(header.ToArray()).Split("\r\n").First(s => s.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                var body = new byte[count]; await stream.ReadExactlyAsync(body);
                using (var json = JsonDocument.Parse(body)) received = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
                byte[] reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = "那个身上有披萨面团气味的苍白小孩。" } } } }));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {reply.Length}\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(reply);
            });
            try
            {
                var cfg = new AppConfig { SourceLanguage = "en-US", EnhanceSmallText = false, PerformanceMode = mode, EnableStreaming = false };
                cfg.Translator = new TranslatorConfig { BaseUrl = $"http://127.0.0.1:{port}", Model = "whole-caption-" + mode, TimeoutSeconds = 5 };
                var translator = new LimitedTranslator(new OpenAiCompatTranslator(cfg.Translator, "local-test"), new SemaphoreSlim(2));
                var ready = new TaskCompletionSource<SessionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var session = new TranslationSession(cfg, new Int32Rect(0, 0, 1200, 200), true, false, true, IntPtr.Zero,
                    new LayoutEngine(() => new OcrOutcome { Lines = [
                        new OcrLine { Text = "that pale kid", X = 10, Y = 10, Width = 200, Height = 30 },
                        new OcrLine { Text = "who smells like pizza dough", X = 700, Y = 40, Width = 450, Height = 30 }] }), translator, _ => RegionSample(0));
                session.Frame += f => { if (f.Final && f.Text.Length > 0) ready.TrySetResult(f); };
                session.Start(); var result = await ready.Task.WaitAsync(TimeSpan.FromSeconds(6)); await server.WaitAsync(TimeSpan.FromSeconds(3));
                Check(received == "that pale kid who smells like pizza dough" && !received.Contains("[[GL") && session.ApiCalls == 1 && result.Text.EndsWith("苍白小孩。"),
                    mode + " real compatible HTTP transport receives one whole paragraph rather than fragment row identifiers");
            }
            finally { listener.Stop(); }
        }
    }
}
