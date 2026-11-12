using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GlassLingo.Native
{
    internal static class SmoothScroll
    {
        private sealed class State { public double Target; public bool Active; public int Generation; }
        private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, Changed));
        private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached("Offset", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0d, (d,e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));
        public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
        public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
        private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            if (target is not ScrollViewer viewer) return;
            if ((bool)e.NewValue) { viewer.PreviewMouseWheel += Wheel; viewer.PreviewMouseDown += Pointer; viewer.PreviewKeyDown += Key; viewer.Unloaded += Unload; }
            else { viewer.PreviewMouseWheel -= Wheel; viewer.PreviewMouseDown -= Pointer; viewer.PreviewKeyDown -= Key; viewer.Unloaded -= Unload; Stop(viewer); }
        }
        private static DependencyObject? Parent(DependencyObject value) => value is Visual ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);
        private static void Wheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || Keyboard.Modifiers != ModifierKeys.None || sender is not ScrollViewer viewer) return;
            for (DependencyObject? item = e.OriginalSource as DependencyObject; item != null; item = Parent(item))
            {
                if (item is ScrollViewer candidate && candidate.ScrollableHeight > 0 &&
                    (e.Delta > 0 ? candidate.VerticalOffset > 0.5 : candidate.VerticalOffset < candidate.ScrollableHeight - 0.5))
                { if (candidate != viewer) return; break; }
            }
            if (viewer.ScrollableHeight <= 0) return;
            int lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return;
            double step = lines < 0 ? Math.Max(60, viewer.ViewportHeight - 40) : Math.Clamp(lines * 18, 36, 108);
            ScrollBy(viewer, -e.Delta / 120d * step);
            e.Handled = true;
        }
        internal static void ScrollBy(ScrollViewer viewer, double distance)
        {
            var state = States.GetValue(viewer, _ => new State());
            int generation = ++state.Generation;
            double current = viewer.VerticalOffset;
            state.Target = Math.Clamp((state.Active ? state.Target : current) + distance, 0, viewer.ScrollableHeight);
            viewer.BeginAnimation(OffsetProperty, null); viewer.SetCurrentValue(OffsetProperty, current);
            if (!SystemParameters.ClientAreaAnimation) { viewer.ScrollToVerticalOffset(state.Target); state.Active = false; return; }
            state.Active = true;
            var animation = new DoubleAnimation(current, state.Target, TimeSpan.FromMilliseconds(170))
            { EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 }, FillBehavior = FillBehavior.HoldEnd };
            animation.Completed += (_, _) => { if (state.Generation == generation) state.Active = false; };
            viewer.BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        internal static void Stop(ScrollViewer viewer)
        {
            double offset = viewer.VerticalOffset;
            viewer.BeginAnimation(OffsetProperty, null); viewer.SetCurrentValue(OffsetProperty, offset);
            if (States.TryGetValue(viewer, out var state)) { state.Generation++; state.Active = false; state.Target = offset; }
        }
        private static void Pointer(object sender, MouseButtonEventArgs e) => Stop((ScrollViewer)sender);
        private static void Key(object sender, KeyEventArgs e) => Stop((ScrollViewer)sender);
        private static void Unload(object sender, RoutedEventArgs e) => Stop((ScrollViewer)sender);
    }
    internal static class UiMotion
    {
        public static Duration HoverDuration => TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 140 : 0);
        public static Duration HoverExitDuration => TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 160 : 0);
        public static readonly DependencyProperty TabTransitionsProperty = DependencyProperty.RegisterAttached("TabTransitions", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, (d,e) =>
        {
            if (d is TabControl tab) { if ((bool)e.NewValue) tab.SelectionChanged += TabChanged; else tab.SelectionChanged -= TabChanged; }
        }));
        public static void SetTabTransitions(DependencyObject value, bool enabled) => value.SetValue(TabTransitionsProperty, enabled);
        public static bool GetTabTransitions(DependencyObject value) => (bool)value.GetValue(TabTransitionsProperty);
        public static void Enter(FrameworkElement content, int direction = 1)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            var easing = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 };
            // Animate the view, never the HWND opacity (which disables native background transmission).
            content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing });
            var transform = new TranslateTransform(); content.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * 10, 0, TimeSpan.FromMilliseconds(190)) { EasingFunction = easing });
        }
        private static void TabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not TabControl tab || e.Source != tab) return;
            tab.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            { if (tab.Template.FindName("PART_SelectedContentHost", tab) is FrameworkElement host) Enter(host); });
        }
    }
}
