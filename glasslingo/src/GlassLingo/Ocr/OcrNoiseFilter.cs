using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GlassLingo.Ocr
{
    // Conservative reject rules, not a language dictionary: retain names, CJK, key labels and numbers.
    internal static class OcrNoiseFilter
    {
        internal static bool IsLikelyText(OcrLine line, bool hasLayout)
        {
            string text = line.Text.Trim();
            if (text.Length == 0 || text.Contains('\uFFFD') || text.Contains("[?]", StringComparison.Ordinal)) return false;
            var glyphs = text.EnumerateRunes().Where(r => !Rune.IsWhiteSpace(r) &&
                Rune.GetUnicodeCategory(r) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format)).ToArray();
            int letters = glyphs.Count(Rune.IsLetter), digits = glyphs.Count(Rune.IsNumber);
            int meaningful = letters + digits;
            if (meaningful == 0) return false;
            bool codeName = Regex.IsMatch(text, @"^[A-Za-z][A-Za-z0-9_]*(?:\+\+|#)$");
            if (!codeName && meaningful * 2 < glyphs.Length) return false;
            if (line.Confidence is double score && (!double.IsFinite(score) || score < (meaningful <= 2 ? .65 : .55))) return false;
            // Repeated Latin strokes are a common icon/texture hallucination; repeated numbers remain valid.
            if (glyphs.Length >= 4 && glyphs.All(r => r == glyphs[0]) && glyphs[0].Value is >= 'A' and <= 'z' && letters == glyphs.Length) return false;
            if (hasLayout)
            {
                if (line.Width < 2 || line.Height < 5) return false;
                double perGlyph = (double)line.Width / Math.Max(1, glyphs.Length);
                if (perGlyph > line.Height * 3.5 || perGlyph < line.Height * .055) return false;
            }
            return true;
        }
    }
}
