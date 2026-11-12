using System;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace GlassLingo.Native
{
    internal static class WindowUtilities
    {
        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        internal static IntPtr ExternalWindowAt(POINT point)
        {
            IntPtr window = GetTopWindow(IntPtr.Zero);
            for (int count = 0; window != IntPtr.Zero && count < 512; count++, window = GetWindow(window, 2))
            {
                if (!IsWindowVisible(window) || !NativeMethods.GetWindowRect(window, out var rect) ||
                    point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom) continue;
                if ((NativeMethods.GetWindowLongPtr(window, NativeMethods.GWL_EXSTYLE).ToInt64() & NativeMethods.WS_EX_TRANSPARENT) != 0) continue;
                NativeMethods.GetWindowThreadProcessId(window, out uint pid);
                if (pid != Environment.ProcessId) return window;
            }
            return IntPtr.Zero;
        }
        public static bool ExcludeFromCapture(Window window)
            => NativeMethods.SetWindowDisplayAffinity(new WindowInteropHelper(window).EnsureHandle(), 0x11);
        public static void DarkTitleBar(Window window)
        {
            int dark = 1;
            NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(window).EnsureHandle(), 20, ref dark, 4);
        }
        public static void Position(Window window, Int32Rect rect)
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            NativeMethods.SetWindowPos(hwnd, new IntPtr(-1), rect.X, rect.Y,
                Math.Max(1, rect.Width), Math.Max(1, rect.Height), 0x0010 | 0x0040);
        }
    }
}
