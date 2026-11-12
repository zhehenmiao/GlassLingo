using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace GlassLingo.Ocr
{
    /// <summary>识别出的一行文字，带在截图里的像素包围盒。</summary>
    internal sealed class OcrLine
    {
        public string Text { get; init; } = "";
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double? Confidence { get; init; }
        public override string ToString() => $"[{X},{Y} {Width}×{Height}] {Text}";
    }

    internal sealed class OcrOutcome
    {
        public IReadOnlyList<OcrLine> Lines { get; init; } = new List<OcrLine>();
        public string EngineName { get; init; } = "";
        public long ElapsedMs { get; init; }
        public int CharCount { get; init; }

        /// <summary>实际用哪个语言识别的（降级后会变）。翻译要用它。</summary>
        public string LanguageTag { get; init; } = "";
        public bool HasLayout { get; init; } = true;

        public bool IsEmpty => Lines.Count == 0;
        public string PlainText => string.Join("\n", System.Linq.Enumerable.Select(Lines, l => l.Text));
    }

    /// <summary>
    /// OCR 后端抽象。P0 实测中文准确率 85% 是擦线过的，
    /// 所以这一层必须能换 PaddleOCR——接口第一天就这么定，不许回头改。
    /// </summary>
    internal interface IOcrEngine
    {
        string Name { get; }
        IReadOnlyList<string> Languages { get; }
        bool RetryAllLanguagesWhenEmpty { get; }
        bool UsesLanguageSpecificModels { get; }
        /// <summary>这个后端支持哪些 BCP-47 语言标签，如 zh-Hans-CN / en-US。</summary>
        bool Supports(string languageTag);
        Task<OcrOutcome> RecognizeAsync(Bitmap bmp, string languageTag, CancellationToken ct);
    }
}
