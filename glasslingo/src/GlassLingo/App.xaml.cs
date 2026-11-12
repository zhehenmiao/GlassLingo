using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GlassLingo.Config;
using GlassLingo.Hotkey;
using GlassLingo.Logging;
using GlassLingo.Native;
using GlassLingo.Overlay;
using GlassLingo.Sessions;
using GlassLingo.Tray;

namespace GlassLingo
{
    public partial class App : Application
    {
        public static string AppVersion { get; } = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        public static double ScaleFactor { get; private set; } = 1;
        internal AppConfig Config { get; private set; } = new();
        private TrayIcon? _tray;
        private HotkeyManager? _hotkeys;
        private Shell.ShellWindow? _shell;
        private Settings.SettingsView? _settings;
        private RegionSelectWindow? _selection;
        private TranslationSession? _session;
        private TranslationPresenter? _presenter;
        private InputTranslationWindow? _inputTranslation;
        internal ObservableCollection<WatchRegion> WatchRegions { get; } = new();
        private Ocr.SharedOcrPool? _watchOcr;
        private readonly SemaphoreSlim _screenTranslationGate = new(2, 2);
        private DispatcherTimer? _followTimer;
        private Task? _preparationTask;
        private int _nextRegionId;
        private string? _lastScreenMode;
        private Task? _screenStopTask;
        private Mutex? _mutex;
        private EventWaitHandle? _showEvent;
        private readonly CancellationTokenSource _exit = new();
        private bool _starting;
        private bool _shortcutCaptureActive;
        private Ocr.PaddleOcrEngine? _preparedOcr;
        private CancellationTokenSource? _preparationCts;
        private int _operation;
        internal bool Monitoring => WatchRegions.Count > 0;
        private bool _changingPerformance;
        internal async Task ChangePerformanceModeAsync(string mode)
        {
            if (_changingPerformance || (mode != "movie" && mode != "game") || Config.PerformanceMode == mode) return;
            _changingPerformance = true;
            var regions = WatchRegions.Select(r => (r.Session.Region, r.Session.TargetWindow, Paused: r.Session.IsPaused, Hidden: r.Presenter.IsHidden)).ToArray();
            var single = _session;
            var singleRegion = single?.Region;
            try
            {
                await StopAsync();
                Config.PerformanceMode = mode; if (!IsPreview) Config.Save();
                foreach (var r in regions)
                {
                    StartSession(r.Region, true, false, true, r.TargetWindow);
                    var entry = WatchRegions.LastOrDefault();
                    if (entry != null) { if (r.Paused) entry.Session.PauseResume(); if (r.Hidden) entry.Presenter.ToggleVisibility(); }
                }
                if (single != null && singleRegion != null) StartSession(singleRegion.Value, false, single.IsFullscreen, single.TranslationRequested, single.TargetWindow);
                _shell?.Refresh(Config);
            }
            finally { _changingPerformance = false; }
        }
        internal bool IsPreview { get; private set; }
        internal string? ActiveMode => _lastScreenMode == "watch" && Monitoring ? "watch" : _session != null ? _session.IsFullscreen ? "full" : "capture" : Monitoring ? "watch" : null;
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e); Environment.CurrentDirectory = AppContext.BaseDirectory;
            if (e.Args.Length >= 3 && e.Args[0] == "--ocr-worker")
            {
                try { await Ocr.PaddleWorker.RunAsync(e.Args[1], int.Parse(e.Args[2])); }
                catch (Exception ex) { Log.Error("OCR worker stopped", ex); }
                Shutdown(); return;
            }
            InstallErrors();
            if (e.Args.Length == 2 && e.Args[0] == "--inspect-caption")
            {
                IsPreview = true;
                try { await Diagnostics.SelfTests.InspectCaptionAsync(e.Args[1]); Shutdown(0); }
                catch (Exception ex) { Log.Error("Caption inspection failed", ex); Shutdown(1); }
                return;
            }
            Localization.UiText.SetLanguage("zh-CN");
            if (e.Args.Contains("--verify-provider"))
            {
                try { await Diagnostics.SelfTests.VerifyProviderAsync(); Shutdown(0); }
                catch (Exception ex) { Log.Error("Provider verification failed", ex); Shutdown(1); }
                return;
            }
            if (e.Args.Contains("--preview-regions"))
            {
                IsPreview = true;
                Diagnostics.SelfTests.OpenRegionsPreview(); return;
            }
            if (e.Args.Contains("--preview-input"))
            {
                IsPreview = true;
                Diagnostics.SelfTests.OpenInputPreview(); return;
            }
            if (e.Args.Contains("--preview-ui"))
            {
                IsPreview = true; Config = new AppConfig();
                Localization.UiText.SetLanguage(e.Args.FirstOrDefault(a => a.StartsWith("--ui-language="))?.Split('=')[1]);
                _shell = new Shell.ShellWindow(this) { Title = GlassLingo.Localization.UiText.T("GlassLingo · 界面验收") };
                WireShell(_shell); _shell.Show(); _shell.Refresh(Config); return;
            }
            if (e.Args.Contains("--self-test"))
            {
                IsPreview = true;
                try { await Diagnostics.SelfTests.RunAsync(e.Args.Contains("--with-paddle"), e.Args.Contains("--soak"), e.Args.Contains("--frame-only")); Shutdown(0); }
                catch (Exception ex) { Log.Error("Self test failed", ex); System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "self-test-error.txt"), ex.ToString()); Shutdown(1); }
                return;
            }
            _mutex = new Mutex(true, "Local\\GlassLingo-0.6", out bool created);
            if (!created)
            {
                string? runningVersion = RunningVersion();
                if (runningVersion != null && runningVersion != AppVersion)
                {
                    Localization.UiText.SetLanguage(AppConfig.Load().UiLanguage);
                    MessageBox.Show(Localization.UiText.F("后台正在运行 GlassLingo {0}。请先从托盘菜单退出旧版，再启动 {1}。", runningVersion, AppVersion),
                        "GlassLingo " + AppVersion, MessageBoxButton.OK, MessageBoxImage.Information);
                    _mutex.Dispose(); _mutex = null; Shutdown(); return;
                }
                try { using var signal = EventWaitHandle.OpenExisting("Local\\GlassLingo-0.6-show"); signal.Set(); } catch { }
                _mutex.Dispose(); _mutex = null; Shutdown(); return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\GlassLingo-0.6-show");
            _ = Task.Run(async () =>
            {
                while (!_exit.IsCancellationRequested)
                { if (_showEvent.WaitOne(500)) _ = Dispatcher.BeginInvoke(ShowPanel); await Task.Yield(); }
            });
            Config = AppConfig.Load(); Localization.UiText.SetLanguage(Config.UiLanguage); ScaleFactor = NativeMethods.ScaleForDpi(NativeMethods.GetDpiForSystem());
            _tray = new TrayIcon();
            _tray.CaptureRequested += () => SelectRegion(false);
            _tray.FullscreenRequested += TranslateFullscreen;
            _tray.WatchRequested += () => SelectRegion(true);
            _tray.InputRequested += ShowInputTranslation;
            _tray.PauseRequested += PauseResume;
            _tray.StopRequested += async () => await StopAllAsync();
            _tray.PanelRequested += ShowPanel;
            _tray.SettingsRequested += ShowSettings;
            _tray.ExitRequested += () => Shutdown();
            RegisterShortcuts(); ShowPanel(); Log.Info(GlassLingo.Localization.UiText.T("启动 ") + AppVersion);
        }
        private static string? RunningVersion()
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("GlassLingo"))
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path != null) return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0];
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
            return null;
        }
        private void InstallErrors()
        {
            DispatcherUnhandledException += (_, e) => { Log.Error(GlassLingo.Localization.UiText.T("UI 异常"), e.Exception); SetStatus(GlassLingo.Localization.UiText.T("操作失败：") + e.Exception.Message); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error(GlassLingo.Localization.UiText.T("进程异常"), e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error(GlassLingo.Localization.UiText.T("后台任务异常"), e.Exception); e.SetObserved(); };
        }
        private void RegisterShortcuts()
        {
            if (IsPreview || _shortcutCaptureActive) return;
            _hotkeys?.Dispose(); _hotkeys = new HotkeyManager();
            var keys = Config.Shortcuts ??= new ShortcutConfig();
            var actions = new (string Key, Action Action, string Name)[]
            {
                (keys.Capture, () => SelectRegion(false), GlassLingo.Localization.UiText.T("框选")), (keys.Fullscreen, TranslateFullscreen, GlassLingo.Localization.UiText.T("全屏")),
                (keys.Watch, () => SelectRegion(true), GlassLingo.Localization.UiText.T("区域监测")), (keys.Pause, PauseResume, GlassLingo.Localization.UiText.T("暂停")),
                (keys.Hide, ToggleOutput, GlassLingo.Localization.UiText.T("显示/隐藏")),
                (keys.Stop, async () => await StopAllAsync(), GlassLingo.Localization.UiText.T("停止")), (keys.Panel, ShowPanel, GlassLingo.Localization.UiText.T("控制面板")),
                (keys.Input, ShowInputTranslation, Localization.UiText.T("输入翻译")),
                ("Ctrl+Shift+S", () => SelectRegion(false, false), GlassLingo.Localization.UiText.T("仅 OCR"))
            };
            var failed = new List<string>();
            foreach (var action in actions)
                if (!ShortcutParser.TryParse(action.Key, out var modifiers, out uint key) || !_hotkeys.Register(modifiers, key, action.Action, action.Name))
                    failed.Add(action.Name + " " + action.Key);
            if (failed.Count > 0) Dispatcher.BeginInvoke(() => SetStatus(GlassLingo.Localization.UiText.T("快捷键不可用：") + string.Join("；", failed) + GlassLingo.Localization.UiText.T("。可从面板操作或在设置中更换。")));
        }
        internal void ShowPanel()
        {
            if (_shell == null) { _shell = new Shell.ShellWindow(this); WireShell(_shell); }
            if (!_shell.IsVisible) _shell.Show();
            if (_shell.WindowState == WindowState.Minimized) _shell.WindowState = WindowState.Normal; _shell.Activate(); _shell.Refresh(Config);
        }
        internal void ShowInputTranslation()
        {
            if (_inputTranslation == null)
            {
                var window = new InputTranslationWindow(Config, persistTarget: () => { if (!IsPreview) Config.Save(); });
                _inputTranslation = window;
                window.Closed += (_, _) => { if (ReferenceEquals(_inputTranslation, window)) _inputTranslation = null; };
                window.SettingsRequested += ShowSettings;
            }
            _inputTranslation.UpdateConfig(Config); _inputTranslation.Reveal();
        }
        internal async void SelectRegion(bool watch, bool translate = true)
        {
            if (_starting || _selection != null) return;
            if (watch && WatchRegions.Count >= 8)
            { ShowPanel(); SetStatus(Localization.UiText.T("最多同时监测 8 个区域，请先移除一个。")); _shell?.OpenRegions(); return; }
            _starting = true;
            try
            {
                if (_screenStopTask != null) await _screenStopTask;
                _operation++; CancelPreparation();
                // A new continuous selection must not leave an older one-shot overlay
                // on the same video, where it can look like a detached caption suffix.
                await StopSingleAsync();
                PrepareOcr(); _shell?.Hide();
                int operation = _operation;
                var selection = new RegionSelectWindow(Config.MaskOpacity); _selection = selection;
                selection.Completed += async region =>
                {
                    if (operation != _operation) return;
                    _selection = null;
                    if (region == null) { CancelPreparation(); SetStatus(GlassLingo.Localization.UiText.T("已取消框选")); return; }
                    await Task.Delay(100);
                    if (operation != _operation) return;
                    var center = new POINT { X = region.Value.X + region.Value.Width / 2, Y = region.Value.Y + region.Value.Height / 2 };
                    var target = WindowUtilities.ExternalWindowAt(center);
                    StartSession(region.Value, watch, false, translate, target);
                };
                selection.Closed += (_, _) => { if (ReferenceEquals(_selection, selection)) _selection = null; };
                selection.Show(); selection.Activate();
            }
            finally { _starting = false; }
        }
        internal async void TranslateFullscreen()
        {
            if (_starting || _selection != null) return; _starting = true;
            try
            {
                if (_screenStopTask != null) await _screenStopTask;
                _operation++; CancelPreparation(); await StopSingleAsync(); PrepareOcr(); _shell?.Hide();
                int operation = _operation;
                var rect = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
                await Task.Delay(100);
                if (operation != _operation) return;
                StartSession(new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height), false, true, true, IntPtr.Zero);
            }
            finally { _starting = false; }
        }
        private void PrepareOcr()
        {
            if (Config.OcrProvider != "paddle" || _watchOcr != null) return;
            var engine = new Ocr.PaddleOcrEngine(Config.EffectivePaddleThreads, Config.MovieMode);
            _preparedOcr = engine; _preparationCts = CancellationTokenSource.CreateLinkedTokenSource(_exit.Token);
            _preparationTask = WarmPreparedAsync(engine, _preparationCts.Token);
        }
        private static async Task WarmPreparedAsync(Ocr.PaddleOcrEngine engine, CancellationToken ct)
        {
            try { await engine.WarmAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Log.Warn("OCR preparation: " + ex.GetType().Name); }
        }
        private void CancelPreparation()
        {
            _preparationCts?.Cancel(); _preparedOcr?.Dispose(); _preparedOcr = null;
            _preparationCts?.Dispose(); _preparationCts = null;
            _preparationTask = null;
        }
        private Ocr.PaddleOcrEngine? TakePrepared()
        {
            var engine = _preparedOcr; _preparedOcr = null;
            var cts = _preparationCts; _preparationCts = null;
            var task = _preparationTask; _preparationTask = null;
            if (cts != null) { if (task == null || task.IsCompleted) cts.Dispose(); else _ = task.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default); }
            return engine;
        }
        private void StartSession(Int32Rect region, bool watch, bool fullscreen, bool translate, IntPtr target)
        {
            var duplicate = watch ? WatchRegions.FirstOrDefault(r => r.Session.TargetWindow == target && RegionOverlap.Duplicate(r.Session.Region, region)) : null;
            if (duplicate != null)
            {
                CancelPreparation();
                if (duplicate.Presenter.IsHidden) duplicate.Presenter.ToggleVisibility();
                SetStatus(duplicate.Name + " · " + duplicate.Status); return;
            }
            string mode = watch ? Config.WatchDisplayMode : fullscreen ? Config.FullscreenDisplayMode : Config.DisplayMode;
            var translator = Config.CreateTranslator();
            if (translator == null && (translate || Config.AutoTranslate))
            { CancelPreparation(); ShowPanel(); SetStatus(Localization.UiText.T("翻译服务未配置，请打开设置")); return; }
            _watchOcr ??= new Ocr.SharedOcrPool((Ocr.IOcrEngine?)TakePrepared() ?? Config.CreateOcrEngine(), Config.MovieMode);
            var engine = _watchOcr.Rent();
            if (translator != null) translator = new Translate.LimitedTranslator(translator, _screenTranslationGate);
            bool follow = !watch && mode == "inline" && Config.OverlayFollowSource && Config.OcrProvider != "openai-vision";
            var session = new TranslationSession(Config, region, watch, fullscreen, translate, target, engine: engine, translator: translator, follow: follow);
            var presenter = new TranslationPresenter(Config, mode, watch || follow);
            if (watch)
            {
                AttachWatchRegion(session, presenter);
            }
            else
            {
                _session = session; _presenter = presenter; _lastScreenMode = fullscreen ? "full" : "capture";
                session.OcrReleased += () => Dispatcher.BeginInvoke(() =>
                { if (ReferenceEquals(_session, session) && WatchRegions.Count == 0 && !session.NeedsOcr) { _watchOcr?.Dispose(); _watchOcr = null; } });
                session.SourceClosed += () => Dispatcher.BeginInvoke(async () =>
                { if (ReferenceEquals(_session, session)) { await StopSingleAsync(); RefreshScreenState(); } });
            BindFrames(session, () => ReferenceEquals(_session, session), frame => presenter.Present(session.Reproject(frame)));
            session.Status += text => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_session, session)) SetStatus(text); });
            session.Error += text => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_session, session)) presenter.Fail(text); });
            presenter.Notice += SetStatus;
            presenter.Dismissed += async () => { if (ReferenceEquals(_session, session)) { await StopSingleAsync(); RefreshScreenState(); } };
            }
            presenter.Begin(region, fullscreen);
            RefreshScreenState(); _shell?.Refresh(Config); EnsureFollowTimer(); session.Start();
        }
        internal WatchRegion AttachWatchRegion(TranslationSession session, TranslationPresenter presenter)
        {
            if (!session.IsWatch || WatchRegions.Count >= 8) throw new InvalidOperationException("Watch-region capacity exceeded");
            var entry = new WatchRegion(++_nextRegionId, session, presenter); WatchRegions.Add(entry); _lastScreenMode = "watch";
            BindFrames(session, () => WatchRegions.Contains(entry), frame =>
            {
                var projected = session.Reproject(frame); presenter.Present(projected); entry.SetTranslation(projected.Text);
            });
            session.Status += text => Dispatcher.BeginInvoke(() => { if (WatchRegions.Contains(entry)) { entry.SetStatus(text); RefreshScreenState(); } });
            session.Error += text => Dispatcher.BeginInvoke(() => { if (WatchRegions.Contains(entry)) { entry.SetStatus(text); presenter.Fail(text); } });
            session.SourceClosed += () => Dispatcher.BeginInvoke(async () => await RemoveRegionAsync(entry));
            presenter.Notice += value => entry.SetStatus(value);
            presenter.Dismissed += async () => await RemoveRegionAsync(entry);
            RefreshScreenState(); return entry;
        }
        private void BindFrames(TranslationSession session, Func<bool> active, Action<SessionFrame> present)
        {
            SessionFrame? latest = null; int queued = 0;
            session.Frame += frame =>
            {
                Volatile.Write(ref latest, frame);
                if (Interlocked.Exchange(ref queued, 1) != 0 || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    Interlocked.Exchange(ref queued, 0);
                    var value = Volatile.Read(ref latest);
                    if (value != null && active() && value.Version == session.CurrentVersion) present(value);
                });
            };
        }
        internal void PauseResume()
        {
            bool pause = WatchRegions.Any(r => !r.Session.IsPaused);
            foreach (var entry in WatchRegions) if (entry.Session.IsPaused != pause) { entry.Session.PauseResume(); entry.Refresh(); }
            RefreshScreenState();
        }
        internal void ToggleOutput()
        {
            bool hide = WatchRegions.Any(r => !r.Presenter.IsHidden) || _presenter is { IsHidden: false };
            foreach (var entry in WatchRegions) if (entry.Presenter.IsHidden != hide) { entry.Presenter.ToggleVisibility(); entry.Refresh(); }
            if (_presenter != null && _presenter.IsHidden != hide) _presenter.ToggleVisibility();
        }
        internal void ChangeDisplay(string mode, string scope)
        {
            DisplayPreferences.Set(Config, scope, mode);
            if (!IsPreview) Config.Save();
            if (scope == "watch") foreach (var entry in WatchRegions) entry.Presenter.SetMode(mode);
            else if (_session != null && scope == (_session.IsFullscreen ? "full" : "capture"))
            {
                _presenter?.SetMode(mode);
                if (mode == "inline") _session.EnableFollowing(() =>
                { _watchOcr ??= new Ocr.SharedOcrPool(Config.CreateOcrEngine(), Config.MovieMode); return _watchOcr.Rent(); });
            }
            EnsureFollowTimer(); _shell?.Refresh(Config);
        }
        private async Task StopSingleAsync()
        {
            var old = _session; _session = null; _presenter?.Dispose(); _presenter = null;
            var stopping = old?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            if (WatchRegions.Count == 0) { _watchOcr?.Shutdown(); _watchOcr = null; }
            await stopping;
        }
        internal Task StopAsync()
        { if (_screenStopTask is { IsCompleted: false }) return _screenStopTask; return _screenStopTask = StopScreensCoreAsync(); }
        private async Task StopScreensCoreAsync()
        {
            _operation++; _selection?.Close(); _selection = null; CancelPreparation();
            var regions = WatchRegions.ToArray(); WatchRegions.Clear();
            var stops = regions.Select(async r => { r.Presenter.Dispose(); await r.Session.DisposeAsync(); }).ToList();
            stops.Add(StopSingleAsync());
            _watchOcr?.Shutdown(); _watchOcr = null; _followTimer?.Stop();
            _shell?.SetSession(false, false, false); _shell?.SetRegionCount(0);
            await Task.WhenAll(stops); _nextRegionId = 0;
            _shell?.SetRegionCount(0);
            _shell?.SetSession(false, false, false); SetStatus(GlassLingo.Localization.UiText.T("已停止 · 等待下一次翻译"));
        }
        internal void ToggleRegionPause(WatchRegion region)
        { if (WatchRegions.Contains(region)) { region.Session.PauseResume(); region.Refresh(); RefreshScreenState(); } }
        internal void ToggleRegionVisibility(WatchRegion region)
        { if (WatchRegions.Contains(region)) { region.Presenter.ToggleVisibility(); region.Refresh(); } }
        internal async Task RemoveRegionAsync(WatchRegion region)
        {
            if (!WatchRegions.Remove(region)) return;
            region.Presenter.Dispose(); var stopping = region.Session.DisposeAsync();
            if (WatchRegions.Count == 0)
            {
                if (_session?.NeedsOcr != true) { _watchOcr?.Shutdown(); _watchOcr = null; }
                _nextRegionId = 0;
            }
            RefreshScreenState(); await stopping;
            RefreshScreenState();
        }
        private void RefreshScreenState()
        {
            _shell?.SetSession(_session != null || Monitoring, Monitoring, Monitoring && WatchRegions.All(r => r.Session.IsPaused));
            _shell?.SetRegionCount(WatchRegions.Count);
            for (int i = 0; i < WatchRegions.Count; i++) WatchRegions[i].Presenter.SetPeerCount(WatchRegions.Count);
            if (Monitoring && (_session == null || _lastScreenMode == "watch"))
                SetStatus(Localization.UiText.F(WatchRegions.All(r => r.Session.IsPaused) ? "已暂停 · {0} 个区域" : "监测中 · {0} 个区域", WatchRegions.Count));
            if (_session == null && !Monitoring) _followTimer?.Stop();
            else EnsureFollowTimer();
        }
        private void EnsureFollowTimer()
        {
            bool needed = Config.OverlayFollowSource && (WatchRegions.Any(r => !r.Session.IsPaused && r.Presenter.Mode == "inline") ||
                (_session is { IsFollowing: true } && _presenter?.Mode == "inline"));
            if (!needed) { _followTimer?.Stop(); return; }
            if (_followTimer == null)
            {
                _followTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
                _followTimer.Tick += (_, _) =>
                {
                    foreach (var entry in WatchRegions.ToArray()) { bool visible = entry.Session.RefreshPlacement(); entry.Presenter.FollowRegion(entry.Session.Region, visible); }
                    if (_session != null) { bool visible = _session.RefreshPlacement(); _presenter?.FollowRegion(_session.Region, visible); }
                };
            }
            _followTimer.Start();
        }
        private async Task StopAllAsync()
        { if (_inputTranslation is { IsTranslating: true }) _inputTranslation.StopTranslation(); await StopAsync(); }
        internal void SetStatus(string text) { _shell?.SetStatus(text); }
        private void WireShell(Shell.ShellWindow shell)
        {
            shell.ShortcutCaptureChanged += active =>
            {
                if (_shortcutCaptureActive == active) return;
                _shortcutCaptureActive = active;
                if (active) { _hotkeys?.Dispose(); _hotkeys = null; }
                else RegisterShortcuts();
            };
            shell.SettingsFinished += async view =>
            {
                _settings = null;
                if (!view.Saved) return;
                Config = view.Config;
                _inputTranslation?.UpdateConfig(Config);
                await StopAsync(); RegisterShortcuts(); shell.Refresh(Config);
                SetStatus(Localization.UiText.T("设置已保存"));
            };
        }
        internal void ShowSettings()
        {
            ShowPanel();
            _settings = _shell!.OpenSettings(IsPreview ? new AppConfig() : AppConfig.Load(), IsPreview);
        }
        protected override void OnExit(ExitEventArgs e)
        {
            _exit.Cancel(); CancelPreparation(); _presenter?.Dispose(); _inputTranslation?.Close();
            _followTimer?.Stop();
            var stopping = WatchRegions.ToArray(); WatchRegions.Clear();
            foreach (var region in stopping) region.Presenter.Dispose();
            var disposals = stopping.Select(r => r.Session.DisposeAsync().AsTask()).Append(_session?.DisposeAsync().AsTask() ?? Task.CompletedTask).ToArray();
            _watchOcr?.Shutdown();
            try { Task.WhenAll(disposals).Wait(TimeSpan.FromSeconds(3)); } catch { }
            _hotkeys?.Dispose(); _tray?.Dispose();
            try { _mutex?.ReleaseMutex(); } catch { } _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
