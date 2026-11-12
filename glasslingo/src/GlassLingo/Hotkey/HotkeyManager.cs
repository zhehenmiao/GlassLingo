using System;
using System.Collections.Generic;
using System.Windows.Interop;
using GlassLingo.Logging;
using GlassLingo.Native;

namespace GlassLingo.Hotkey
{
    /// <summary>
    /// 全局热键。靠一个隐藏消息窗口收 WM_HOTKEY，不做键盘钩子（钩子会被杀软盯上，
    /// 而且拦得住别的程序）。IDisposable：不 Dispose 会漏热键，下次启动注册就失败。
    /// </summary>
    internal sealed class HotkeyManager : IDisposable
    {
        private readonly Dictionary<int, Action> _handlers = new Dictionary<int, Action>();
        private HwndSource? _source;
        private int _nextId = 9000;
        private bool _disposed;

        /// <summary>必须在 UI 线程创建（会建一个 message-only 窗口）。</summary>
        public HotkeyManager()
        {
            var parameters = new HwndSourceParameters("GlassLingoHotkeySink")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            Log.Info($"热键管理器已就绪，hWnd=0x{_source.Handle.ToInt64():X}");
        }

        public IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

        /// <summary>
        /// 注册热键。返回 false 表示被别的程序占了——这个必须告诉用户，
        /// 不能静默失败，否则「按了没反应」会变成玄学问题。
        /// </summary>
        public bool Register(NativeMethods.HotkeyModifiers mods, uint vk,
                             Action handler, string description)
        {
            if (_source == null) return false;

            int id = _nextId++;
            if (!NativeMethods.RegisterHotKey(_source.Handle, id, mods, vk))
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Log.Warn($"热键注册失败 {description}（mods={mods}, vk=0x{vk:X}）Win32Error={err}，" +
                         "多半是已被其他程序占用");
                return false;
            }

            _handlers[id] = handler;
            Log.Info($"热键已注册 {description} id={id}");
            return true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                               ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (_handlers.TryGetValue(id, out var action))
                {
                    handled = true;
                    // 热键回调里绝不能让异常逃出去——逃出去整个 UI 线程就没了
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"热键回调异常 id={id}", ex);
                    }
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var id in _handlers.Keys)
            {
                try { NativeMethods.UnregisterHotKey(_source?.Handle ?? IntPtr.Zero, id); }
                catch (Exception ex) { Log.Warn($"注销热键失败 id={id}: {ex.Message}"); }
            }
            _handlers.Clear();

            if (_source != null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
            Log.Info("热键管理器已释放");
        }
    }
}
