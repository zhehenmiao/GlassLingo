using System;
using System.Collections.Generic;
using System.Linq;
using GlassLingo.Ocr;

namespace GlassLingo.Sessions
{
    internal sealed record AnchoredTranslation(OcrLine Source, string Text);
    internal sealed class AnchoredTranslations
    {
        private readonly Dictionary<string, string> _lines = new();
        private readonly Queue<string> _order = new();
        private static string Key(OcrOutcome layout, OcrLine line) => layout.LanguageTag.ToLowerInvariant() + "|" + WatchTextStabilizer.Canonical(line.Text);
        internal void Remember(OcrOutcome source, string translation)
        {
            string[] rows = translation.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!source.HasLayout || rows.Length != source.Lines.Count) return;
            for (int i = 0; i < rows.Length; i++)
            {
                string key = Key(source, source.Lines[i]);
                if (string.IsNullOrWhiteSpace(source.Lines[i].Text)) continue;
                if (!_lines.ContainsKey(key)) _order.Enqueue(key);
                _lines[key] = rows[i];
            }
            while (_order.Count > 256) _lines.Remove(_order.Dequeue());
        }
        internal IReadOnlyList<AnchoredTranslation> Match(OcrOutcome layout)
            => !layout.HasLayout ? [] : layout.Lines.Where(l => _lines.ContainsKey(Key(layout, l)))
                .Select(l => new AnchoredTranslation(l, _lines[Key(layout, l)])).ToArray();
        internal bool TryAll(OcrOutcome layout, out string text)
        {
            var matches = Match(layout);
            text = string.Join("\n", matches.Select(m => m.Text));
            return layout.Lines.Count > 0 && matches.Count == layout.Lines.Count;
        }
    }
}
