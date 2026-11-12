using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassLingo.Native
{
    internal static class GlassMaterials
    {
        private sealed class MaterialState { public IntPtr Handle; public bool? Enabled; public bool Active, RoundAttached, CompositorSupported; public Border? Surface; public GlassRim? Rim; public Grid? Layers; }
        private static readonly ConditionalWeakTable<Window, MaterialState> States = new();
        [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int State, Flags, GradientColor, Animation; }
        [StructLayout(LayoutKind.Sequential)] private struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
        [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
        [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
        private static void Round(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (!States.TryGetValue(window, out var state)) return;
            if (window.AllowsTransparency)
            {
                // A single alpha contour clips every layer; no DWM blur, HRGN, or square backing.
                if (state.Layers is { } layers && layers.ActualWidth > 0 && layers.ActualHeight > 0)
                {
                    double radius = window.WindowState == WindowState.Maximized ? 0 : 8;
                    layers.Clip = new RectangleGeometry(new Rect(0, 0, layers.ActualWidth, layers.ActualHeight), radius, radius);
                }
                if (state.Surface is { } clearSurface) { clearSurface.CornerRadius = new CornerRadius(0); clearSurface.Clip = null; }
                return;
            }
            // Let Windows 11 clip every composited layer using its standard window contour.
            state.CompositorSupported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
            int preference = state.CompositorSupported ? 2 : 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, 33, ref preference, 4);
            int noBorder = unchecked((int)0xFFFFFFFE);
            NativeMethods.DwmSetWindowAttribute(hwnd, 34, ref noBorder, 4);
            if (state.CompositorSupported) SetWindowRgn(hwnd, IntPtr.Zero, true);
            else if (NativeMethods.GetWindowRect(hwnd, out var rect) && rect.Width > 0 && rect.Height > 0)
            {
                int diameter = window.WindowState == WindowState.Maximized ? 0 :
                    (int)Math.Round(16 * NativeMethods.ScaleForDpi(NativeMethods.GetDpiForWindow(hwnd)));
                IntPtr region = CreateRoundRectRgn(0, 0, rect.Width, rect.Height, diameter, diameter);
                if (region != IntPtr.Zero && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
            }
            if (state.Surface is { } surface)
            {
                surface.CornerRadius = new CornerRadius(0);
                surface.Clip = null;
            }
        }

        private static bool LiveBlur(IntPtr hwnd, bool enabled)
        {
            // Desktop Acrylic becomes a solid fallback when inactive on some system configurations.
            // Accent blur samples the live desktop without that focus-dependent opacity change.
            var policy = new AccentPolicy { State = enabled ? 3 : 0, Flags = 0, GradientColor = 0 };
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
            try
            {
                Marshal.StructureToPtr(policy, memory, false);
                var data = new CompositionData { Attribute = 19, Data = memory, Size = Marshal.SizeOf<AccentPolicy>() };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            catch (EntryPointNotFoundException) { return false; }
            finally { Marshal.FreeHGlobal(memory); }
        }
        public static bool Apply(Window window, Border surface, bool enabled, double opacity)
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            WindowUtilities.DarkTitleBar(window);

            var state = States.GetValue(window, _ => new MaterialState());
            state.Surface = surface;
            if (state.Rim == null && window.Content is UIElement content)
            {
                window.Content = null;
                var layers = new Grid(); layers.Children.Add(content);
                state.Layers = layers;
                state.Rim = new GlassRim(window); layers.Children.Add(state.Rim); window.Content = layers;
                layers.SizeChanged += (_, _) => Round(window);
            }
            if (!state.RoundAttached)
            {
                state.RoundAttached = true;
                window.SizeChanged += (_, _) => Round(window);
                window.StateChanged += (_, _) => Round(window);
                surface.SizeChanged += (_, _) => Round(window);
                HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (!window.AllowsTransparency && message == 0x85 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                    { handled = true; return IntPtr.Zero; } // DWM owns the transparent non-client rim.
                    if (!window.AllowsTransparency && message == 0x83 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                    {
                        // Paint the full window: an inset non-client rim stays white in WPF.
                        // Explicit ROUND plus FRAMECHANGED retains DWM's standard contour.
                        handled = true; return IntPtr.Zero;
                    }
                    if (message == 0x02E0) window.Dispatcher.BeginInvoke(() => Round(window));
                    return IntPtr.Zero;
                });
                if (!window.AllowsTransparency && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                {
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x37); // FRAMECHANGED, no move/resize/activate
                }
                Round(window);
            }
            bool allowed = enabled && !SystemParameters.HighContrast && (window.AllowsTransparency || state.CompositorSupported);
            bool success = state.Active;
            if (state.Handle != hwnd || state.Enabled != allowed)
            {
                int type = 1; NativeMethods.DwmSetWindowAttribute(hwnd, 38, ref type, 4);
                success = window.AllowsTransparency ? allowed : LiveBlur(hwnd, allowed) && allowed;
                if (!window.AllowsTransparency && !success && allowed && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
                { type = 3; success = NativeMethods.DwmSetWindowAttribute(hwnd, 38, ref type, 4) == 0; }
                if (!window.AllowsTransparency)
                {
                    var margins = new Margins { Left = success ? -1 : 0, Right = success ? -1 : 0, Top = success ? -1 : 0, Bottom = success ? -1 : 0 };
                    DwmExtendFrameIntoClientArea(hwnd, ref margins);
                }
                state.Handle = hwnd; state.Enabled = allowed; state.Active = success;
                Round(window);
            }
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = window.AllowsTransparency || success ? Colors.Transparent : Color.FromRgb(19, 25, 35);
            window.Background = window.AllowsTransparency || success ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(19, 25, 35));
            surface.Background = Tint(success ? opacity : 1);
            // A bright upper edge and a disappearing lower rim give the material a curved highlight.
            surface.BorderBrush = new LinearGradientBrush(new GradientStopCollection
            { new(Color.FromArgb(170, 242, 249, 255), 0), new(Color.FromArgb(32, 225, 240, 255), 0.35),
              new(Color.FromArgb(12, 190, 212, 242), 0.65), new(Color.FromArgb(90, 224, 240, 255), 1) }, 55);
            surface.BorderThickness = new Thickness(0);
            surface.CornerRadius = new CornerRadius(0);
            surface.Clip = null;
            if (state.Rim != null) state.Rim.Visibility = success ? Visibility.Visible : Visibility.Collapsed;
            return success;
        }
        public static Brush Tint(double opacity)
        {
            double density = Math.Clamp(opacity, 0, 1);
            byte alpha = (byte)(density * 255);
            return new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb((byte)Math.Min(255, alpha * 1.18), 38, 48, 64), 0),
                new(Color.FromArgb(alpha, 17, 25, 39), 0.22),
                new(Color.FromArgb(alpha, 23, 34, 49), 0.8),
                new(Color.FromArgb((byte)Math.Min(255, alpha * 1.12), 26, 38, 54), 1)
            }, new Point(0, 0), new Point(0.75, 1));
        }
    }
}
