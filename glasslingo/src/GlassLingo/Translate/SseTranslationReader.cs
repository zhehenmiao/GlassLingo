using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GlassLingo.Translate
{
    internal static class SseTranslationReader
    {
        public static async Task<TranslateResult> ReadAsync(Stream input, Action<string> onText, CancellationToken ct)
        {
            using var reader = new StreamReader(input, Encoding.UTF8, true, 4096, leaveOpen: true);
            var text = new StringBuilder();
            var data = new StringBuilder();
            var result = new TranslateResult();
            var clock = Stopwatch.StartNew();
            long lastUpdate = -100;
            bool complete = false;
            bool done = false;
            void Consume()
            {
                string payload = data.ToString().Trim(); data.Clear();
                if (payload.Length == 0) return;
                if (payload == "[DONE]") { complete = true; done = true; return; }
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error))
                    throw new InvalidOperationException("流式接口返回错误，请检查模型、额度或稍后重试");
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("prompt_tokens", out var p)) result.InputTokens = p.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var c)) result.OutputTokens = c.GetInt32();
                }
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return;
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                {
                    if (finish.GetString() != "stop") throw new InvalidOperationException("译文未完整结束：" + finish.GetString());
                    complete = true;
                }
                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    text.Append(content.GetString());
                    if (clock.ElapsedMilliseconds - lastUpdate >= 24)
                    { onText(text.ToString()); lastUpdate = clock.ElapsedMilliseconds; }
                }
            }
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is string line)
            {
                if (line.Length == 0) { Consume(); if (done) break; continue; }
                if (line.StartsWith("data:", StringComparison.Ordinal))
                { if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart()); }
            }
            Consume();
            if (!complete) throw new IOException("翻译连接提前断开，请重试");
            result.Text = text.ToString().Trim();
            if (result.Text.Length == 0) throw new InvalidOperationException("接口没有返回译文");
            onText(result.Text);
            return result;
        }
    }
}
