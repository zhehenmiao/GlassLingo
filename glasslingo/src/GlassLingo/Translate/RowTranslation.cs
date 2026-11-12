using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GlassLingo.Translate
{
    internal sealed record TranslatedRow(int Index, string Text);
    internal interface IRowTranslator
    {
        bool SupportsRows { get; }
        Task<TranslateResult> TranslateRowsAsync(IReadOnlyList<string> rows, string? from, string to,
            Action<IReadOnlyList<TranslatedRow>>? onRows, CancellationToken ct);
    }

    // Row IDs travel in one ordinary compatible chat request. They never enter the UI or
    // cache. Out-of-order replies are valid; missing/duplicate/out-of-range IDs are not.
    internal static class RowTranslation
    {
        private static readonly Regex Marker = new(@"\[\[GL(\d+)\]\]", RegexOptions.CultureInvariant);
        internal static string Encode(IReadOnlyList<string> rows) => string.Join("\n", rows.Select((s, i) => $"[[GL{i}]]{s}"));
        internal static IReadOnlyList<TranslatedRow> Parse(string value, IReadOnlyList<string> source, bool final)
        {
            var matches = Marker.Matches(value);
            var result = new List<TranslatedRow>(); var seen = new HashSet<int>();
            bool invalid = false;
            for (int i = 0; i < matches.Count; i++)
            {
                var marker = matches[i];
                if (!int.TryParse(marker.Groups[1].Value, out int id) || id < 0 || id >= source.Count || !seen.Add(id))
                { invalid = true; continue; }
                int start = marker.Index + marker.Length;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : value.Length;
                string text = value[start..end].Trim();
                if (!final && i == matches.Count - 1)
                {
                    // Hold an unfinished marker even when it is split across SSE chunks.
                    int bracket = text.LastIndexOf('[');
                    if (bracket >= 0)
                    {
                        int first = bracket > 0 && text[bracket - 1] == '[' ? bracket - 1 : bracket;
                        if ("[[GL".StartsWith(text[first..], StringComparison.Ordinal) || text[first..].StartsWith("[[GL", StringComparison.Ordinal)) text = text[..first];
                    }
                }
                text = TranslationOutput.Clean(text.Trim().TrimEnd('`').Trim(), source[id], final);
                text = Regex.Replace(text, @"\s*\r?\n\s*", " ").Trim();
                if (text.Length > 0) result.Add(new(id, text));
            }
            if (final && (invalid || result.Count != source.Count || result.Any(r => r.Text.Contains("[[GL", StringComparison.Ordinal))))
                throw new InvalidOperationException("译文行对应不完整，正在等待有效结果");
            return invalid ? [] : result.OrderBy(r => r.Index).ToArray();
        }
    }
}
