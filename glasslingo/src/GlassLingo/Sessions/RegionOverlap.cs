using System;
using System.Windows;

namespace GlassLingo.Sessions
{
    internal static class RegionOverlap
    {
        internal static bool Duplicate(Int32Rect a, Int32Rect b)
        {
            double overlap = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) *
                (double)Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
            double union = (double)a.Width * a.Height + (double)b.Width * b.Height - overlap;
            return union > 0 && overlap / union >= .85;
        }
    }
}
