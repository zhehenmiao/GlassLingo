using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassLingo.Config;
using GlassLingo.Logging;
using GlassLingo.Translate;

namespace GlassLingo.Settings
{
    /// <summary>
    /// 设置页。
    ///
    /// 承载于主窗口内；保存才落盘，返回或取消丢弃草稿。
    ///
    /// 编辑的是一份**配置副本**，点「保存」才落盘；取消直接丢掉，不留半截状态。
    /// </summary>
    public partial class SettingsView : UserControl
    {
        private readonly Config.AppConfig _cfg;
        private bool _saving;
        private bool _closed;
        private bool _plateReady;
        internal event Action? Completed;
        internal event Action<bool>? ShortcutCaptureStateChanged;
        private Hotkey.ShortcutCaptureBox[] ShortcutBoxes => [CaptureKeyBox, FullKeyBox, WatchKeyBox, PauseKeyBox, HideKeyBox, StopKeyBox, PanelKeyBox, InputKeyBox];

        /// <summary>点「保存」并成功落盘才为 true。</summary>
        public bool Saved { get; private set; }
        internal bool PreviewMode { get; set; }

        /// <summary>保存后的配置。App 拿它去重建翻译器。（AppConfig 是 internal，所以这里只能 internal）</summary>
        internal Config.AppConfig Config => _cfg;
        internal bool PreviewGlassEnabled => GlassCheck.IsChecked == true;
        internal double PreviewTint => TransparentGlassCheck.IsChecked == true ? 1 - PanelTintSlider.Value / 100 : 1;

        private static readonly LangItem[] SourceLanguages =
        {
            new("", GlassLingo.Localization.UiText.T("自动识别（根据选区文字）")),
            new("zh-Hans-CN", GlassLingo.Localization.UiText.T("简体中文")),
            new("zh-Hant-TW", GlassLingo.Localization.UiText.T("繁体中文")),
            new("en-US", GlassLingo.Localization.UiText.T("英文")),
            new("ja-JP", GlassLingo.Localization.UiText.T("日文")),
            new("ko-KR", GlassLingo.Localization.UiText.T("韩文")),
            new("fr-FR", GlassLingo.Localization.UiText.T("法文")),
            new("de-DE", GlassLingo.Localization.UiText.T("德文")),
            new("es-ES", GlassLingo.Localization.UiText.T("西班牙文")),
            new("ru-RU", GlassLingo.Localization.UiText.T("俄文")),
        };

        private static readonly LangItem[] TargetLanguages =
        {
            new("zh-Hans-CN", GlassLingo.Localization.UiText.T("简体中文")),
            new("zh-Hant-TW", GlassLingo.Localization.UiText.T("繁体中文")),
            new("en-US", GlassLingo.Localization.UiText.T("英文")),
            new("ja-JP", GlassLingo.Localization.UiText.T("日文")),
            new("ko-KR", GlassLingo.Localization.UiText.T("韩文")),
            new("fr-FR", GlassLingo.Localization.UiText.T("法文")),
            new("de-DE", GlassLingo.Localization.UiText.T("德文")),
            new("es-ES", GlassLingo.Localization.UiText.T("西班牙文")),
            new("ru-RU", GlassLingo.Localization.UiText.T("俄文")),
        };

        internal sealed class LangItem
        {
            public string Tag { get; }
            private readonly string _name;
            public string Name => Localization.UiText.T(_name);
            public LangItem(string tag, string name) { Tag = tag; _name = name; }
            public override string ToString() => Name;
        }
        internal static System.Collections.Generic.IReadOnlyList<LangItem> TranslationTargets => TargetLanguages;

        private readonly string _previousUiLanguage = Localization.UiText.Language;
        private bool _uiLanguageReady;
        internal SettingsView(Config.AppConfig cfg)
        {
            InitializeComponent();
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            VersionText.Text = $"glasslingo v{App.AppVersion}";
            LoadIntoUi();
            _plateReady = true; UpdatePlatePreview();
            UiLanguageCombo.SelectedValue = Localization.UiText.Language; _uiLanguageReady = true;
            Localization.UiText.Changed += RefreshUiLanguage;
            foreach (var box in ShortcutBoxes)
            {
                var owner = box;
                box.ValidateCandidate = value => ValidateShortcut(owner, value);
                box.RecordingChanged += active => ShortcutCaptureStateChanged?.Invoke(active);
                box.Feedback += SetStatus;
            }
        }

