using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace GlassLingo.Native
{
    // Animate content, not the transparent HWND. No perpetual rendering/timer and
    // no spatial movement of subtitles that must stay attached to their source.
    internal static class OverlayMotion
    {
        internal static void Reveal(FrameworkElement content, bool subtitle = false)
        {
            content.BeginAnimation(UIElement.OpacityProperty, null);
            content.Opacity = 1;
            if (!SystemParameters.ClientAreaAnimation) return;
            content.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(subtitle ? .86 : .78, 1, TimeSpan.FromMilliseconds(subtitle ? 90 : 140))
                { EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 }, FillBehavior = FillBehavior.Stop });
        }
        internal static void Settle(FrameworkElement content)
        { content.BeginAnimation(UIElement.OpacityProperty, null); content.Opacity = 1; }
    }
}
