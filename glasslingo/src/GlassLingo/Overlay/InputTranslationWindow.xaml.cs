using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassLingo.Config;
using GlassLingo.Localization;
using GlassLingo.Native;
using GlassLingo.Translate;

namespace GlassLingo.Overlay
{
    public partial class InputTranslationWindow : Window
    {
        private AppConfig _config;
        private readonly Func<AppConfig, ITranslator?> _translatorFactory;
        private readonly Action<string> _copy;
        private readonly Action? _persistTarget;
        private CancellationTokenSource? _request;
        private long _version;
        private bool _ready, _closed, _busy;
        private bool _compact, _composing;
        private DispatcherTimer? _copyFeedback;
        private readonly DispatcherTimer _autoTranslate = new() { Interval = TimeSpan.FromMilliseconds(420) };
        private readonly StreamTextMotion _flow;
        internal bool IsCompact => _compact;
        private string _status = "";
        private object[] _statusArgs = [];
        private bool _statusError;
        internal event Action? SettingsRequested;
        internal bool IsTranslating => _busy;
        internal string TranslationText => ResultBox.Text;
        internal InputTranslationWindow(AppConfig config, Func<AppConfig, ITranslator?>? translatorFactory = null,
            Action<string>? copy = null, Action? persistTarget = null)
        {
            _config = config; _translatorFactory = translatorFactory ?? (c => c.CreateTranslator());
            _copy = copy ?? Clipboard.SetText; _persistTarget = persistTarget;
            InitializeComponent();
            _flow = new StreamTextMotion((text, _) => SetResult(text));
            _autoTranslate.Tick += async (_, _) =>
            {
                if (_composing) return;
                _autoTranslate.Stop();
                if (!_closed && IsVisible && !string.IsNullOrWhiteSpace(InputBox.Text)) await TranslateInputAsync();
            };
            TargetCombo.ItemsSource = Settings.SettingsView.TranslationTargets;
            SelectTarget(); ApplyMode(config.InputCompactMode, false); _ready = true;
            TextCompositionManager.AddPreviewTextInputStartHandler(InputBox, (_, _) => { _composing = true; _autoTranslate.Stop(); });
            TextCompositionManager.AddPreviewTextInputHandler(InputBox, (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { _composing = false; ScheduleTranslation(); }));
            InputBox.PreviewKeyUp += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Escape || e.ImeProcessedKey is Key.Enter or Key.Escape)
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _composing = false);
            };
            InputBox.LostKeyboardFocus += (_, _) => { _composing = false; ScheduleTranslation(); };
            SetStatus("输入后会自动发送到已配置的翻译服务。");
            UiText.Changed += RefreshLanguage;
            Loaded += (_, _) => { UiMotion.Enter(ContentGrid, 0); FocusInput(); };
            IsVisibleChanged += (_, _) => { if (_ready && !IsVisible) CancelRequest(); };
        }
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (!ShowInTaskbar) NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle, NativeMethods.WS_EX_TOOLWINDOW);
            ApplyGlass();
            if (Application.Current is App { IsPreview: false }) WindowUtilities.ExcludeFromCapture(this);
        }
        private void ApplyGlass() => GlassMaterials.Apply(this, Card, _config.GlassEffects, _config.GlassTransparencyEnabled ? _config.PopupTintOpacity : 1);
        internal void UpdateConfig(AppConfig config)
        {
            if (ReferenceEquals(config, _config)) return;
            CancelRequest(); _config = config;
            _ready = false; SelectTarget(); ApplyMode(config.InputCompactMode, false); _ready = true;
            SetResult(""); ApplyGlass(); ScheduleTranslation(); SetStatus("正在等待输入停顿…");
        }
        private void SelectTarget()
        {
            TargetCombo.SelectedValue = _config.InputTargetLanguage ?? _config.TargetLanguage;
            if (TargetCombo.SelectedIndex < 0) TargetCombo.SelectedValue = _config.TargetLanguage;
            if (TargetCombo.SelectedIndex < 0) TargetCombo.SelectedIndex = 0;
        }
        internal void Reveal()
        {
            if (_closed) return;
            bool entering = !IsVisible;
            if (entering) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (entering) PositionOnPointerScreen();
            Activate(); FocusInput();
            if (TranslationText.Length == 0 && !IsTranslating) ScheduleTranslation();
        }
        private void PositionOnPointerScreen()
        {
            var work = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            var hwnd = new WindowInteropHelper(this).Handle;
            // First move to the requested monitor so WM_DPICHANGED can update WPF's layout.
            NativeMethods.SetWindowPos(hwnd, new IntPtr(-1), work.Left + work.Width / 3, work.Top + work.Height / 4, 0, 0, 0x51);
            UpdateLayout();
            double scale = NativeMethods.ScaleForDpi(NativeMethods.GetDpiForWindow(hwnd));
            Width = Math.Min(_compact ? 400 : 480, Math.Max(MinWidth, work.Width / scale - 24));
            Height = Math.Min(_compact ? 276 : 470, Math.Max(MinHeight, work.Height / scale - 24));
            UpdateLayout();
            int width = (int)Math.Ceiling(ActualWidth * scale), height = (int)Math.Ceiling(ActualHeight * scale);
            WindowUtilities.Position(this, new Int32Rect(Math.Max(work.Left, work.Left + (work.Width - width) / 2),
                Math.Max(work.Top, work.Top + (work.Height - height) / 2), width, height));
        }
        private void FocusInput() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        { if (!_closed && IsVisible) { InputBox.Focus(); Keyboard.Focus(InputBox); InputBox.SelectAll(); } });
        internal async Task TranslateInputAsync()
        {
            if (_closed || _busy) return;
            string text = InputBox.Text.Trim();
            if (text.Length == 0) { SetStatus("请输入要翻译的文字。", true); InputBox.Focus(); return; }
            if (text.Length > 20000) { SetStatus("最多输入 {0} 个字符。", true, 20000); return; }
            CancelRequest(); long version = _version;
            using var request = new CancellationTokenSource(); var token = request.Token;
            _request = request;
            SetResult(""); SettingsButton.Visibility = Visibility.Collapsed;
            try
            {
                var translator = _translatorFactory(_config);
                if (translator == null)
                {
                    SetStatus("请先在设置中配置翻译模型和 API Key。", true);
                    SettingsButton.Visibility = Visibility.Visible; return;
                }
                SetBusy(true); SetStatus("正在翻译…");
                string target = TargetCombo.SelectedValue as string ?? _config.TargetLanguage;
                var clock = Stopwatch.StartNew();
                string? latest = null; int queued = 0, finishing = 0;
                void Stream(string value)
                {
                    if (token.IsCancellationRequested || Volatile.Read(ref finishing) != 0 || Dispatcher.HasShutdownStarted) return;
                    Volatile.Write(ref latest, value);
                    if (Interlocked.Exchange(ref queued, 1) != 0) return;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        Interlocked.Exchange(ref queued, 0);
                        string? pending = Volatile.Read(ref latest);
                        if (pending != null && Volatile.Read(ref finishing) == 0 && !_closed && version == _version && _busy && ReferenceEquals(_request, request) && !token.IsCancellationRequested) _flow.Push(pending);
                    });
                }
                var result = _config.EnableStreaming
                    ? await translator.TranslateStreamingAsync(text, null, target, Stream, token)
                    : await translator.TranslateAsync(text, null, target, token);
                if (_closed || version != _version || token.IsCancellationRequested) return;
                Interlocked.Exchange(ref finishing, 1);
                if (_config.EnableStreaming) await _flow.FinishAsync(result.Text); else SetResult(result.Text);
                if (_closed || version != _version || token.IsCancellationRequested) return;
                if (string.IsNullOrWhiteSpace(result.Text)) SetStatus("服务未返回译文，请重试。", true);
                else SetStatus("翻译完成 · {0} ms", false, result.ElapsedMs > 0 ? result.ElapsedMs : clock.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_closed && version == _version)
                {
                    SetStatus("翻译失败：{0}", true, ex.Message.Length > 160 ? ex.Message[..160] + "…" : ex.Message);
                    Logging.Log.Warn("Input translation failed: " + ex.GetType().Name);
                }
            }
            finally
            {
                if (version == _version) { _request = null; SetBusy(false); }
            }
        }
        internal void StopTranslation()
        { CancelRequest(); SetStatus("已停止，可继续编辑。"); }
        private void CancelRequest()
        {
            _autoTranslate.Stop(); _flow.Reset();
            ++_version; var request = _request; _request = null;
            request?.Cancel(); SetBusy(false);
        }
        private void SetBusy(bool value)
        {
            _busy = value;
            TranslateButton.Content = UiText.T(value ? "停止" : "翻译");
            TranslateButton.IsEnabled = value || !string.IsNullOrWhiteSpace(InputBox.Text);
        }
        private void SetResult(string value)
        {
            ResultBox.Text = value;
            ResultPlaceholder.Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(value);
        }
        private void SetStatus(string source, bool error = false, params object[] args)
        {
            _status = source; _statusArgs = args; _statusError = error;
            StatusText.Text = args.Length == 0 ? UiText.T(source) : UiText.F(source, args);
            StatusText.Foreground = new SolidColorBrush(error ? Color.FromRgb(237, 188, 131) : Color.FromRgb(175, 197, 229));
            UpdateFooter();
        }
        private void UpdateFooter()
        {
            FooterArea.Visibility = !_compact || _statusError ? Visibility.Visible : Visibility.Collapsed;
            if (ResultBox.Text.Length == 0)
                ResultPlaceholder.SetResourceReference(TextBlock.TextProperty, _busy ? "ui278" : _autoTranslate.IsEnabled ? "ui314" : "ui277");
        }
        internal void ApplyMode(bool compact, bool persist)
        {
            bool visible = IsVisible;
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.GetWindowRect(hwnd, out var previous);
            _compact = compact;
            MinHeight = compact ? 230 : 350; MinWidth = compact ? 320 : 360;
            Width = compact ? 400 : 480; Height = compact ? 276 : 470;
            Card.Padding = new Thickness(compact ? 14 : 20, compact ? 12 : 16, compact ? 14 : 20, compact ? 12 : 16);
            TitleStack.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            TitleColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            TargetColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(170);
            CompactCopyButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            ActionsRow.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ResultHeader.Visibility = Visibility.Visible;
            CopyButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            KeyboardHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            InputBox.Height = compact ? 64 : 88; InputBox.FontSize = compact ? 14 : 15; ResultBox.FontSize = compact ? 16 : 18;
            InputArea.Margin = new Thickness(0, compact ? 8 : 14, 0, 0);
            ResultsArea.Margin = new Thickness(0, compact ? 10 : 14, 0, 0);
            ResultPlate.Margin = new Thickness(0, compact ? 0 : 7, 0, 0);
            InputPlaceholder.SetResourceReference(TextBlock.TextProperty, "ui309");
            UpdateFooter();
            if (persist) { _config.InputCompactMode = compact; _persistTarget?.Invoke(); }
            if (visible)
            {
                UpdateLayout();
                double scale = NativeMethods.ScaleForDpi(NativeMethods.GetDpiForWindow(hwnd));
                var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
                int width = (int)Math.Ceiling(Width * scale), height = (int)Math.Ceiling(Height * scale);
                int x = Math.Clamp(previous.Left + (previous.Width - width) / 2, work.Left, Math.Max(work.Left, work.Right - width));
                int y = Math.Clamp(previous.Top + (previous.Height - height) / 2, work.Top, Math.Max(work.Top, work.Bottom - height));
                WindowUtilities.Position(this, new Int32Rect(x, y, width, height));
            }
        }
        private void Mode_Click(object sender, RoutedEventArgs e) { ApplyMode(!_compact, true); UiMotion.Enter(ContentGrid, 0); FocusInput(); }
        private void Input_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            InputPlaceholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            CancelRequest(); SetResult(""); SettingsButton.Visibility = Visibility.Collapsed;
            ScheduleTranslation();
            SetStatus(InputBox.Text.Length == 0 ? "" : "正在等待输入停顿…");
        }
        private void ScheduleTranslation()
        {
            _autoTranslate.Stop();
            if (_ready && !_closed && !_composing && IsVisible && !string.IsNullOrWhiteSpace(InputBox.Text)) _autoTranslate.Start();
        }
        private void Target_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || TargetCombo.SelectedValue is not string target) return;
            CancelRequest(); SetResult("");
            _config.InputTargetLanguage = target; _persistTarget?.Invoke();
            ScheduleTranslation(); SetStatus("正在等待输入停顿…");
        }
        internal bool CopyTranslation()
        {
            if (string.IsNullOrWhiteSpace(ResultBox.Text)) return false;
            try
            {
                _copy(ResultBox.Text); SetStatus("已复制译文");
                CompactCopyPath.Data = Geometry.Parse("M1,7 L5,11 L13,2");
                _copyFeedback?.Stop(); var feedback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) }; _copyFeedback = feedback;
                feedback.Tick += (_, _) => { feedback.Stop(); CompactCopyPath.Data = Geometry.Parse("M4,1 H13 V11 H4 Z M1,4 V14 H10"); };
                feedback.Start(); return true;
            }
            catch { SetStatus("复制失败，请稍后重试。", true); return false; }
        }
        private async void Translate_Click(object sender, RoutedEventArgs e)
        { if (_busy) StopTranslation(); else await TranslateInputAsync(); }
        private void Copy_Click(object sender, RoutedEventArgs e) => CopyTranslation();
        private void Clear_Click(object sender, RoutedEventArgs e)
        { CancelRequest(); InputBox.Clear(); SetResult(""); SetStatus("输入后会自动发送到已配置的翻译服务。"); InputBox.Focus(); }
        private void Settings_Click(object sender, RoutedEventArgs e)
        { CancelRequest(); Hide(); SettingsRequested?.Invoke(); }
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Title_Drag(object sender, MouseButtonEventArgs e)
        {
            for (DependencyObject? node = e.OriginalSource as DependencyObject; node != null && node != sender as DependencyObject;
                node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (node is Control) return;
            if (e.LeftButton == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { }
        }
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (_composing || e.Key == Key.ImeProcessed) { base.OnPreviewKeyDown(e); return; }
            if (e.Key == Key.Escape && !TargetCombo.IsDropDownOpen) { e.Handled = true; Close(); return; }
            if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            { e.Handled = true; CopyTranslation(); return; }
            base.OnPreviewKeyDown(e);
        }
        private void RefreshLanguage()
        { TargetCombo.Items.Refresh(); SetBusy(_busy); SetStatus(_status, _statusError, _statusArgs); }
        protected override void OnClosed(EventArgs e)
        {
            _closed = true; CancelRequest(); UiText.Changed -= RefreshLanguage;
            _copyFeedback?.Stop();
            _flow.Dispose();
            base.OnClosed(e);
        }
    }
}
