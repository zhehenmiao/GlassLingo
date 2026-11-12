using System;
using System.Runtime.InteropServices;

namespace GlassLingo.Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    internal static class NativeMethods
    {
        // ── 热键 ────────────────────────────────────────────────
        public const int WM_HOTKEY = 0x0312;

        [Flags]
        public enum HotkeyModifiers : uint
        {
            None = 0,
            Alt = 0x0001,
            Control = 0x0002,
            Shift = 0x0004,
            Win = 0x0008,
            NoRepeat = 0x4000,
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id,
            HotkeyModifiers fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // ── 光标 / 窗口 ─────────────────────────────────────────
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;
        public const int SM_CMONITORS = 80;

        // ── DPI ─────────────────────────────────────────────────
        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        /// <summary>物理像素 → DIP 的缩放系数（96 DPI 时 = 1.0）。</summary>
        public static double ScaleForDpi(uint dpi) => dpi <= 0 ? 1.0 : dpi / 96.0;

        // ── 窗口样式（覆层穿透/置顶） ────────────────────────────
        public const int GWL_EXSTYLE = -20;

        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_LAYERED = 0x00080000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        // 注意：32 位路径必须用 unchecked 转成无符号再转 IntPtr。
        // 直接 new IntPtr(int) 会做符号扩展——0x08000000 会变成 0xFFFFFFFF08000000，
        // 样式位全被污染（CS0675 就是在警告这个）。
        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
            IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : (nint)unchecked((uint)GetWindowLong32(hWnd, nIndex));

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
            IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, value)
                : (nint)unchecked((uint)SetWindowLong32(hWnd, nIndex,
                        unchecked((int)(uint)value.ToInt64())));

        /// <summary>给窗口加扩展样式位（点击穿透、不抢焦点、不进任务栏）。</summary>
        public static void AddExStyle(IntPtr hWnd, int style)
        {
            long cur = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            // (uint) 是关键：不转的话 style 会符号扩展成负数 long
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)(cur | (uint)style));
        }

        public static void RemoveExStyle(IntPtr hWnd, int style)
        {
            long cur = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)(cur & ~(long)(uint)style));
        }
    }
}
