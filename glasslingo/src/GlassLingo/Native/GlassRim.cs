using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassLingo.Native
{
    // A restrained specular rim, not a solid frame or a screenshot/shader loop.
    internal sealed class GlassRim : FrameworkElement
    {
        private double _light = .12;
        private long _lastMove;
        internal GlassRim(Window owner)
        {
            IsHitTestVisible = false; Focusable = false;
            owner.MouseMove += FollowLight;
            owner.MouseLeave += (_, _) => { _light = .12; InvalidateVisual(); };
        }
        private void FollowLight(object sender, MouseEventArgs e)
        {
            if (!IsVisible || !SystemParameters.ClientAreaAnimation || Environment.TickCount64 - _lastMove < 40) return;
            var p = e.GetPosition(this);
            if (p.Y > 40 && p.Y < ActualHeight - 20 && p.X > 20 && p.X < ActualWidth - 20) return;
            double next = Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1);
            if (Math.Abs(next - _light) < .035) return;
            _light = next; _lastMove = Environment.TickCount64; InvalidateVisual();
        }
        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 12 || ActualHeight < 12) return;
            dc.PushClip(new RectangleGeometry(new Rect(2, 2, ActualWidth - 4, ActualHeight - 4), 6, 6));
            // Two broad corner caustics stay static at rest; no blur effect or frame loop.
            void Light(Point center, double radius, Color color)
            {
                var glow = new RadialGradientBrush(color, Colors.Transparent);
                dc.DrawEllipse(glow, null, center, radius, radius * .72);
            }
            double radius = Math.Min(ActualWidth * .62, 390);
            Light(new Point(8, 4), radius, Color.FromArgb(12, 222, 240, 255));
            Light(new Point(ActualWidth - 5, ActualHeight - 3), radius * .85, Color.FromArgb(9, 173, 213, 249));
            var brush = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(6, 237, 249, 255), 0),
                new(Color.FromArgb(65, 245, 251, 255), Math.Clamp(_light, .02, .38)),
                new(Colors.Transparent, .48), new(Colors.Transparent, .72),
                new(Color.FromArgb(38, 207, 233, 255), .96), new(Colors.Transparent, 1)
            }, new Point(0, 0), new Point(1, 1));
            dc.DrawRoundedRectangle(null, new Pen(brush, .75), new Rect(2, 2, ActualWidth - 4, ActualHeight - 4), 6, 6);
            dc.Pop();
        }
    }
}