        // ── 初始化 ──────────────────────────────────────────────

        private void LoadIntoUi()
        {
            var t = _cfg.Translator ??= new TranslatorConfig();

            BaseUrlBox.Text = t.BaseUrl ?? "";
            ModelBox.Text = t.Model ?? "";
            TimeoutBox.Text = t.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            TempBox.Text = t.Temperature.ToString("0.##", CultureInfo.InvariantCulture);
            PromptBox.Text = t.SystemPrompt ?? "";

            OcrProviderCombo.SelectedValue = _cfg.OcrProvider ?? "windows";
            VisionOcrUrlBox.Text = _cfg.VisionOcr?.BaseUrl ?? "https://api.openai.com/v1";
            VisionOcrModelBox.Text = _cfg.VisionOcr?.Model ?? "gpt-4o-mini";
            string? visionKey = CredentialStore.Resolve(_cfg.VisionOcr?.ApiKeyRef);
            VisionOcrKeyHint.Text = string.IsNullOrWhiteSpace(visionKey)
                ? GlassLingo.Localization.UiText.T("尚未保存 OCR API Key。")
                : GlassLingo.Localization.UiText.T("OCR API Key 已保存在 Windows 凭据管理器；留空表示继续使用。");
            OcrProvider_SelectionChanged(OcrProviderCombo, null!);

            SourceCombo.ItemsSource = SourceLanguages;
            TargetCombo.ItemsSource = TargetLanguages;
            SourceCombo.SelectedValue = _cfg.SourceLanguage ?? "";
            if (SourceCombo.SelectedIndex < 0) SourceCombo.SelectedIndex = 0;
            var ocrLanguages = Ocr.WindowsOcrEngine.AvailableLanguages();
            SourceHint.Text = ocrLanguages.Count == 0
                ? GlassLingo.Localization.UiText.T("系统没有可用的 OCR 语言包；请在 Windows 语言设置中安装 OCR 功能。")
                : GlassLingo.Localization.UiText.F("已安装 OCR：{0}。自动模式按文字系统选择；日语需安装 ja-JP OCR 语言包。", string.Join("、", ocrLanguages));
            TargetCombo.SelectedValue = _cfg.TargetLanguage ?? "zh-Hans-CN";
            if (TargetCombo.SelectedIndex < 0) TargetCombo.SelectedIndex = 0;

            AutoTranslateCheck.IsChecked = _cfg.AutoTranslate;
            FilterNonTextCheck.IsChecked = _cfg.FilterNonText;
            ExplainTechnicalCheck.IsChecked = t.ExplainTechnicalText;

            double op = _cfg.MaskOpacity;
            if (double.IsNaN(op) || op < 0.3 || op > 0.95) op = 0.69;
            OpacitySlider.Value = op;
            OpacityValue.Text = $"{op * 100:F0}%";

            RowHeightBox.Text = _cfg.SmallTextRowHeight.ToString("0.##", CultureInfo.InvariantCulture);
            PerformanceModeCombo.SelectedValue = _cfg.MovieMode ? "movie" : "game";
            PollCombo.SelectedValue = _cfg.PollIntervalMs.ToString(); ThreadsCombo.SelectedValue = _cfg.PaddleThreads.ToString();
            OcrIntervalBox.Text = _cfg.OcrIntervalMs.ToString(); StreamingCheck.IsChecked = _cfg.EnableStreaming;
            PopupDisplayCombo.SelectedValue = _cfg.DisplayMode; FullDisplayCombo.SelectedValue = _cfg.FullscreenDisplayMode;
            WatchDisplayCombo.SelectedValue = _cfg.WatchDisplayMode;
            FontSizeBox.Text = _cfg.OverlayFontSize.ToString(CultureInfo.InvariantCulture);
            FollowSourceCheck.IsChecked = _cfg.OverlayFollowSource;
            PlateEnabledCheck.IsChecked = _cfg.OverlayBackgroundEnabled;
            PlateColorBox.Text = Overlay.OverlayAppearance.TryColor(_cfg.OverlayBackgroundColor, out _) ? _cfg.OverlayBackgroundColor : "#101820";
            PlateTransparencySlider.Value = Math.Round((1 - Math.Clamp(_cfg.OverlayOpacity, 0, 1)) * 100);
            GlassCheck.IsChecked = _cfg.GlassEffects; PanelTintSlider.Value = (1 - _cfg.PanelTintOpacity) * 100; PopupTintSlider.Value = (1 - _cfg.PopupTintOpacity) * 100;
            TransparentGlassCheck.IsChecked = _cfg.GlassTransparencyEnabled;
            RefreshGlassValues();
            var shortcuts = _cfg.Shortcuts ??= new ShortcutConfig();
            CaptureKeyBox.Text = shortcuts.Capture; FullKeyBox.Text = shortcuts.Fullscreen; WatchKeyBox.Text = shortcuts.Watch;
            PauseKeyBox.Text = shortcuts.Pause; HideKeyBox.Text = shortcuts.Hide; StopKeyBox.Text = shortcuts.Stop; PanelKeyBox.Text = shortcuts.Panel;
            InputKeyBox.Text = shortcuts.Input;
            OcrProvider_SelectionChanged(OcrProviderCombo, null!);

            RefreshKeyLabel();
        }

