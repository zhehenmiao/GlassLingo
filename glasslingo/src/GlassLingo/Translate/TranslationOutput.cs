using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace GlassLingo.Translate
{
    internal static class TranslationOutput
    {
        private static readonly string[] Labels = ["译文", "翻译", "翻译结果", "以下是翻译", "Translation", "Translated text", "翻訳", "번역"];
        private static readonly Regex SourceHeader = new(@"^\s*(?:原文|Original(?: text)?|Source(?: text)?|系统提示词|System(?: prompt)?)\s*[:：]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex TargetHeader = new(@"(?:^|\n)\s*(?:译文|翻译(?:结果)?|以下是翻译|Translation|Translated text|翻訳|번역)\s*[:：]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        internal static string Clean(string value, string source, bool final)
        {
            string text = value.TrimStart();
            if (!source.TrimStart().StartsWith("<think>", StringComparison.OrdinalIgnoreCase) && text.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
            {
                int end = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (end < 0) return "";
                text = text[(end + 8)..].TrimStart();
            }
            bool literalHeader = SourceHeader.IsMatch(source) || TargetHeader.IsMatch(source.TrimStart()) && TargetHeader.Match(source.TrimStart()).Index == 0;
            if (!literalHeader)
            {
                text = Regex.Replace(text, @"(?m)^\s*(?:\*\*|__|#{1,3}\s+)(原文|Original(?: text)?|Source(?: text)?|系统提示词|System(?: prompt)?|译文|翻译(?:结果)?|Translation|Translated text|翻訳|번역)\s*(?:(?:\*\*|__)\s*[:：]|[:：]\s*(?:\*\*|__)?)\s*", "$1：", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!final && Labels.Concat(new[] { "原文", "Original", "Source", "系统提示词", "System prompt", "<think>" })
                    .Any(label => label.StartsWith(text.TrimStart('*', '_', '#', ' ').TrimEnd('*', '_', ':', '：', ' '), StringComparison.OrdinalIgnoreCase))) return "";
                if (SourceHeader.IsMatch(text))
                {
                    var target = TargetHeader.Match(text);
                    if (!target.Success) return "";
                    text = text[(target.Index + target.Length)..];
                }
                else
                {
                    var header = TargetHeader.Match(text);
                    if (header.Success && header.Index == 0) text = text[header.Length..];
                }
            }
            return final ? text.Trim() : text.TrimEnd('\r');
        }
    }
}
