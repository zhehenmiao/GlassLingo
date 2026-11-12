using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GlassLingo.Logging;

namespace GlassLingo.Tray
{
    /// <summary>
    /// 托盘图标。用 WinForms 的 NotifyIcon——WPF 没有原生托盘，
    /// 第三方包（Hardcodet 之类）为这点功能不值当，P0 定过「能不用就不用」。
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private bool _disposed;

        public event Action? CaptureRequested;
        public event Action? SettingsRequested;
        public event Action? ExitRequested;
        public event Action? PanelRequested;
        public event Action? FullscreenRequested;
        public event Action? WatchRequested;
        public event Action? PauseRequested;
        public event Action? StopRequested;
        public event Action? InputRequested;

        public TrayIcon()
        {
            _menu = new ContextMenuStrip();
            try
            {
                _menu.Font = new Font("Microsoft YaHei UI", 9f);
            }
            catch
            {
                // 没有这个字体也无所谓，用默认
            }

            var miCapture = new ToolStripMenuItem(GlassLingo.Localization.UiText.T("框选翻译"));
            miCapture.Click += (_, _) => SafeInvoke(CaptureRequested, nameof(CaptureRequested));

            var miSettings = new ToolStripMenuItem(GlassLingo.Localization.UiText.T("设置"));
            miSettings.Click += (_, _) => SafeInvoke(SettingsRequested, nameof(SettingsRequested));

            var miLog = new ToolStripMenuItem(GlassLingo.Localization.UiText.T("打开日志目录"));
            miLog.Click += (_, _) => OpenLogDir();

            var miExit = new ToolStripMenuItem(GlassLingo.Localization.UiText.T("退出"));
            miExit.Click += (_, _) => SafeInvoke(ExitRequested, nameof(ExitRequested));

            _menu.Items.Add(miCapture);
            void Add(string label, Action action) { var item = new ToolStripMenuItem(label); item.Click += (_, _) => action(); _menu.Items.Add(item); }
            Add(Localization.UiText.T("输入翻译"), () => SafeInvoke(InputRequested, nameof(InputRequested)));
            Add(GlassLingo.Localization.UiText.T("全屏翻译"), () => SafeInvoke(FullscreenRequested, nameof(FullscreenRequested)));
            Add(GlassLingo.Localization.UiText.T("区域持续翻译"), () => SafeInvoke(WatchRequested, nameof(WatchRequested)));
            Add(GlassLingo.Localization.UiText.T("暂停 / 恢复监测"), () => SafeInvoke(PauseRequested, nameof(PauseRequested)));
            Add(GlassLingo.Localization.UiText.T("停止翻译"), () => SafeInvoke(StopRequested, nameof(StopRequested)));
            Add(GlassLingo.Localization.UiText.T("打开控制面板"), () => SafeInvoke(PanelRequested, nameof(PanelRequested)));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miSettings);
            _menu.Items.Add(miLog);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miExit);

            _icon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = GlassLingo.Localization.UiText.T("glasslingo · 任意窗口翻译"),
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _icon.DoubleClick += (_, _) =>
                SafeInvoke(PanelRequested, nameof(PanelRequested));

            Localization.UiText.Changed += RefreshLanguage;
            RefreshLanguage();
            Log.Info(GlassLingo.Localization.UiText.T("托盘图标已显示"));
        }

        private void RefreshLanguage()
        {
            foreach (ToolStripItem item in _menu.Items)
            { if (item is ToolStripMenuItem) { item.Tag ??= item.Text; item.Text = Localization.UiText.T(item.Tag as string ?? item.Text ?? ""); } }
            _icon.Text = Localization.UiText.T("glasslingo · 任意窗口翻译");
        }
        private static Icon LoadAppIcon()
        {
            try
            {
                // 优先用 exe 自带的图标
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    var extracted = Icon.ExtractAssociatedIcon(exe);
                    if (extracted != null) return extracted;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(GlassLingo.Localization.UiText.F("提取 exe 图标失败: {0}", ex.Message));
            }
            return SystemIcons.Application;
        }

        private static void OpenLogDir()
        {
            try
            {
                var dir = Path.GetDirectoryName(Log.CurrentFile);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true,
                    });
            }
            catch (Exception ex)
            {
                Log.Error(GlassLingo.Localization.UiText.T("打开日志目录失败"), ex);
            }
        }

        private static void SafeInvoke(Action? handler, string name)
        {
            if (handler == null) return;
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Log.Error(GlassLingo.Localization.UiText.F("托盘菜单回调异常（{0}）", name), ex);
            }
        }

        /// <summary>
        /// 提示用户。走自绘 ToastWindow，不走 NotifyIcon.ShowBalloonTip。
        ///
        /// 实测：Win11 上 ShowBalloonTip 静默失效——热键触发三次，
        /// 屏幕无任何反应，通知中心里也查不到记录。老式气泡在 Win11
        /// 的通知体系里已经被架空了。
        /// </summary>
        public void ShowBalloon(string title, string text)
        {
            Overlay.ToastWindow.Show(title, text);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Localization.UiText.Changed -= RefreshLanguage;

            try
            {
                _icon.Visible = false;
                _icon.Dispose();
                _menu.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn(GlassLingo.Localization.UiText.F("释放托盘失败: {0}", ex.Message));
            }
            Log.Info(GlassLingo.Localization.UiText.T("托盘已释放"));
        }
    }
}