        /// <summary>
        /// key 输入框永远留空（不回显密钥），只在标签上说明「已存 / 没存」。
        /// </summary>
        private void RefreshKeyLabel()
        {
            string? refName = _cfg.Translator?.ApiKeyRef;
            string? stored = CredentialStore.Resolve(refName);
            bool hasKey = !string.IsNullOrWhiteSpace(stored);

            KeyLabel.Text = hasKey
                ? GlassLingo.Localization.UiText.F("API Key（已存在凭据管理器：{0}，留空 = 不改动）", TargetNameOf() ?? "?")
                : GlassLingo.Localization.UiText.T("API Key（还没有，填一个再点保存）");
            KeyHint.Text = hasKey
                ? GlassLingo.Localization.UiText.T("留空表示保留现有的 key；要换 key 就在这里输入新的，保存后覆盖")
                : GlassLingo.Localization.UiText.T("存进 Windows 凭据管理器（DPAPI 加密），config.json 里只留 keyring 引用");
        }

        /// <summary>凭据管理器里的 target 名（去掉 keyring: 前缀）。没有就用默认名。</summary>
        private string TargetNameOf()
        {
            const string prefix = "keyring:";
            string? r = _cfg.Translator?.ApiKeyRef;
            if (!string.IsNullOrWhiteSpace(r) &&
                r.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string name = r[prefix.Length..].Trim();
                if (name.Length > 0) return name;
            }
            return "glasslingo/translator";
        }

        // ── 测试连接 ────────────────────────────────────────────

        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            if (_saving) return;

            if (!TryReadTranslatorFromUi(out TranslatorConfig? tmp, out string? err))
            {
                SetStatus(err!, false);
                return;
            }

            // 输入框里有就先用输入的，空着才去凭据管理器捞
            string? key = KeyBox.Password;
            if (string.IsNullOrWhiteSpace(key)) key = CredentialStore.Resolve(_cfg.Translator?.ApiKeyRef);
            if (string.IsNullOrWhiteSpace(key))
            {
                SetStatus(GlassLingo.Localization.UiText.T("没有可用的 key：先在 API Key 里填一个（点了保存才算存下来）"), false);
                return;
            }

            string target = (TargetCombo.SelectedValue as string) ?? "zh-Hans-CN";
            TestButton.IsEnabled = false;
            TestButton.Content = GlassLingo.Localization.UiText.T("测试中…");
            SetStatus(GlassLingo.Localization.UiText.T("正在请求接口…"), null);

            try
            {
                var translator = new OpenAiCompatTranslator(tmp!, key!);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(tmp!.TimeoutSeconds + 5));

                var r = await translator.TranslateAsync(
                    "Hello, world. This is a connection test.",
                    "en-US", target, cts.Token);

