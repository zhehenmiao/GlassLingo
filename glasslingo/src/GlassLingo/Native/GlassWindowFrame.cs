using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassLingo.Native
{
    // Keep WPF WindowChrome's opaque frame compositor out of the live-glass client area.
    internal static class GlassWindowFrame
    {
        [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo
        { public PointI Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
        { public int Size; public RECT Monitor, Work; public uint Flags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointI point);
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        public static void Attach(Window window, double captionHeight)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            bool resizing = false;
            int resizeHit = 0;
            RECT start = default; PointI origin = default;
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == 0xA1 && wParam.ToInt64() is >= 10 and <= 17 && window.WindowState == WindowState.Normal)
                {
                    resizeHit = (int)wParam; NativeMethods.GetWindowRect(hwnd, out start); GetCursorPos(out origin);
                    resizing = true; SetCapture(hwnd); handled = true; return IntPtr.Zero;
                }
                if (resizing && message == 0x200 && GetCursorPos(out var cursor))
                {
                    double dpi = NativeMethods.ScaleForDpi(NativeMethods.GetDpiForWindow(hwnd));
                    int minWidth = (int)Math.Ceiling(window.MinWidth * dpi), minHeight = (int)Math.Ceiling(window.MinHeight * dpi);
                    int left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
                    if (resizeHit is 10 or 13 or 16) left = Math.Min(start.Left + cursor.X - origin.X, right - minWidth);
                    if (resizeHit is 11 or 14 or 17) right = Math.Max(start.Right + cursor.X - origin.X, left + minWidth);
                    if (resizeHit is 12 or 13 or 14) top = Math.Min(start.Top + cursor.Y - origin.Y, bottom - minHeight);
                    if (resizeHit is 15 or 16 or 17) bottom = Math.Max(start.Bottom + cursor.Y - origin.Y, top + minHeight);
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, left, top, right - left, bottom - top, 0x14);
                    handled = true; return IntPtr.Zero;
                }
                if (resizing && message == 0x202) { resizing = false; ReleaseCapture(); handled = true; return IntPtr.Zero; }
                if (message == 0x215) resizing = false;
                if (message == 0x24) // WM_GETMINMAXINFO: maximize to the monitor's work area.
                {
                    var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor))
                    {
                        var sizing = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                        sizing.MaxPosition.X = monitor.Work.Left - monitor.Monitor.Left;
                        sizing.MaxPosition.Y = monitor.Work.Top - monitor.Monitor.Top;
                        sizing.MaxSize.X = monitor.Work.Width; sizing.MaxSize.Y = monitor.Work.Height;
                        Marshal.StructureToPtr(sizing, lParam, false); handled = true;
                    }
                }
                if (message != 0x84 || !NativeMethods.GetWindowRect(hwnd, out var rect)) return IntPtr.Zero;
                int x = unchecked((short)lParam.ToInt64()), y = unchecked((short)(lParam.ToInt64() >> 16));
                double scale = NativeMethods.ScaleForDpi(NativeMethods.GetDpiForWindow(hwnd));
                int edge = (int)Math.Ceiling(6 * scale);
                int corner = (int)Math.Ceiling(14 * scale);
                bool leftEdge = x - rect.Left < edge, rightEdge = rect.Right - x <= edge;
                bool topEdge = y - rect.Top < edge, bottomEdge = rect.Bottom - y <= edge;
                int hit = 0;
                if (window.WindowState == WindowState.Normal)
                {
                    // Visible rounded arcs are outside the narrow linear edge strips.
                    // Give their inside quadrant a diagonal resize target as well.
                    if (x - rect.Left < corner && y - rect.Top < corner) hit = 13;
                    else if (rect.Right - x < corner && y - rect.Top < corner) hit = 14;
                    else if (x - rect.Left < corner && rect.Bottom - y < corner) hit = 16;
                    else if (rect.Right - x < corner && rect.Bottom - y < corner) hit = 17;
                    else if (topEdge) hit = leftEdge ? 13 : rightEdge ? 14 : 12;
                    else if (bottomEdge) hit = leftEdge ? 16 : rightEdge ? 17 : 15;
                    else if (leftEdge) hit = 10;
                    else if (rightEdge) hit = 11;
                }
                if (hit == 0 && y - rect.Top < captionHeight * scale)
                {
                    var point = window.PointFromScreen(new Point(x, y));
                    var element = window.InputHitTest(point) as DependencyObject;
                    bool interactive = false;
                    while (element != null && element != window)
                    {
                        if (element is Control) { interactive = true; break; }
                        element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
                    }
                    if (!interactive) hit = 2;
                }
                if (hit == 0) return IntPtr.Zero;
                handled = true; return new IntPtr(hit);
            });
        }
    }
}
