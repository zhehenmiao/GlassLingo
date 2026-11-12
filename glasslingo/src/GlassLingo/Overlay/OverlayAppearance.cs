using System;
using System.Globalization;
using System.Windows.Media;
using GlassLingo.Config;

namespace GlassLingo.Overlay
{
    internal static class OverlayAppearance
    {
        internal static bool TryColor(string? value, out Color color)
        {
            color = Color.FromRgb(16, 24, 32);
            string text = value?.Trim() ?? "";
            if (text.Length != 7 || text[0] != '#' ||
                !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb)) return false;
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        internal static Color Background(AppConfig config)
        {
            TryColor(config.OverlayBackgroundColor, out var color);
            color.A = config.OverlayBackgroundEnabled ? (byte)Math.Round(Math.Clamp(config.OverlayOpacity, 0, 1) * 255) : (byte)0;
            return color;
        }
        internal static bool UseDarkText(Color background)
        {
            double Linear(byte value) { double s = value / 255d; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            double luminance = Linear(background.R) * .2126 + Linear(background.G) * .7152 + Linear(background.B) * .0722;
            return background.A >= 128 && luminance > .45;
        }
    }
}