                SetStatus(
                    GlassLingo.Localization.UiText.F("连接正常 · {0} · {1}ms · tokens {2}/{3} · 译文：{4}", r.EngineName, r.ElapsedMs, r.InputTokens, r.OutputTokens, Short(r.Text, 40)),
                    true);
                Log.Info(GlassLingo.Localization.UiText.F("设置页测试连接成功：{0} {1}ms", r.EngineName, r.ElapsedMs));
            }
            catch (Exception ex)
            {
                Log.Warn(GlassLingo.Localization.UiText.F("设置页测试连接失败：{0}", ex.Message));
                SetStatus(GlassLingo.Localization.UiText.F("连接失败：{0}", Short(ex.Message, 140)), false);
            }
            finally
            {
                TestButton.Content = GlassLingo.Localization.UiText.T("测试连接");
                TestButton.IsEnabled = true;
            }
        }

        // ── 保存 ────────────────────────────────────────────────

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (PreviewMode) { SetStatus(GlassLingo.Localization.UiText.T("界面验收模式：配置不会保存"), null); return; }
            if (_saving) return;

            if (!TryReadTranslatorFromUi(out TranslatorConfig? tmp, out string? err))
            {
                SetStatus(err!, false);
                return;
            }

            if (!double.TryParse(RowHeightBox.Text.Trim(), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out double rowH) || !double.IsFinite(rowH) || rowH < 0 || rowH > 200)
            {
                SetStatus(GlassLingo.Localization.UiText.T("小字重认阈值要填 0~200 的数字（0 = 关掉）"), false);
                return;
            }
            if (!int.TryParse(OcrIntervalBox.Text, out int interval) || interval < 300 || interval > 10000 ||
                !double.TryParse(FontSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double font) || !double.IsFinite(font) || font < 12 || font > 40)
            { SetStatus(GlassLingo.Localization.UiText.T("请检查 OCR 间隔（300–10000）与字号上限（12–40）。"), false); return; }
            if (PlateEnabledCheck.IsChecked == true && !Overlay.OverlayAppearance.TryColor(PlateColorBox.Text, out _))
            { SetStatus(Localization.UiText.T("请输入 #RRGGBB 格式的颜色，例如 #101820。"), false); PlateColorBox.Focus(); return; }
            double plate = 1 - PlateTransparencySlider.Value / 100;
            var keyValues = new[] { CaptureKeyBox.Text, FullKeyBox.Text, WatchKeyBox.Text, PauseKeyBox.Text, HideKeyBox.Text, StopKeyBox.Text, PanelKeyBox.Text, InputKeyBox.Text };
            var unique = new System.Collections.Generic.HashSet<string>();
            foreach (string value in keyValues)
            {
                Hotkey.ShortcutParser.TryParse("Ctrl+Shift+S", out var reservedMod, out uint reservedVk);
                if (!Hotkey.ShortcutParser.TryParse(value, out var mod, out uint vk) || !unique.Add($"{mod}:{vk}") || (mod == reservedMod && vk == reservedVk))
                { SetStatus(GlassLingo.Localization.UiText.T("快捷键无效或重复；请包含 Ctrl 或 Alt，并避免 Ctrl+Shift+S"), false); return; }
            }

            string ocrProvider = OcrProviderCombo.SelectedValue as string ?? "windows";
            VisionOcrConfig visionCfg = _cfg.VisionOcr ?? new VisionOcrConfig();
            if (ocrProvider == "openai-vision")
            {
                string visionUrl = VisionOcrUrlBox.Text.Trim();
                string visionModel = VisionOcrModelBox.Text.Trim();
                if ((!visionUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                     !visionUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ||
                    string.IsNullOrWhiteSpace(visionModel))
                {
                    SetStatus(GlassLingo.Localization.UiText.T("OCR API 地址需以 http:// 或 https:// 开头，模型名不能为空"), false);
                    return;
                }
                visionCfg.BaseUrl = visionUrl.TrimEnd('/');
                visionCfg.Model = visionModel;
                if (string.IsNullOrWhiteSpace(VisionOcrKeyBox.Password) &&
                    string.IsNullOrWhiteSpace(CredentialStore.Resolve(visionCfg.ApiKeyRef)))
                {
                    SetStatus(GlassLingo.Localization.UiText.T("视觉 API OCR 需要单独配置 API Key"), false);
                    return;
                }
            }

            _saving = true;
            try
            {
                string target = TargetNameOf();

                // 先写凭据，成功了再改配置——反过来的话配置会指向一个不存在的 key
                if (!string.IsNullOrWhiteSpace(KeyBox.Password))
                {
                    if (!CredentialStore.Write(target, KeyBox.Password))
                    {
                        SetStatus(GlassLingo.Localization.UiText.T("凭据写入失败（详见日志），配置没有保存"), false);
                        Log.Error(GlassLingo.Localization.UiText.T("设置页：CredentialStore.Write 返回 false"));
                        return;
                    }
                    Log.Info(GlassLingo.Localization.UiText.F("凭据已写入：target={0} 长度={1}", target, KeyBox.Password.Length));
                    KeyBox.Clear();
                }

                if (!string.IsNullOrWhiteSpace(VisionOcrKeyBox.Password))
                {
                    const string ocrTarget = "glasslingo/ocr";
                    if (!CredentialStore.Write(ocrTarget, VisionOcrKeyBox.Password))
                    {
                        SetStatus(GlassLingo.Localization.UiText.T("OCR 凭据写入失败（详见日志），配置没有保存"), false);
                        return;
                    }
                    visionCfg.ApiKeyRef = "keyring:" + ocrTarget;
                    VisionOcrKeyBox.Clear();
                }

                _cfg.Translator = tmp!;
                _cfg.Translator.ExplainTechnicalText = ExplainTechnicalCheck.IsChecked == true;
                _cfg.OcrProvider = ocrProvider;
                _cfg.VisionOcr = visionCfg;
                _cfg.Translator.ApiKeyRef = "keyring:" + target;
                _cfg.SourceLanguage = string.IsNullOrEmpty(SourceCombo.SelectedValue as string)
                    ? null
                    : (string)SourceCombo.SelectedValue;
                _cfg.TargetLanguage = (TargetCombo.SelectedValue as string) ?? "zh-Hans-CN";
                _cfg.AutoTranslate = AutoTranslateCheck.IsChecked == true;
                _cfg.FilterNonText = FilterNonTextCheck.IsChecked == true;
                _cfg.MaskOpacity = Math.Round(OpacitySlider.Value, 2);
                _cfg.SmallTextRowHeight = rowH;
                _cfg.PerformanceMode = PerformanceModeCombo.SelectedValue as string ?? "game";
                _cfg.PollIntervalMs = int.Parse(PollCombo.SelectedValue as string ?? "500");
                _cfg.PaddleThreads = int.Parse(ThreadsCombo.SelectedValue as string ?? "2");
                _cfg.OcrIntervalMs = interval; _cfg.EnableStreaming = StreamingCheck.IsChecked == true;
                _cfg.DisplayMode = PopupDisplayCombo.SelectedValue as string ?? "popup";
                _cfg.FullscreenDisplayMode = FullDisplayCombo.SelectedValue as string ?? "inline";
                _cfg.WatchDisplayMode = WatchDisplayCombo.SelectedValue as string ?? "inline";
                _cfg.OverlayFontSize = font; _cfg.OverlayOpacity = plate;
                _cfg.OverlayFollowSource = FollowSourceCheck.IsChecked == true;
                _cfg.OverlayBackgroundEnabled = PlateEnabledCheck.IsChecked == true;
                _cfg.OverlayBackgroundColor = Overlay.OverlayAppearance.TryColor(PlateColorBox.Text, out _) ? PlateColorBox.Text.Trim().ToUpperInvariant() : "#101820";
                _cfg.AppearanceVersion = Math.Max(2, _cfg.AppearanceVersion);
                _cfg.UiLanguage = UiLanguageCombo.SelectedValue as string ?? "zh-CN";
                _cfg.GlassEffects = GlassCheck.IsChecked == true;
                _cfg.GlassTransparencyEnabled = TransparentGlassCheck.IsChecked == true;
                _cfg.PanelTintOpacity = 1 - PanelTintSlider.Value / 100; _cfg.PopupTintOpacity = 1 - PopupTintSlider.Value / 100;
                _cfg.Shortcuts = new ShortcutConfig { Capture = keyValues[0], Fullscreen = keyValues[1], Watch = keyValues[2],
                    Pause = keyValues[3], Hide = keyValues[4], Stop = keyValues[5], Panel = keyValues[6], Input = keyValues[7] };

                if (!_cfg.Save())
                {
                    SetStatus(GlassLingo.Localization.UiText.T("配置写入失败（详见日志）"), false);
                    return;
                }

                Saved = true;
                Log.Info(GlassLingo.Localization.UiText.F("设置已保存：{0} @ {1}，", _cfg.Translator.Model, _cfg.Translator.BaseUrl) +
                         GlassLingo.Localization.UiText.F("源={0} 目标={1} ", _cfg.SourceLanguage ?? GlassLingo.Localization.UiText.T("自动"), _cfg.TargetLanguage) +
                         GlassLingo.Localization.UiText.F("自动翻译={0} 遮罩={1:F2} 小字阈值={2:F0}", _cfg.AutoTranslate, _cfg.MaskOpacity, _cfg.SmallTextRowHeight));
                Close();
            }
            catch (Exception ex)
            {
                Log.Error(GlassLingo.Localization.UiText.T("保存设置失败"), ex);
                SetStatus(GlassLingo.Localization.UiText.F("保存失败：{0}", Short(ex.Message, 120)), false);
            }
            finally
            {
                _saving = false;
            }
        }

        /// <summary>把界面上的引擎字段读成一份 TranslatorConfig，格式不对就带原因返回 false。</summary>
        private bool TryReadTranslatorFromUi(out TranslatorConfig? cfg, out string? err)
        {
            cfg = null;
            err = null;

            string baseUrl = BaseUrlBox.Text.Trim();
            if (baseUrl.Length == 0)
            {
                err = GlassLingo.Localization.UiText.T("接口地址不能为空");
                return false;
            }
            if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                err = GlassLingo.Localization.UiText.T("接口地址要以 http:// 或 https:// 开头");
                return false;
            }

            string model = ModelBox.Text.Trim();
            if (model.Length == 0)
            {
                err = GlassLingo.Localization.UiText.T("模型名不能为空");
                return false;
            }

            if (!int.TryParse(TimeoutBox.Text.Trim(), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out int timeout) || timeout < 1 || timeout > 300)
            {
                err = GlassLingo.Localization.UiText.T("超时要填 1~300 的整数（秒）");
                return false;
            }

            if (!double.TryParse(TempBox.Text.Trim(), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out double temp) || !double.IsFinite(temp) || temp < 0 || temp > 2)
            {
                err = GlassLingo.Localization.UiText.T("温度要填 0~2 之间的数字");
                return false;
            }

            cfg = new TranslatorConfig
            {
                BaseUrl = baseUrl.TrimEnd('/'),
                Model = model,
                ApiKeyRef = _cfg.Translator?.ApiKeyRef ?? "",
                TimeoutSeconds = timeout,
                Temperature = temp,
                SystemPrompt = string.IsNullOrWhiteSpace(PromptBox.Text)
                    ? new TranslatorConfig().SystemPrompt
                    : PromptBox.Text.Trim(),
            };
            return true;
        }

        // ── 杂项 ────────────────────────────────────────────────

        private void SetStatus(string text, bool? ok)
        {
            StatusText.Text = text;
            StatusText.Foreground = ok == true
                ? new SolidColorBrush(Color.FromRgb(0x6F, 0xCF, 0x97))
                : ok == false
                    ? new SolidColorBrush(Color.FromRgb(0xEB, 0x6D, 0x6D))
                    : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2));
        }

        private void OcrProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string provider = OcrProviderCombo?.SelectedValue as string ?? "windows";
            VisionOcrPanel.Visibility = provider == "openai-vision" ? Visibility.Visible : Visibility.Collapsed;
            OcrProviderHint.Text = provider switch
            {
                "paddle" => GlassLingo.Localization.UiText.T("PaddleOCR 在本机离线运行；当前自包含包约 620 MB，首次加载模型也会稍慢。"),
                "openai-vision" => GlassLingo.Localization.UiText.T("使用视觉模型逐字转录；截图会发送至配置的 API，按服务商计费。"),
                _ => GlassLingo.Localization.UiText.T("Windows OCR 无需下载模型；识别语言取决于系统语言包。"),
            };
            SourceHint.Text = provider switch
            {
                "paddle" => GlassLingo.Localization.UiText.T("Paddle 模型可识别中文、英文、日文等多种文字；自动模式根据识别文本判断语言。"),
                "openai-vision" => GlassLingo.Localization.UiText.T("由视觉模型识别截图文字并判断语言；每次识别都会上传截图到所选 API。") + " " + Localization.UiText.T("此 OCR 接口不提供文字坐标；原位跟随会回退侧栏。"),
                _ => GlassLingo.Localization.UiText.F("Windows 已安装 OCR：{0}。未安装的语言无法识别。", string.Join("、", Ocr.WindowsOcrEngine.AvailableLanguages())),
            };
        }

        private static string Short(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > max ? s[..max] + "…" : s;
        }

        internal string? ValidateShortcut(Hotkey.ShortcutCaptureBox owner, string value)
        {
            if (!Hotkey.ShortcutParser.TryParse(value, out var modifiers, out uint key)) return Localization.UiText.T("请同时按住 Ctrl 或 Alt，再按一个键。");
            Hotkey.ShortcutParser.TryParse("Ctrl+Shift+S", out var reserved, out uint reservedKey);
            if (modifiers == reserved && key == reservedKey) return Localization.UiText.T("Ctrl+Shift+S 保留给 OCR，请使用其他组合键。");
            foreach (var box in ShortcutBoxes)
                if (box != owner && Hotkey.ShortcutParser.TryParse(box.Text, out var otherModifiers, out uint otherKey) && modifiers == otherModifiers && key == otherKey)
                    return Localization.UiText.T("这个组合键已用于其他操作，请换一个。");
            if (!PreviewMode)
            {
                const int probe = 0x6A71;
                if (!Native.NativeMethods.RegisterHotKey(IntPtr.Zero, probe, modifiers, key))
                    return Localization.UiText.T("这个组合键已被系统或其他软件占用，请换一个。");
                Native.NativeMethods.UnregisterHotKey(IntPtr.Zero, probe);
            }
            return null;
        }
        internal void CancelShortcutCapture()
        { foreach (var box in ShortcutBoxes) box.CancelRecording(false); }
        private void UiLanguage_Changed(object sender, SelectionChangedEventArgs e)
        { if (_uiLanguageReady && UiLanguageCombo.SelectedValue is string language) Localization.UiText.SetLanguage(language); }
        private void RefreshUiLanguage()
        {
            RefreshKeyLabel(); OcrProvider_SelectionChanged(OcrProviderCombo, null!);
            SourceCombo.Items.Refresh(); TargetCombo.Items.Refresh();
            UpdatePlatePreview(); RefreshGlassValues();
        }
        private void PlateColor_Click(object sender, RoutedEventArgs e)
        { if (sender is Button { Tag: string color }) PlateColorBox.Text = color; }
        private void PlateColor_Changed(object sender, TextChangedEventArgs e) => UpdatePlatePreview();
        private void PlateAppearance_Changed(object sender, RoutedEventArgs e) => UpdatePlatePreview();
        private void PlateTransparency_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdatePlatePreview();
        private void UpdatePlatePreview()
        {
            if (!_plateReady) return;
            bool enabled = PlateEnabledCheck.IsChecked == true;
            PlateOptions.IsEnabled = enabled;
            bool valid = Overlay.OverlayAppearance.TryColor(PlateColorBox.Text, out var color);
            PlateColorError.Visibility = !valid && enabled ? Visibility.Visible : Visibility.Collapsed;
            PlateTransparencyValue.Text = Localization.UiText.F("{0:0}% 透明", PlateTransparencySlider.Value);
            color.A = enabled ? (byte)Math.Round((1 - PlateTransparencySlider.Value / 100) * 255) : (byte)0;
            PlatePreview.Background = new SolidColorBrush(color);
            PlatePreviewText.Foreground = Overlay.OverlayAppearance.UseDarkText(color) ? new SolidColorBrush(Color.FromRgb(16, 24, 32)) : Brushes.White;
        }
        internal void Close()
        {
            if (_closed) return; _closed = true;
            CancelShortcutCapture();
            Localization.UiText.Changed -= RefreshUiLanguage;
            if (!Saved) Localization.UiText.SetLanguage(_previousUiLanguage);
            Completed?.Invoke();
        }
        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OpacityValue != null)
                OpacityValue.Text = $"{e.NewValue * 100:F0}%";
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Glass_Changed(object sender, RoutedEventArgs e) => ApplyGlassPreview();
        private void GlassOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            RefreshGlassValues(); ApplyGlassPreview();
        }
        private void RefreshGlassValues()
        {
            if (PanelTintValue != null) PanelTintValue.Text = Localization.UiText.F("{0:0}% 透明", PanelTintSlider.Value);
            if (PopupTintValue != null) PopupTintValue.Text = Localization.UiText.F("{0:0}% 透明", PopupTintSlider.Value);
        }
        private void ApplyGlassPreview()
        {
            if (Window.GetWindow(this) is Shell.ShellWindow shell)
                shell.PreviewGlass(GlassCheck.IsChecked == true, PreviewTint);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape) Close();
        }
    }
}
