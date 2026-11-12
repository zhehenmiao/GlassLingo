using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace GlassLingo.Localization
{
    internal static class UiText
    {
        internal static readonly string[] Languages = ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR"];
        private static readonly Dictionary<string, Dictionary<string, string>> Packs = Load();
        private static readonly Dictionary<string, string> OriginalKeys = Packs["zh-CN"].GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key);
        private static readonly Dictionary<string, string> ReverseKeys = BuildReverse();
        public static string Language { get; private set; } = "zh-CN";
        public static event Action? Changed;
        internal static IReadOnlyDictionary<string, Dictionary<string, string>> Catalogs => Packs;
        private static Dictionary<string, Dictionary<string, string>> Load()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var packs = new Dictionary<string, Dictionary<string, string>>();
            foreach (string language in Languages)
            {
                using var stream = assembly.GetManifestResourceStream("GlassLingo.Localization.Catalog." + language + ".json")
                    ?? throw new InvalidOperationException("Missing UI language pack: " + language);
                packs[language] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            }
            return packs;
        }
        private static Dictionary<string, string> BuildReverse()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pack in Packs.Values) foreach (var item in pack) values.TryAdd(item.Value, item.Key);
            return values;
        }
        public static string T(string source)
        {
            if (!OriginalKeys.TryGetValue(source, out string? key) && !ReverseKeys.TryGetValue(source, out key)) return source;
            return Packs[Language].GetValueOrDefault(key, source);
        }
        public static string F(string source, params object?[] values) => string.Format(CultureInfo.GetCultureInfo(Language), T(source), values);
        public static void SetLanguage(string? language)
        {
            string next = language != null && Packs.ContainsKey(language) ? language : "zh-CN";
            bool changed = next != Language; Language = next;
            if (Application.Current is { } app)
            {
                foreach (var item in Packs[next]) app.Resources[item.Key] = item.Value;
                app.Resources["UiFont"] = new FontFamily(next switch {
                    "ja-JP" => "Segoe UI, Yu Gothic UI, Microsoft YaHei UI",
                    "ko-KR" => "Segoe UI, Malgun Gothic, Microsoft YaHei UI",
                    _ => "Segoe UI, Microsoft YaHei UI" });
                foreach (Window window in app.Windows) window.Language = XmlLanguage.GetLanguage(next);
            }
            if (changed) Changed?.Invoke();
        }
    }
}
