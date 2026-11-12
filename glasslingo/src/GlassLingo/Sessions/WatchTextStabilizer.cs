using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GlassLingo.Sessions
{
    // Pixel changes are only a reason to inspect text, never a reason to replace a subtitle.
    internal sealed class WatchTextStabilizer(bool fast = false)
    {
        private string? _accepted;
        private string? _candidate;
        private int _count;
        private long _since;
        private long _growthSince = -1;
        private int _growthCount;
        public bool NeedsConfirmation => _candidate != null;
        public int ConfirmationDelayMs => _candidate == "" ? (fast ? 120 : 500) : fast ? 60 : _accepted != null && _candidate != null && Equivalent(_accepted, _candidate) ? 250 : 120;
        public void Reset() { _accepted = _candidate = null; _count = 0; _growthSince = -1; _growthCount = 0; }
        public bool Observe(string text, long now, bool trusted = false)
        {
            string key = string.Join("\n", text.Split('\n').Select(Canonical).Where(s => s.Length > 0));
            if (_accepted == null)
            {
                if (key.Length == 0) return false;
                _accepted = key; return true;
            }
            if (_accepted == key) { _candidate = null; _count = 0; _growthSince = -1; _growthCount = 0; return false; }
            if (trusted && key.Length >= 6 && SignificantChange(_accepted, key))
            { _accepted = key; _candidate = null; _count = 0; _growthSince = -1; _growthCount = 0; return true; }
            if (_candidate == null || _candidate != key)
            {
                bool growing = _candidate is { Length: > 1 } && key.Length > _candidate.Length && key.StartsWith(_candidate, StringComparison.Ordinal);
                if (!growing) { _growthSince = now; _growthCount = 0; } else _growthCount++;
                _candidate = key; _count = 1; _since = now;
                if (growing && _growthCount >= 2 && now - _growthSince >= 1200 && key.Length >= 4 && key.Any(char.IsLetter))
                { _accepted = key; _candidate = null; _count = 0; _growthSince = -1; _growthCount = 0; return true; }
                return false;
            }
            _count++;
            // A cursor, typewriter animation or a missed OCR frame must not blank the last translation.
            int confirmations = key.Length == 0 || Equivalent(_accepted, key) ? 3 : 2;
            int delay = key.Length == 0 ? (fast ? 400 : 1400) : fast ? 45 : 90;
            if (_count < confirmations || now - _since < delay) return false;
            _accepted = key; _candidate = null; _count = 0; _growthSince = -1; _growthCount = 0; return true;
        }
        private static bool SignificantChange(string a, string b)
        {
            if (Equivalent(a, b)) return false;
            int prefix = 0, suffix = 0;
            while (prefix < Math.Min(a.Length, b.Length) && a[prefix] == b[prefix]) prefix++;
            while (suffix < Math.Min(a.Length, b.Length) - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
            return Math.Max(a.Length, b.Length) - prefix - suffix >= Math.Max(4, Math.Max(a.Length, b.Length) * .35);
        }
        internal static string Canonical(string text)
        {
            string key = Regex.Replace(text.Normalize(NormalizationForm.FormKC).Replace("\u200b", ""), @"\s+", " ").Trim();
            // Preserve English word boundaries ("now here" is not "nowhere"). CJK OCR often
            // inserts spaces between glyphs; those don't define words in these scripts.
            key = Regex.Replace(key, @"(?<=[\u3040-\u30ff\u3400-\u9fff]) +(?=[\u3040-\u30ff\u3400-\u9fff])", "");
            return key.ToUpperInvariant();
        }
        internal static bool Equivalent(string a, string b)
        {
            if (a == b) return true;
            // Never suppress a changed timer, score, quantity, version or short menu label.
            if (Math.Min(a.Length, b.Length) < 18 || Math.Abs(a.Length - b.Length) > 1 ||
                string.Concat(a.Where(char.IsDigit)) != string.Concat(b.Where(char.IsDigit))) return false;
            int i = 0, j = 0, errors = 0;
            while (i < a.Length && j < b.Length)
            {
                if (a[i] == b[j]) { i++; j++; continue; }
                if (++errors > 1) return false;
                if (a.Length >= b.Length) i++;
                if (b.Length >= a.Length) j++;
            }
            return errors + (a.Length - i) + (b.Length - j) <= 1;
        }
    }
}
