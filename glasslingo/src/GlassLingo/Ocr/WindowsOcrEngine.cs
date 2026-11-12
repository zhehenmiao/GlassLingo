using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.Ocr;
using GlassLingo.Logging;

namespace GlassLingo.Ocr
{
    /// <summary>
    /// Windows 系统 OCR。零依赖、21~31ms。
    /// </summary>
    internal sealed class WindowsOcrEngine : IOcrEngine
    {
        public string Name => "Windows.Media.Ocr";
        public IReadOnlyList<string> Languages => WindowsOcrEngine.AvailableLanguages();
        public bool RetryAllLanguagesWhenEmpty => true;
        public bool UsesLanguageSpecificModels => true;

        // 引擎实例按语言缓存。OcrEngine 建一次要几十毫秒，不能每次都建。
        private readonly ConcurrentDictionary<string, OcrEngine> _engines =
            new ConcurrentDictionary<string, OcrEngine>(StringComparer.OrdinalIgnoreCase);

        // OcrEngine 不保证线程安全，同一实例串行用。
        private readonly object _gate = new object();

        public bool Supports(string languageTag)
        {
            var tag = Normalize(languageTag);
            if (tag == null) return false;
            try
            {
                return OcrEngine.AvailableRecognizerLanguages
                    .Any(l => string.Equals(l.LanguageTag, tag, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Log.Warn($"查询 OCR 语言失败: {ex.Message}");
                return false;
            }
        }

        public static IReadOnlyList<string> AvailableLanguages()
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages
                    .Select(l => l.LanguageTag).ToList();
            }
            catch (Exception ex)
            {
                Log.Warn($"枚举 OCR 语言失败: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 把用户配置的语言标签规整成系统能认的形式。
        /// 中文特别注意：系统里是 zh-Hans-CN，用户可能写 zh / zh-CN / chinese。
        /// </summary>
        public static string? Normalize(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string t = tag.Trim().Replace('_', '-');

            var available = AvailableLanguages();
            // 1. 精确命中
            foreach (var a in available)
                if (string.Equals(a, t, StringComparison.OrdinalIgnoreCase)) return a;

            // 2. 前缀命中（zh → zh-Hans-CN）
            string lower = t.ToLowerInvariant();
            if (lower is "zh" or "cn" or "chinese" or "zh-cn" or "zh-hans")
            {
                var cjk = available.FirstOrDefault(a =>
                    a.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
                if (cjk != null) return cjk;
            }
            if (lower is "en" or "english")
            {
                var en = available.FirstOrDefault(a =>
                    a.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                if (en != null) return en;
            }
            if (lower is "ja" or "jp" or "japanese")
            {
                var ja = available.FirstOrDefault(a =>
                    a.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
                if (ja != null) return ja;
            }

            // 3. 语言学匹配
            try
            {
                var lang = new Language(t);
                return available.FirstOrDefault(a =>
                    string.Equals(a, lang.LanguageTag, StringComparison.OrdinalIgnoreCase)) ?? t;
            }
            catch
            {
                return t;
            }
        }

        private OcrEngine? GetEngine(string tag)
        {
            string? norm = Normalize(tag);
            if (norm == null) return null;

            return _engines.GetOrAdd(norm, key =>
            {
                var engine = OcrEngine.TryCreateFromLanguage(new Language(key));
                if (engine == null)
                {
                    Log.Warn($"OCR 引擎创建失败: {key}（语言包可能没装）");
                    // 占位，避免反复尝试——调用方会判 null
                    throw new InvalidOperationException($"OCR 引擎创建失败: {key}");
                }
                Log.Info($"OCR 引擎已创建: {key}");
                return engine;
            });
        }

        public Task<OcrOutcome> RecognizeAsync(Bitmap bmp, string languageTag,
                                               CancellationToken ct)
        {
            // OCR 是 CPU 活，扔线程池，别占 UI 线程
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                OcrEngine engine;
                try
                {
                    var e = GetEngine(languageTag);
                    if (e == null)
                        throw new InvalidOperationException($"语言不可用: {languageTag}");
                    engine = e;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"OCR 引擎不可用（{languageTag}）：{ex.Message}", ex);
                }

                var sw = Stopwatch.StartNew();
                Windows.Media.Ocr.OcrResult raw;
                lock (_gate)
                {
                    using var sb = Capture.ScreenCapture.ToSoftwareBitmap(bmp);
                    raw = engine.RecognizeAsync(sb).AsTask(ct).GetAwaiter().GetResult();
                }
                sw.Stop();

                var lines = new List<OcrLine>(raw.Lines.Count);
                foreach (var line in raw.Lines)
                {
                    // 单行包围盒 = 所有词包围盒的并集
                    int minX = int.MaxValue, minY = int.MaxValue;
                    int maxX = int.MinValue, maxY = int.MinValue;
                    foreach (var word in line.Words)
                    {
                        var r = word.BoundingRect;
                        if (r.X < minX) minX = (int)r.X;
                        if (r.Y < minY) minY = (int)r.Y;
                        if (r.X + r.Width > maxX) maxX = (int)(r.X + r.Width);
                        if (r.Y + r.Height > maxY) maxY = (int)(r.Y + r.Height);
                    }
                    if (minX == int.MaxValue) { minX = minY = maxX = maxY = 0; }

                    lines.Add(new OcrLine
                    {
                        Text = TextPostProcess.Fix(line.Text),
                        X = minX,
                        Y = minY,
                        Width = Math.Max(0, maxX - minX),
                        Height = Math.Max(0, maxY - minY),
                    });
                }

                return new OcrOutcome
                {
                    Lines = lines,
                    EngineName = Name,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CharCount = lines.Sum(l => l.Text.Length),
                    LanguageTag = languageTag,
                };
            }, ct);
        }
    }

    /// <summary>
    /// Windows OCR 的中文后处理。P0 实测发现两个出厂缺陷，这里治第一个。
    /// </summary>
    internal static class TextPostProcess
    {
        private static readonly Regex CjkSpaces = new Regex(
            @"(?<=[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\u3000-\u303F\uFF00-\uFFEF])" +
            @"[ \t]+" +
            @"(?=[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\u3000-\u303F\uFF00-\uFFEF])",
            RegexOptions.Compiled);

        /// <summary>
        /// 去掉汉字之间的空格，保留中英文之间的空格。
        /// Windows OCR 会把每个汉字当独立词输出（"传 感 器"），直接用会毁掉翻译质量。
        /// </summary>
        public static string Fix(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            string s = raw;

            // 汉字之间、汉字与中文标点之间的空格 → 删
            s = CjkSpaces.Replace(s, "");

            // 全角空格也当空格处理
            s = s.Replace('\u3000', ' ');

            // 连续空格压成一个
            s = Regex.Replace(s, " {2,}", " ");

            return s.Trim();
        }
    }
}
