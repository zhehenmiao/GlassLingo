using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GlassLingo.Config;
using GlassLingo.Native;

namespace GlassLingo.Shell
{
    public partial class ShellWindow : Window
    {
        private readonly App _app;
        private AppConfig? _appearance;
        private bool _refreshing;
        private bool _active, _watch, _paused;
        private string _status = "";
        private string _scope = "capture";
        internal Settings.SettingsView? SettingsPage { get; private set; }
        private RegionManagerView? _regionsPage;
        internal event Action<Settings.SettingsView>? SettingsFinished;
        internal event Action<bool>? ShortcutCaptureChanged;
        private readonly System.Windows.Controls.Border _glass;
        internal ShellWindow(App app)
        {
            InitializeComponent(); _app = app;
            var content = (UIElement)Content; Content = null;
            _glass = new System.Windows.Controls.Border { Child = content, CornerRadius = new CornerRadius(10) };
            Content = _glass;
            Localization.UiText.Changed += RefreshUiLanguage;
            Loaded += (_, _) => UiMotion.Enter(HomePage);
            Deactivated += (_, _) => SettingsPage?.CancelShortcutCapture();
        }
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            if (SettingsPage is { } draft) PreviewGlass(draft.PreviewGlassEnabled, draft.PreviewTint);
            else ApplyGlass(_appearance ?? _app.Config);

        }
        protected override void OnSourceInitialized(EventArgs e) { base.OnSourceInitialized(e); GlassWindowFrame.Attach(this, 72); ApplyGlass(_appearance ?? _app.Config); if (!_app.IsPreview) WindowUtilities.ExcludeFromCapture(this); }
        internal bool GlassActive { get; private set; }
        private void ApplyGlass(AppConfig cfg) => GlassActive = GlassMaterials.Apply(this, _glass, cfg.GlassEffects, cfg.GlassTransparencyEnabled ? cfg.PanelTintOpacity : 1);
        internal void Refresh(AppConfig cfg)
        {
            _appearance = cfg;
            if (IsInitialized && new System.Windows.Interop.WindowInteropHelper(this).Handle != IntPtr.Zero)
            { if (SettingsPage is { } draft) PreviewGlass(draft.PreviewGlassEnabled, draft.PreviewTint); else ApplyGlass(cfg); }
            _refreshing = true; PerformanceModeCombo.SelectedValue = cfg.MovieMode ? "movie" : "game"; _scope = _app.ActiveMode ?? _scope; ScopeCombo.SelectedValue = _scope;
            DisplayCombo.SelectedValue = DisplayPreferences.Get(cfg, _scope); _refreshing = false;
            EngineText.Text = cfg.OcrProvider switch { "paddle" => GlassLingo.Localization.UiText.T("PaddleOCR · 本机离线"), "openai-vision" => GlassLingo.Localization.UiText.T("视觉 API · 截图上传"), _ => GlassLingo.Localization.UiText.T("Windows OCR · 系统自带") };
            LanguageText.Text = $"{cfg.SourceLanguage ?? GlassLingo.Localization.UiText.T("自动识别")} → {cfg.TargetLanguage} · {cfg.Translator.Model}";
            CaptureKey.Text = cfg.Shortcuts.Capture; FullscreenKey.Text = cfg.Shortcuts.Fullscreen; WatchKey.Text = cfg.Shortcuts.Watch;
            string DisplayName(string mode) => Localization.UiText.T(mode == "inline" ? "原位覆盖" : mode == "sidebar" ? "侧栏" : "翻译浮窗");
            CaptureDisplay.Text = DisplayName(cfg.DisplayMode); FullscreenDisplay.Text = DisplayName(cfg.FullscreenDisplayMode); WatchDisplay.Text = DisplayName(cfg.WatchDisplayMode);
            InputTranslateButton.ToolTip = Localization.UiText.F("输入翻译 · {0}", cfg.Shortcuts.Input);
            FooterText.Text = GlassLingo.Localization.UiText.F("{0} 暂停    {1} 隐藏    {2} 停止    ·    {3}", cfg.Shortcuts.Pause, cfg.Shortcuts.Hide, cfg.Shortcuts.Stop, App.AppVersion);
            SetRegionCount(_app.WatchRegions.Count);
        }
        internal void SetStatus(string value) { _status = value; StatusText.Text = Localization.UiText.T(value); }
        private async void PerformanceMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshing || PerformanceModeCombo.SelectedValue is not string mode) return;
            PerformanceModeCombo.IsEnabled = false;
            try { await _app.ChangePerformanceModeAsync(mode); }
            finally { PerformanceModeCombo.IsEnabled = true; }
        }
        internal void SetSession(bool active, bool watch, bool paused)
        { _active = active; _watch = watch; _paused = paused; SessionLabel.Text = active ? watch ? paused ? GlassLingo.Localization.UiText.T("监测已暂停") : GlassLingo.Localization.UiText.T("区域监测中") : GlassLingo.Localization.UiText.T("单次翻译") : GlassLingo.Localization.UiText.T("就绪"); PauseButton.IsEnabled = active && watch; StopButton.IsEnabled = active; PauseButton.Content = paused ? GlassLingo.Localization.UiText.T("恢复") : GlassLingo.Localization.UiText.T("暂停"); }
        private void RefreshUiLanguage()
        { if (SettingsPage != null) PageTitle.Text = Localization.UiText.T("设置"); else if (_regionsPage != null) PageTitle.Text = Localization.UiText.T("翻译区域"); foreach (var region in _app.WatchRegions) region.Refresh(); Refresh(_app.Config); SetRegionCount(_app.WatchRegions.Count); SetSession(_active, _watch, _paused); if (_status.Length > 0) SetStatus(_status); }
        protected override void OnClosed(EventArgs e)
        { Localization.UiText.Changed -= RefreshUiLanguage; base.OnClosed(e); }
        internal void PreviewGlass(bool enabled, double opacity)
        { GlassActive = GlassMaterials.Apply(this, _glass, enabled, opacity); }
        internal Settings.SettingsView OpenSettings(AppConfig config, bool preview)
        {
            if (SettingsPage != null) return SettingsPage;
            CloseRegions();
            var view = new Settings.SettingsView(config) { PreviewMode = preview };
            SettingsPage = view;
            view.ShortcutCaptureStateChanged += active => ShortcutCaptureChanged?.Invoke(active);
            HomePage.Visibility = Visibility.Collapsed;
            PageHost.Children.Add(view);
            PageTitle.Text = Localization.UiText.T("设置"); PageSubtitle.Text = "GlassLingo " + App.AppVersion; PageSubtitle.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Visible; SettingsButton.Visibility = Visibility.Collapsed; InputTranslateButton.Visibility = Visibility.Collapsed;
            view.Completed += () =>
            {
                SettingsFinished?.Invoke(view);
                PageHost.Children.Remove(view); SettingsPage = null; HomePage.Visibility = Visibility.Visible;
                BackButton.Visibility = Visibility.Collapsed; SettingsButton.Visibility = Visibility.Visible; InputTranslateButton.Visibility = Visibility.Visible;
                PageTitle.Text = "GlassLingo"; PageSubtitle.SetResourceReference(TextBlock.TextProperty, "ui085"); PageSubtitle.Visibility = Visibility.Visible;
                Refresh(_app.Config); UiMotion.Enter(HomePage, -1);
            };
            UiMotion.Enter(view);
            return view;
        }
        protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            if (System.Windows.Input.Keyboard.FocusedElement is Hotkey.ShortcutCaptureBox recorder)
            {
                var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                if (recorder.HandleDown(key, System.Windows.Input.Keyboard.Modifiers, e.IsRepeat))
                { e.Handled = true; return; }
            }
            if (e.Key == System.Windows.Input.Key.Escape && SettingsPage != null &&
                !(System.Windows.Input.Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }))
            { SettingsPage.Close(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape && _regionsPage != null)
            { CloseRegions(); e.Handled = true; }
            base.OnPreviewKeyDown(e);
        }
        internal void SetRegionCount(int count)
        {
            ManageRegionsButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ManageRegionsButton.Content = Localization.UiText.F("管理区域 · {0}", count);
            WatchTitle.Text = Localization.UiText.T(count > 0 ? "添加区域" : "区域持续翻译");
        }
        internal void OpenRegions()
        {
            if (SettingsPage != null || _regionsPage != null) return;
            _regionsPage = new RegionManagerView(_app); HomePage.Visibility = Visibility.Collapsed; PageHost.Children.Add(_regionsPage);
            PageTitle.Text = Localization.UiText.T("翻译区域"); PageSubtitle.Text = Localization.UiText.T("每个区域可独立暂停、隐藏或移除。");
            BackButton.Visibility = Visibility.Visible; SettingsButton.Visibility = Visibility.Collapsed; InputTranslateButton.Visibility = Visibility.Collapsed;
            UiMotion.Enter(_regionsPage);
        }
        private void CloseRegions()
        {
            if (_regionsPage == null) return;
            PageHost.Children.Remove(_regionsPage); _regionsPage = null; HomePage.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed; SettingsButton.Visibility = Visibility.Visible; InputTranslateButton.Visibility = Visibility.Visible;
            PageTitle.Text = "GlassLingo"; PageSubtitle.SetResourceReference(TextBlock.TextProperty, "ui085"); UiMotion.Enter(HomePage, -1);
        }
        private void ManageRegions_Click(object sender, RoutedEventArgs e) => OpenRegions();
        private void Back_Click(object sender, RoutedEventArgs e) { if (SettingsPage != null) SettingsPage.Close(); else CloseRegions(); }
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();
        private void Capture_Click(object sender, RoutedEventArgs e) => _app.SelectRegion(false);
        private void InputTranslate_Click(object sender, RoutedEventArgs e) => _app.ShowInputTranslation();
        private void Fullscreen_Click(object sender, RoutedEventArgs e) => _app.TranslateFullscreen();
        private void Watch_Click(object sender, RoutedEventArgs e) => _app.SelectRegion(true);
        private void Pause_Click(object sender, RoutedEventArgs e) => _app.PauseResume();
        private void Hide_Click(object sender, RoutedEventArgs e) => _app.ToggleOutput();
        private async void Stop_Click(object sender, RoutedEventArgs e) => await _app.StopAsync();
        private void Display_Changed(object sender, SelectionChangedEventArgs e)
        { if (!_refreshing && _app != null && DisplayCombo.SelectedValue is string value) _app.ChangeDisplay(value, _scope); }
        private void Scope_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshing || _app == null || ScopeCombo.SelectedValue is not string value) return;
            _scope = value; _refreshing = true; DisplayCombo.SelectedValue = DisplayPreferences.Get(_app.Config, _scope); _refreshing = false;
        }
        protected override void OnClosing(CancelEventArgs e)
        { if (_app.IsPreview) Dispatcher.BeginInvoke(() => Application.Current.Shutdown()); else { e.Cancel = true; Hide(); } }
    }
}
