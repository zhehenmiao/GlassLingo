using System;
using System.Collections.Generic;
using System.Linq;
using GlassLingo.Sessions;

namespace GlassLingo.Ocr
{
    internal static class OcrLayout
    {
        internal static IReadOnlyList<OcrLine> Normalize(IEnumerable<OcrLine> input, bool captionRows = false)
        {
            var kept = new List<OcrLine>();
            // Paddle and tile seams can report overlapping copies of the same text.
            // Preserve repeated labels in different places; remove only spatial duplicates.
            foreach (var line in input.OrderByDescending(l => l.Confidence ?? .8).ThenByDescending(l => l.Text.Length))
            {
                if (line.Width <= 0 || line.Height <= 0) { kept.Add(line); continue; }
                if (!kept.Any(other => Duplicate(line, other))) kept.Add(line);
            }
            var ordered = new List<OcrLine>();
            var remaining = kept.OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
            while (remaining.Count > 0)
            {
                var first = remaining[0];
                double center = first.Y + first.Height / 2d;
                var band = remaining.Where(l => Math.Abs(l.Y + l.Height / 2d - center) <= Math.Max(2, Math.Min(l.Height, first.Height) * .45)).OrderBy(l => l.X).ToArray();
                if (captionRows)
                {
                    OcrLine? sentence = null;
                    foreach (var row in band)
                    {
                        if (sentence != null && CaptionContinuation(sentence, row)) sentence = Combine(sentence, row);
                        else { if (sentence != null) ordered.Add(sentence); sentence = row; }
                        remaining.Remove(row);
                    }
                    if (sentence != null) ordered.Add(sentence);
                }
                else foreach (var row in band) { ordered.Add(row); remaining.Remove(row); }
            }
            return JoinShortTails(ordered);
        }
        private static bool CaptionContinuation(OcrLine a, OcrLine b)
        {
            double h = Math.Min(a.Height, b.Height);
            double gap = b.X - a.X - a.Width;
            return h >= 8 && Math.Max(a.Height, b.Height) <= h * 1.7 &&
                Math.Abs(a.Y + a.Height / 2d - b.Y - b.Height / 2d) <= h * .45 &&
                gap >= -h * .3 && gap <= h * 3 &&
                b.Text.Trim().ToUpperInvariant() is not ("OK" or "CANCEL" or "BACK" or "YES" or "NO" or "ON" or "OFF");
        }
        private static OcrLine Combine(OcrLine first, OcrLine row)
        {
            string a = first.Text.TrimEnd(), b = row.Text.TrimStart();
            if (a.Length == 0) return row; if (b.Length == 0) return first;
            bool cjk = IsCjk(a[^1]) || IsCjk(b[0]);
            string separator = cjk || char.IsPunctuation(b[0]) ? "" : " ";
            int left = Math.Min(first.X, row.X), top = Math.Min(first.Y, row.Y);
            return new OcrLine { Text = a + separator + b, X = left, Y = top,
                Width = Math.Max(first.X + first.Width, row.X + row.Width) - left,
                Height = Math.Max(first.Y + first.Height, row.Y + row.Height) - top,
                Confidence = first.Confidence == null && row.Confidence == null ? null : Math.Min(first.Confidence ?? 1, row.Confidence ?? 1) };
        }
        // Paddle can split a sentence into a long body and a tiny trailing box.
        // Rejoin before translation/cache identity, so the suffix never becomes an
        // independent translation anchored to the far end of the source sentence.
        private static IReadOnlyList<OcrLine> JoinShortTails(List<OcrLine> rows)
        {
            var joined = new List<OcrLine>();
            foreach (var row in rows)
            {
                if (joined.Count == 0 || !IsTail(joined[^1], row)) { joined.Add(row); continue; }
                var first = joined[^1];
                joined[^1] = Combine(first, row);
            }
            return joined;
        }
        private static bool IsCjk(char c) => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff' or >= '\uac00' and <= '\ud7af';
        private static bool IsTail(OcrLine body, OcrLine tail)
        {
            string a = body.Text.Trim(), b = tail.Text.Trim();
            if (a.Length < 8 || b.Length is < 1 or > 8 || b.ToUpperInvariant() is "OK" or "CANCEL" or "BACK" or "YES" or "NO" or "ON" or "OFF") return false;
            if (".!?。！？:：".Contains(a[^1])) return false;
            double h = Math.Min(body.Height, tail.Height);
            double gap = tail.X - (body.X + body.Width);
            return h >= 8 && Math.Max(body.Height, tail.Height) <= h * 1.3 &&
                Math.Abs(body.Y + body.Height / 2d - tail.Y - tail.Height / 2d) <= h * .22 &&
                gap >= -2 && gap <= Math.Max(3, h * .55) && tail.Width <= h * 6;
        }
        private static bool Duplicate(OcrLine a, OcrLine b)
        {
            int width = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
            int height = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
            if (width <= 0 || height <= 0) return false;
            double intersection = (double)width * height;
            double minimum = Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height);
            if (intersection / Math.Max(1, minimum) < .8) return false;
            string x = WatchTextStabilizer.Canonical(a.Text), y = WatchTextStabilizer.Canonical(b.Text);
            return x == y || (Math.Min(x.Length, y.Length) >= 4 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal)));
        }
    }
}
