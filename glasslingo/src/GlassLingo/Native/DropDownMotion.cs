using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GlassLingo.Native
{
    internal static class DropDownMotion
    {
        private static readonly ConditionalWeakTable<ComboBox, State> States = new();
        private static readonly DependencyPropertyDescriptor OpenDescriptor =
            DependencyPropertyDescriptor.FromProperty(ComboBox.IsDropDownOpenProperty, typeof(ComboBox));
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(DropDownMotion), new PropertyMetadata(false, Changed));
        public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
        public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
        private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            if (target is not ComboBox combo) return;
            var state = States.GetValue(combo, c => new State(c));
            if ((bool)e.NewValue) state.Attach(); else state.Detach();
        }
        private sealed class State(ComboBox owner)
        {
            private Popup? _popup;
            private FrameworkElement? _panel;
            private RotateTransform? _arrow;
            private bool _attached, _listening;
            private int _generation;
            public void Attach()
            {
                if (_attached) return;
                _attached = true; owner.Loaded += Loaded; owner.Unloaded += Unloaded;
                if (owner.IsLoaded) Connect();
            }
            public void Detach()
            {
                if (!_attached) return;
                _attached = false; owner.Loaded -= Loaded; owner.Unloaded -= Unloaded; Disconnect();
            }
            private void Loaded(object sender, RoutedEventArgs e) => Connect();
            private void Unloaded(object sender, RoutedEventArgs e) => Disconnect();
            private void Connect()
            {
                if (_listening) return;
                owner.ApplyTemplate();
                _popup = owner.Template.FindName("PART_Popup", owner) as Popup;
                _panel = _popup?.Child as FrameworkElement;
                if (_popup == null || _panel == null) return;
                if (owner.Template.FindName("DropDownChevron", owner) is FrameworkElement arrow)
                { _arrow = new RotateTransform(); arrow.RenderTransform = _arrow; }
                _panel.RenderTransform = new TranslateTransform();
                _listening = true; OpenDescriptor.AddValueChanged(owner, Requested); Requested(owner, EventArgs.Empty);
            }
            private void Disconnect()
            {
                ++_generation;
                if (_listening) OpenDescriptor.RemoveValueChanged(owner, Requested);
                _listening = false;
                if (_popup != null) _popup.IsOpen = false;
                ResetPanel();
                _arrow?.BeginAnimation(RotateTransform.AngleProperty, null);
                if (_arrow != null) _arrow.Angle = 0;
                _popup = null; _panel = null; _arrow = null;
            }
            private void ResetPanel()
            {
                if (_panel == null) return;
                _panel.BeginAnimation(UIElement.OpacityProperty, null); _panel.Opacity = 1;
                if (_panel.RenderTransform is TranslateTransform shift)
                { shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = 0; }
                _panel.IsHitTestVisible = true;
            }
            private void Requested(object? sender, EventArgs e)
            {
                if (_popup == null || _panel == null) return;
                int generation = ++_generation;
                bool open = owner.IsDropDownOpen;
                bool animate = SystemParameters.ClientAreaAnimation;
                var duration = TimeSpan.FromMilliseconds(animate ? open ? 160 : 100 : 0);
                var ease = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut };
                if (_arrow != null)
                    _arrow.BeginAnimation(RotateTransform.AngleProperty,
                        new DoubleAnimation(_arrow.Angle, open ? 180 : 0, duration) { EasingFunction = ease });
                if (open)
                {
                    if (!_popup.IsOpen)
                    {
                        ResetPanel();
                        _panel.Opacity = animate ? .6 : 1;
                        ((TranslateTransform)_panel.RenderTransform).Y = animate ? -4 : 0;
                        _popup.IsOpen = true;
                    }
                    _panel.IsHitTestVisible = true;
                }
                else
                {
                    if (!_popup.IsOpen) return;
                    // Commit selection/release ComboBox capture immediately; only the visual lingers.
                    _panel.IsHitTestVisible = false;
                    if (!animate) { _popup.IsOpen = false; ResetPanel(); return; }
                }
                var fade = new DoubleAnimation(_panel.Opacity, open ? 1 : 0, duration) { EasingFunction = ease };
                if (!open) fade.Completed += (_, _) =>
                {
                    if (generation != _generation || owner.IsDropDownOpen) return;
                    _popup.IsOpen = false; ResetPanel();
                };
                _panel.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
                var move = (TranslateTransform)_panel.RenderTransform;
                move.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(move.Y, open ? 0 : -3, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
            }
        }
    }
}
