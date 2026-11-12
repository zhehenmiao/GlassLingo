using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GlassLingo.Logging;
using GlassLingo.Native;
using GlassLingo.Ocr;

namespace GlassLingo.Overlay
{
    /// <summary>
    /// 结果覆层。
    ///
    /// P1 阶段先做成「可交互」而不是「点击穿透」——穿透虽然不打扰用户，
    /// 但也意味着收不到 Esc、关不掉，用户只能干等。等 P2 有了「再按一次热键关闭」
    /// 再切成穿透模式。
    /// </summary>
    public partial class ResultOverlayWindow : Window
    {
        internal bool ActivateOnShow { get; set; } = true;
        internal Config.AppConfig Appearance { get; set; } = new();
        internal event Action<double>? TintChanged;
        internal event Action? TintCommitted;
        private bool _appearanceReady;
        /// <summary>footer 的基础部分（OCR 耗时等），翻译更新时要在它后面追加。</summary>
        private string _footerBase = "";

        public ResultOverlayWindow()
        {
            InitializeComponent();
            IsVisibleChanged += (_, _) =>
            { if (IsVisible) OverlayMotion.Reveal(Card); else OverlayMotion.Settle(Card); };
        }

        protected override void OnContentRendered(EventArgs e)
        { base.OnContentRendered(e); }
        internal void RevealTranslation() => OverlayMotion.Reveal(TranslatedText);
        internal void ShowPending(Int32Rect region, double scale)
        {
            HeaderText.Text = Localization.UiText.T("识别中");
            TranslatedText.Text = Localization.UiText.T("识别中");
            BodyText.Text = ""; FooterText.Text = ""; HintText.Text = ""; SepLine.Visibility = Visibility.Collapsed;
            Show(); UpdateLayout(); Reposition(region, scale);
            if (ActivateOnShow) { Activate(); Keyboard.Focus(Card); }
        }
        internal void SetFailure(string message)
        { HeaderText.Text = Localization.UiText.T("翻译结果"); TranslatedText.Text = message; }
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            // 不进 Alt+Tab、不占任务栏，但不设 NOACTIVATE——我们要能收 Esc
            NativeMethods.AddExStyle(hwnd, NativeMethods.WS_EX_TOOLWINDOW);
            PopupOpacitySlider.Value = Appearance.PopupTintOpacity;
            PopupTransparentCheck.IsChecked = Appearance.GlassTransparencyEnabled;
            _appearanceReady = true;
            GlassMaterials.Apply(this, Card, Appearance.GlassEffects, Appearance.GlassTransparencyEnabled ? Appearance.PopupTintOpacity : 1);
            WindowUtilities.ExcludeFromCapture(this);
            if (!ActivateOnShow) NativeMethods.AddExStyle(hwnd, NativeMethods.WS_EX_NOACTIVATE);
        }

