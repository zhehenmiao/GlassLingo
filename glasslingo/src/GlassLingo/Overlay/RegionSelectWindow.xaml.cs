using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassLingo.Logging;
using GlassLingo.Native;

namespace GlassLingo.Overlay
{
    /// <summary>
    /// 全屏框选遮罩。
    ///
    /// 坐标规矩（P0 用血换的，别改）：
    ///   虚拟屏幕尺寸 / 光标位置 = 物理像素
    ///   本窗口的 Left/Top/Width/Height 和 Canvas 内坐标 = DIP
    /// 换算只在边界做一次，内部全程 DIP。
    /// </summary>
    public partial class RegionSelectWindow : Window
    {
        private Point _anchor;
        private bool _dragging;
        private double _scale = 1.0;

        // 虚拟屏幕的物理起点（多显示器时可能是负数）
        private int _vsX, _vsY;

        /// <summary>选中区域，物理像素坐标。null 表示取消。</summary>
        public Int32Rect? Result { get; private set; }

        public event Action<Int32Rect?>? Completed;

        /// <param name="maskOpacity">遮罩不透明度 0~1，来自设置页的 MaskOpacity</param>
        public RegionSelectWindow(double maskOpacity = 0.69)
        {
            InitializeComponent();
            WindowState = WindowState.Normal;

            // XAML 里写死了 #B0000000（69%），这里按用户配置覆盖
            double op = maskOpacity;
            if (double.IsNaN(op) || op < 0.05 || op > 0.99) op = 0.69;
            byte alpha = (byte)Math.Round(op * 255);
            Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
            _vsX = screen.X; _vsY = screen.Y;
            int vsW = screen.Width, vsH = screen.Height;

            // 用窗口自己所在显示器的 DPI 算缩放（PerMonitorV2 下这才准）
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            NativeMethods.SetWindowPos(helper.Handle, new IntPtr(-1), screen.X, screen.Y, screen.Width, screen.Height, 0x0010);
            uint dpi = NativeMethods.GetDpiForWindow(helper.Handle);
            if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
            _scale = NativeMethods.ScaleForDpi(dpi);

            Left = _vsX / _scale;
            Top = _vsY / _scale;
            Width = vsW / _scale;
            Height = vsH / _scale;

            Log.Info(GlassLingo.Localization.UiText.F("框选遮罩：虚拟屏幕 {0}×{1} @({2},{3}) 物理像素，", vsW, vsH, _vsX, _vsY) +
                     GlassLingo.Localization.UiText.F("DPI={0} 缩放={1:F2}，窗口 {2:F0}×{3:F0} DIP @({4:F0},{5:F0})", dpi, _scale, Width, Height, Left, Top));

            // 提示条居中靠上
            HintBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(HintBar, (Width - HintBar.DesiredSize.Width) / 2.0);
            Canvas.SetTop(HintBar, Math.Max(40, Height * 0.12));

            Activate();
            Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape)
            {
                Log.Info(GlassLingo.Localization.UiText.T("框选取消（Esc）"));
                Cancel();
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _anchor = e.GetPosition(RootCanvas);
            _dragging = true;
            CaptureMouse();

            SelectionRect.Visibility = Visibility.Visible;
            SizeChip.Visibility = Visibility.Visible;
            UpdateSelection(_anchor, _anchor);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            UpdateSelection(_anchor, e.GetPosition(RootCanvas));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_dragging) return;

            _dragging = false;
            ReleaseMouseCapture();

            var end = e.GetPosition(RootCanvas);
            var rect = Normalize(_anchor, end);

            if (rect.Width < 4 || rect.Height < 4)
            {
                Log.Info(GlassLingo.Localization.UiText.F("框选太小（{0:F0}×{1:F0} DIP），当作取消", rect.Width, rect.Height));
                Cancel();
                return;
            }

            // DIP → 物理像素
            int px = (int)Math.Round(rect.X * _scale) + _vsX;
            int py = (int)Math.Round(rect.Y * _scale) + _vsY;
            int pw = (int)Math.Round(rect.Width * _scale);
            int ph = (int)Math.Round(rect.Height * _scale);

            Result = new Int32Rect(px, py, pw, ph);
            Log.Info(GlassLingo.Localization.UiText.F("框选完成：DIP {0:F0}×{1:F0} @({2:F0},{3:F0})", rect.Width, rect.Height, rect.X, rect.Y) +
                     GlassLingo.Localization.UiText.F(" → 物理 {0}×{1} @({2},{3})", pw, ph, px, py));
            CloseWithResult(Result);
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            Log.Info(GlassLingo.Localization.UiText.T("框选取消（右键）"));
            Cancel();
        }

        private void UpdateSelection(Point a, Point b)
        {
            var r = Normalize(a, b);
            Canvas.SetLeft(SelectionRect, r.X);
            Canvas.SetTop(SelectionRect, r.Y);
            SelectionRect.Width = r.Width;
            SelectionRect.Height = r.Height;

            int pw = (int)Math.Round(r.Width * _scale);
            int ph = (int)Math.Round(r.Height * _scale);
            SizeText.Text = $"{pw} × {ph}";

            // 提示条贴在框的右下角，留 8 DIP 余量
            SizeChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double chipLeft = r.X + r.Width + 8;
            double chipTop = r.Y + r.Height + 8;
            // 别跑出屏幕
            if (chipLeft + SizeChip.DesiredSize.Width > Width)
                chipLeft = Math.Max(0, r.X + r.Width - SizeChip.DesiredSize.Width);
            if (chipTop + SizeChip.DesiredSize.Height > Height)
                chipTop = Math.Max(0, r.Y - SizeChip.DesiredSize.Height - 8);

            Canvas.SetLeft(SizeChip, chipLeft);
            Canvas.SetTop(SizeChip, chipTop);
        }

        private static Rect Normalize(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X);
            double y = Math.Min(a.Y, b.Y);
            return new Rect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        private void Cancel()
        {
            Result = null;
            CloseWithResult(null);
        }

        private void CloseWithResult(Int32Rect? r)
        {
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                Log.Warn(GlassLingo.Localization.UiText.F("关闭框选窗口失败: {0}", ex.Message));
            }
            // 事件放在 Close 之后触发，避免回调里再操作已经关掉的窗口
            try
            {
                Completed?.Invoke(r);
            }
            catch (Exception ex)
            {
                Log.Error(GlassLingo.Localization.UiText.T("框选回调异常"), ex);
            }
        }
    }
}
