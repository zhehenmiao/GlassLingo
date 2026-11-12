using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaddleOCRSharp;

namespace GlassLingo.Ocr
{
    /// <summary>Local multilingual PaddleOCR engine. Model/runtime initialize on first use.</summary>
    internal sealed class PaddleLocalEngine : IOcrEngine
    {
        private readonly object _gate = new();
        private PaddleOCREngine? _engine;
        private readonly int _threads;
        public PaddleLocalEngine(int threads = 2) => _threads = Math.Clamp(threads, 1, 4);

        public string Name => "PaddleOCR (本地)";
        public IReadOnlyList<string> Languages { get; } = new[]
        {
            "en-US", "zh-Hans-CN", "zh-Hant-TW", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"
        };
        public bool RetryAllLanguagesWhenEmpty => false;
        public bool UsesLanguageSpecificModels => false;
        public bool Supports(string languageTag) => true;

        public Task<OcrOutcome> RecognizeAsync(Bitmap bmp, string languageTag, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                OCRResult raw;
                lock (_gate)
                {
                    _engine ??= new PaddleOCREngine(null, new OCRParameter
                    { cpu_math_library_num_threads = _threads, enable_mkldnn = true, max_side_len = 1536 });
                    raw = _engine.DetectText(bmp);
                }
                sw.Stop();

                var lines = new List<OcrLine>();
                if (raw?.TextBlocks != null)
                {
                    foreach (var block in raw.TextBlocks)
                    {
                        if (block is null) continue;
                        string text = block.Text?.Trim() ?? "";
                        if (text.Length == 0) continue;
                        int minX = block.BoxPoints?.Min(p => p.X) ?? 0;
                        int minY = block.BoxPoints?.Min(p => p.Y) ?? 0;
                        int maxX = block.BoxPoints?.Max(p => p.X) ?? bmp.Width;
                        int maxY = block.BoxPoints?.Max(p => p.Y) ?? bmp.Height;
                        lines.Add(new OcrLine
                        {
                            Text = text, X = minX, Y = minY, Confidence = block.Score,
                            Width = Math.Max(0, maxX - minX), Height = Math.Max(0, maxY - minY)
                        });
                    }
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
}