        /// <summary>
        /// 显示识别结果。<paramref name="region"/> 是物理像素坐标。
        /// internal 是因为 OcrOutcome 是 internal——WPF 窗口类必须 public，
        /// 但方法可以只对自己的程序集开放。
        /// </summary>
        internal void ShowResult(OcrOutcome outcome, Int32Rect region, double scale,
                                 string languageTag, string? statusNote = null,
                                 Translate.TranslateResult? translation = null)
        {
            bool hasTranslation = translation != null && !string.IsNullOrWhiteSpace(translation.Text);

            string shownLanguage = string.Equals(languageTag, "auto", StringComparison.OrdinalIgnoreCase)
                ? GlassLingo.Localization.UiText.T("自动检测")
                : languageTag;
            HeaderText.Text = GlassLingo.Localization.UiText.T("翻译结果");
            HintText.Text = ActivateOnShow ? GlassLingo.Localization.UiText.F("Esc（窗口内）关闭 · {0} 停止", Appearance.Shortcuts.Stop) : GlassLingo.Localization.UiText.F("{0} 隐藏 · {1} 停止", Appearance.Shortcuts.Hide, Appearance.Shortcuts.Stop);

            if (hasTranslation)
            {
                TranslatedText.Text = translation!.Text;
                TranslatedText.Visibility = Visibility.Visible;
                SepLine.Visibility = Visibility.Visible;
                BodyText.Text = outcome.PlainText;
                BodyText.FontSize = 12;
            }
            else
            {
                TranslatedText.Text = GlassLingo.Localization.UiText.T("等待译文…");
                TranslatedText.Visibility = Visibility.Visible;
                SepLine.Visibility = Visibility.Collapsed;
                BodyText.Text = outcome.IsEmpty
                    ? GlassLingo.Localization.UiText.T("这块区域没认到文字。可能是：区域太小、字太淡、或者不是文字。")
                    : outcome.PlainText;
            }

            var parts = new System.Collections.Generic.List<string>
            {
                $"OCR {outcome.ElapsedMs} ms",
            };
            if (hasTranslation)
                parts.Add(GlassLingo.Localization.UiText.F("翻译 {0} ms", translation!.ElapsedMs));
            parts.Add(outcome.EngineName);
            // 「按 Esc 关闭」不进 footer——它是独立一行（HintText），
            // 免得中文逐字折行把「关/闭」劈开。
            _footerBase = string.Join("  ·  ", parts);
            FooterText.Text = _footerBase;


            Show();
            UpdateLayout();

            // 内容可能撑到很高，别超过屏幕
            double maxH = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height)).WorkingArea.Height /
                NativeMethods.ScaleForDpi(NativeMethods.GetDpiForWindow(new WindowInteropHelper(this).Handle)) * 0.8;
            if (Height > maxH) Height = maxH;

            // 布局完成后尺寸才准，重算一次位置
            Reposition(region, scale);

            Log.Info(GlassLingo.Localization.UiText.F("覆层已显示：{0:F0}×{1:F0} DIP @({2:F0},{3:F0})", Width, Height, Left, Top) +
                     GlassLingo.Localization.UiText.F("，{0} 行", outcome.Lines.Count));

            if (ActivateOnShow) { Activate(); Keyboard.Focus(Card); }
        }

        /// <summary>
        /// 翻译回来了，把译文填进同一个窗口。
        /// 用户先看到原文（OCR 30ms），译文几百毫秒后补上，不用干等。
        /// </summary>
        internal void UpdateTranslation(Translate.TranslateResult tr, string? statusNote)
        {
            if (tr == null || string.IsNullOrWhiteSpace(tr.Text)) return;

            TranslatedText.Text = tr.Text;
            TranslatedText.Visibility = Visibility.Visible;
            SepLine.Visibility = Visibility.Visible;
            BodyText.FontSize = 12;

            var parts = new System.Collections.Generic.List<string>
            {
                GlassLingo.Localization.UiText.F("翻译 {0} ms", tr.ElapsedMs),
                tr.EngineName,
            };
            FooterText.Text = _footerBase + "  ·  " + string.Join("  ·  ", parts);

            // 内容变高了，重新摆一次位置
            if (double.IsNaN(Width) || Width <= 0) Width = 520;
            Reposition(_lastRegion, _lastScale);
        }

        /// <summary>只改 footer 的提示（比如翻译失败），不动正文。提示行由 HintText 固定，不参与。</summary>
        internal void SetFooterNote(string note)
        {
            string baseText = _footerBase;
            FooterText.Text = string.IsNullOrEmpty(note)
                ? baseText
                : (string.IsNullOrEmpty(baseText) ? note : $"{note}  ·  {baseText}");
        }

        private Int32Rect _lastRegion;
        private double _lastScale = 1.0;

        internal void Reposition(Int32Rect region, double scale)
        {
            _lastRegion = region;
            var work = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height)).WorkingArea;
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            // First enter the target monitor, then ask that HWND for its actual DPI.
            if (System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea != work)
                NativeMethods.SetWindowPos(hwnd, new IntPtr(-1), work.Left, work.Top, 0, 0, 0x51);
            scale = NativeMethods.ScaleForDpi(NativeMethods.GetDpiForWindow(hwnd));
            _lastScale = scale;
            UpdateLayout();
            double logicalWidth = double.IsFinite(Width) && Width > 0 ? Width : 520;
            double logicalHeight = ActualHeight > 0 ? ActualHeight : 200;
            var placement = PopupPlacement.Calculate(region, work, (int)Math.Ceiling(logicalWidth * scale), (int)Math.Ceiling(logicalHeight * scale));
            WindowUtilities.Position(this, placement);

        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }

        internal void SetStreamingText(string text)
        {
            TranslatedText.Text = text;
            TranslatedText.Visibility = Visibility.Visible;
            SepLine.Visibility = Visibility.Visible;
            BodyText.FontSize = 12;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.GetPosition(this).Y > 54) return;
            // 拖动整张卡片
            try
            {
                DragMove();
            }
            catch (Exception ex)
            {
                Log.Warn(GlassLingo.Localization.UiText.F("拖动覆层失败: {0}", ex.Message));
            }
        }
        private void Copy_Click(object sender, RoutedEventArgs e)
        { try { Clipboard.SetText(TranslatedText.Text); } catch (Exception ex) { SetFooterNote(GlassLingo.Localization.UiText.T("复制失败：") + ex.Message); } }
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void PopupOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_appearanceReady) return;
            Appearance.PopupTintOpacity = e.NewValue;
            GlassMaterials.Apply(this, Card, Appearance.GlassEffects, Appearance.GlassTransparencyEnabled ? e.NewValue : 1);
            TintChanged?.Invoke(e.NewValue);
        }
        private void PopupOpacity_Commit(object sender, RoutedEventArgs e) => TintCommitted?.Invoke();
        private void PopupTransparency_Changed(object sender, RoutedEventArgs e)
        {
            if (!_appearanceReady) return;
            Appearance.GlassTransparencyEnabled = PopupTransparentCheck.IsChecked == true;
            GlassMaterials.Apply(this, Card, Appearance.GlassEffects, Appearance.GlassTransparencyEnabled ? Appearance.PopupTintOpacity : 1);
            TintCommitted?.Invoke();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Log.Info(GlassLingo.Localization.UiText.T("覆层已关闭"));
        }
    }
}
