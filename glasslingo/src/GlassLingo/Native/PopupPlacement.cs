using System;
using System.Drawing;
using System.Windows;

namespace GlassLingo.Native
{
    internal static class PopupPlacement
    {
        internal static Int32Rect Calculate(Int32Rect source, Rectangle work, int width, int height)
        {
            width = Math.Clamp(width, 1, Math.Max(1, work.Width)); height = Math.Clamp(height, 1, Math.Max(1, work.Height));
            int y = source.Y + source.Height + 10;
            if (y + height > work.Bottom) y = source.Y - height - 10;
            return new Int32Rect(Math.Clamp(source.X, work.Left, work.Right - width),
                Math.Clamp(y, work.Top, work.Bottom - height), width, height);
        }
    }
}
