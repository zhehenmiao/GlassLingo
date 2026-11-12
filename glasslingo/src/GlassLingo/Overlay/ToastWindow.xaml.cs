using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GlassLingo.Logging;
using GlassLingo.Native;

namespace GlassLingo.Overlay
{
    /// <summary>
    /// 自绘提示条，替代 NotifyIcon.ShowBalloonTip。
    ///
    /// 为什么要换：Win11 把通知体系整个换了，老式气泡（ShowBalloonTip）
    /// 经常被静默丢弃——既不显示，也不进通知中心，连日志里都查不出。
    /// 实测：Ctrl+Shift+S 触发三次，屏幕毫无反应，通知中心里也没有记录。
    ///
    /// 这个窗口不抢焦点、点击穿透，3 秒后自己淡出。
    /// </summary>
    public partial class ToastWindow : Window
    {
        private DispatcherTimer? _timer;

        public ToastWindow()
        {
            InitializeComponent();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;

            // 不抢焦点 + 不挡鼠标 + 不进 Alt+Tab
            NativeMethods.AddExStyle(hwnd,
                NativeMethods.WS_EX_NOACTIVATE |
                NativeMethods.WS_EX_TRANSPARENT |
                NativeMethods.WS_EX_TOOLWINDOW);

            // 右下角，留 20 DIP 边距
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 20;
            Top = wa.Bottom - ActualHeight - 20;
        }

        private void ShowCore(string title, string body, int seconds)
        {
            TitleText.Text = title;
            BodyText.Text = body;

            // SizeToContent 之后才知道真实尺寸，等一轮布局再定位
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - ActualWidth - 20;
                Top = wa.Bottom - ActualHeight - 20;
            }), DispatcherPriority.Loaded);

            Show();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _timer.Tick += (_, _) =>
            {
                _timer?.Stop();
                FadeOutAndClose();
            };
            _timer.Start();
        }

        private void FadeOutAndClose()
        {
            try
            {
                var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(240));
                fade.Completed += (_, _) =>
                {
                    try { Close(); } catch { }
                };
                BeginAnimation(OpacityProperty, fade);
            }
            catch
            {
                try { Close(); } catch { }
            }
        }

        /// <summary>弹一条提示。任意线程可调，内部会切回 UI 线程。</summary>
        public static void Show(string title, string body, int seconds = 3)
        {
            try
            {
                var app = Application.Current;
                if (app == null)
                {
                    Log.Warn(GlassLingo.Localization.UiText.F("无 Application，提示被丢弃：{0} / {1}", title, body));
                    return;
                }

                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var w = new ToastWindow();
                        w.ShowCore(title, body, seconds);
                        Log.Info(GlassLingo.Localization.UiText.F("提示条：{0} — {1}", title, body));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(GlassLingo.Localization.UiText.T("显示提示条失败"), ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                Log.Error(GlassLingo.Localization.UiText.T("派发提示条失败"), ex);
            }
        }
    }
}
