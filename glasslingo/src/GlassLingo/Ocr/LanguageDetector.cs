using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GlassLingo.Ocr
{
    /// <summary>
    /// Detects scripts locally. Latin text is passed to the translator as "auto"
    /// because distinguishing English/French/German reliably requires a language model.
    /// </summary>
    internal static class LanguageDetector
    {
        internal sealed class Detection
        {
            public string OcrLanguage { get; init; } = "en-US";
            public string TranslationLanguage { get; init; } = "auto";
        }

        public static IEnumerable<string> OrderCandidates(IReadOnlyList<string> available)
        {
            string[] preferred = { "en-US", "zh-Hans-CN", "ja-JP", "ko-KR", "ru-RU", "zh-Hant-TW" };
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string wanted in preferred)
            {
                string? match = available.FirstOrDefault(a =>
                    string.Equals(a, wanted, StringComparison.OrdinalIgnoreCase));
                if (match != null && yielded.Add(match)) yield return match;
            }
            foreach (string lang in available)
                if (yielded.Add(lang)) yield return lang;
        }

        public static Detection Detect(string text, IReadOnlyList<string> available)
        {
            int latin = 0, han = 0, kana = 0, hangul = 0, cyrillic = 0;
            foreach (Rune rune in text.EnumerateRunes())
            {
                int v = rune.Value;
                if ((v >= 0x0041 && v <= 0x024F) || (v >= 0x1E00 && v <= 0x1EFF)) latin++;
                else if ((v >= 0x3400 && v <= 0x9FFF) || (v >= 0xF900 && v <= 0xFAFF)) han++;
                else if ((v >= 0x3040 && v <= 0x30FF) || (v >= 0x31F0 && v <= 0x31FF)) kana++;
                else if (v >= 0xAC00 && v <= 0xD7AF) hangul++;
                else if (v >= 0x0400 && v <= 0x052F) cyrillic++;
            }

            string tag;
            if (kana > 0) tag = "ja-JP";
            else if (hangul > 0) tag = "ko-KR";
            else if (cyrillic > latin && cyrillic > han) tag = "ru-RU";
            else if (han > 0) tag = IsLikelyTraditionalChinese(text) ? "zh-Hant-TW" : "zh-Hans-CN";
            else tag = "en-US";

            string? installed = available.FirstOrDefault(a =>
                string.Equals(a, tag, StringComparison.OrdinalIgnoreCase));
            installed ??= available.FirstOrDefault(a =>
                a.StartsWith(tag.Split('-')[0], StringComparison.OrdinalIgnoreCase));
            return new Detection
            {
                OcrLanguage = installed ?? tag,
                TranslationLanguage = tag == "en-US" && latin > 0 ? "auto" : tag,
            };
        }

        private static bool IsLikelyTraditionalChinese(string text)
        {
            const string traditional = "體國學會時為這個來發後裡應與開關東車書門見說話語點萬無專業臺灣選擇";
            const string simplified = "体国学会时为这个来发后里应与开关东车书门见说话语点万无专业台湾选择";
            int t = text.Count(c => traditional.Contains(c));
            int s = text.Count(c => simplified.Contains(c));
            return t > s;
        }
    }
}
