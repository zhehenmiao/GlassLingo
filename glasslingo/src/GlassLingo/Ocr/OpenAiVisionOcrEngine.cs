using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GlassLingo.Config;
using GlassLingo.Translate;

namespace GlassLingo.Ocr
{
    /// <summary>OCR through an OpenAI-compatible vision chat-completions endpoint.</summary>
    internal sealed class OpenAiVisionOcrEngine : IOcrEngine
    {
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
        private readonly VisionOcrConfig _cfg;
        private readonly string _key;

        public OpenAiVisionOcrEngine(VisionOcrConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _key = CredentialStore.Resolve(cfg.ApiKeyRef) ?? "";
        }

        public string Name => $"视觉 API OCR · {_cfg.Model}";
        public IReadOnlyList<string> Languages { get; } = new[]
        {
            "en-US", "zh-Hans-CN", "zh-Hant-TW", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"
        };
        public bool RetryAllLanguagesWhenEmpty => false;
        public bool UsesLanguageSpecificModels => false;
        public bool Supports(string languageTag) => true;

        public async Task<OcrOutcome> RecognizeAsync(Bitmap bmp, string languageTag, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_key))
                throw new InvalidOperationException("视觉 OCR API 没有配置密钥");

            using var image = new MemoryStream();
            bmp.Save(image, System.Drawing.Imaging.ImageFormat.Png);
            string dataUrl = "data:image/png;base64," + Convert.ToBase64String(image.ToArray());
            var requestBody = new
            {
                model = _cfg.Model,
                temperature = 0,
                messages = new object[]
                {
                    new { role = "system", content = "你是 OCR 引擎。逐字转录图片中清楚可辨的文字，保持阅读顺序和换行。忽略图标、图案、装饰、纹理与非文字形状，不要猜成字，不描述图片，不翻译、不解释、不补全。仅返回确认的原文；没有文字时返回空字符串。无法确认的整行直接省略。" },
                    new { role = "user", content = new object[]
                        {
                            new { type = "text", text = "请识别这张截图里的文字。" },
                            new { type = "image_url", image_url = new { url = dataUrl } }
                        }
                    }
                }
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_cfg.TimeoutSeconds, 1, 300)));
            using var req = new HttpRequestMessage(HttpMethod.Post,
                _cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            req.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var sw = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                     .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"视觉 OCR 请求超时（{_cfg.TimeoutSeconds}s）");
            }

            using (response)
            {
                string payload = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"OCR API HTTP {(int)response.StatusCode}：请检查密钥、视觉模型、额度或接口地址");

                using var doc = JsonDocument.Parse(payload);
                string text = doc.RootElement.GetProperty("choices")[0]
                    .GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
                sw.Stop();
                var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => new OcrLine { Text = line.Trim(), X = 0, Y = 0, Width = bmp.Width, Height = bmp.Height })
                    .Where(line => line.Text.Length > 0).ToList();
                return new OcrOutcome
                {
                    Lines = lines, EngineName = Name, ElapsedMs = sw.ElapsedMilliseconds,
                    CharCount = lines.Sum(l => l.Text.Length), LanguageTag = languageTag, HasLayout = false
                };
            }
        }
    }
}
