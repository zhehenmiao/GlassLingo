using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlassLingo.Logging;
using GlassLingo.Translate;

namespace GlassLingo.Config
{
    /// <summary>
    /// 用户配置。落在 %LOCALAPPDATA%\glasslingo\config.json。
    ///
    /// 规矩：这里**不存明文 key**，只存 "keyring:xxx" 引用。
    /// 真值在 Windows 凭据管理器（CredentialStore）。
    /// </summary>
    internal sealed class AppConfig
    {
        /// <summary>源语言。null = 跟随系统猜测。P0 教训：不许自动选，但首版先留空。</summary>
        public string? SourceLanguage { get; set; }

        /// <summary>目标语言，默认简体中文。</summary>
        public string TargetLanguage { get; set; } = "zh-Hans-CN";
        public string? InputTargetLanguage { get; set; }
        public bool InputCompactMode { get; set; } = true;
        public bool OverlayFollowSource { get; set; } = true;
        public bool FilterNonText { get; set; } = true;
        public string PerformanceMode { get; set; } = "game";
        [JsonIgnore] public bool MovieMode => PerformanceMode == "movie";
        [JsonIgnore] public int EffectivePaddleThreads => MovieMode ? 4 : PaddleThreads;

        /// <summary>识别完是否直接翻译。false = 只显示识别原文（当前行为）。</summary>
        public bool AutoTranslate { get; set; }
        public string UiLanguage { get; set; } = "zh-CN";
        public string DisplayMode { get; set; } = "popup";
        public string WatchDisplayMode { get; set; } = "inline";
        public string FullscreenDisplayMode { get; set; } = "sidebar";
        public int DisplayDefaultsVersion { get; set; }
        public int PollIntervalMs { get; set; } = 500;
        public int OcrIntervalMs { get; set; } = 500;
        public int PaddleThreads { get; set; } = 2;
        public double OverlayFontSize { get; set; } = 20;
        public double OverlayOpacity { get; set; } = 0.82;
        public bool OverlayBackgroundEnabled { get; set; } = true;
        public string OverlayBackgroundColor { get; set; } = "#101820";
        public bool EnableStreaming { get; set; } = true;
        public bool GlassEffects { get; set; } = true;
        public bool GlassTransparencyEnabled { get; set; } = false;
        public double PanelTintOpacity { get; set; } = 0.10;
        public double PopupTintOpacity { get; set; } = 0.12;
        public int AppearanceVersion { get; set; }
        public ShortcutConfig Shortcuts { get; set; } = new();

        public TranslatorConfig Translator { get; set; } = new();

        /// <summary>OCR provider: windows, paddle, or openai-vision.</summary>
        public string OcrProvider { get; set; } = "windows";

        public VisionOcrConfig VisionOcr { get; set; } = new();

        /// <summary>小字放大阈值（物理像素行高）。低于它就放大 2 倍重认。</summary>
        public double SmallTextRowHeight { get; set; } = 24;
        public bool EnhanceSmallText { get; set; } = true;

        /// <summary>框选遮罩的不透明度 0~1。</summary>
        public double MaskOpacity { get; set; } = 0.69;

        // ── 运行时 ──

        [JsonIgnore]
        public static string Dir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "glasslingo");

        [JsonIgnore]
        public static string FilePath => Path.Combine(Dir, "config.json");

        /// <summary>翻译器没启用时的原因，给 UI 显示用。运行时字段，不写盘。</summary>
        [JsonIgnore]
        public string? TranslateUnavailable { get; private set; }

        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>读配置。文件不存在或坏了就返回默认值，不抛。</summary>
        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    // 首次运行：写一份模板出去，用户照着填就行，不用猜字段名
                    var fresh = new AppConfig();
                    fresh.UpgradeDisplayDefaults();
                    Log.Info($"配置文件不存在，生成模板：{FilePath}");
                    fresh.Save();
                    return fresh;
                }

                string json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    Log.Warn("配置文件是空的，用默认值");
                    return new AppConfig();
                }

                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpt);
                if (cfg == null)
                {
                    Log.Warn("配置文件反序列化为 null，用默认值");
                    return new AppConfig();
                }

                if (cfg.AppearanceVersion < 1)
                {
                    // Retune only the old shipped defaults; retain values the user actually changed.
                    if (Math.Abs(cfg.PanelTintOpacity - 0.55) < 0.001) cfg.PanelTintOpacity = 0.18;
                    if (Math.Abs(cfg.PopupTintOpacity - 0.66) < 0.001) cfg.PopupTintOpacity = 0.22;
                    if (Math.Abs(cfg.OverlayOpacity - 0.86) < 0.001) cfg.OverlayOpacity = 0.35;
                    cfg.AppearanceVersion = 1;
                }
                cfg.UpgradeOverlayAppearance();
                if (cfg.AppearanceVersion < 3)
                {
                    if (Math.Abs(cfg.PanelTintOpacity - .18) < .001) cfg.PanelTintOpacity = .10;
                    if (Math.Abs(cfg.PopupTintOpacity - .22) < .001) cfg.PopupTintOpacity = .12;
                    cfg.AppearanceVersion = 3;
                }
                cfg.UpgradeDisplayDefaults();
                cfg.Translator ??= new TranslatorConfig();
                cfg.VisionOcr ??= new VisionOcrConfig();
                cfg.Shortcuts ??= new ShortcutConfig();
                Log.Info($"配置已加载：源语言={cfg.SourceLanguage ?? "(自动)"} " +
                         $"目标={cfg.TargetLanguage} 自动翻译={cfg.AutoTranslate} " +
                         $"模型={cfg.Translator.Model}");
                return cfg;
            }
            catch (Exception ex)
            {
                Log.Error("读配置失败，用默认值", ex);
                return new AppConfig();
            }
        }

        internal void UpgradeDisplayDefaults()
        {
            if (DisplayDefaultsVersion >= 1) return;
            DisplayMode = "popup"; FullscreenDisplayMode = "sidebar"; WatchDisplayMode = "inline";
            if (OcrIntervalMs == 750) OcrIntervalMs = 500;
            DisplayDefaultsVersion = 1;
        }
        /// <summary>写配置。写完读回来确认。</summary>
        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string json = JsonSerializer.Serialize(this, JsonOpt);

                // 先写临时文件再替换，避免写一半断电留下坏文件
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);

                Log.Info($"配置已保存：{FilePath}");

                // 读回来确认（写外部状态后要验证，这条踩过坑）
                var back = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOpt);
                if (back == null)
                {
                    Log.Error("配置写入后读回是 null");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("写配置失败", ex);
                return false;
            }
        }

        internal void UpgradeOverlayAppearance()
        {
            if (AppearanceVersion >= 2) return;
            // Restore a readable backing for the old transparent shipped defaults.
            // Preserve other user-selected opacity values and allow full transparency afterwards.
            if (OverlayOpacity <= 0.001 || Math.Abs(OverlayOpacity - 0.35) < 0.001) OverlayOpacity = 0.82;
            OverlayBackgroundEnabled = true;
            AppearanceVersion = 2;
        }

        /// <summary>按配置造一个翻译器。key 取不到就返回 null。</summary>
        public ITranslator? CreateTranslator()
        {
            var t = Translator;
            if (t == null)
            {
                TranslateUnavailable = "配置里没有 translator 段";
                return null;
            }

            string? key = CredentialStore.Resolve(t.ApiKeyRef);
            if (string.IsNullOrWhiteSpace(key))
            {
                // 引用可能是 ""、可能是 null、可能是没写 keyring: 前缀——
                // 三种情况都要说清楚，否则日志里只是一个空括号（p1d 踩过）
                string why = string.IsNullOrWhiteSpace(t.ApiKeyRef)
                    ? "配置里 apiKeyRef 为空，没指定凭据"
                    : !t.ApiKeyRef.StartsWith("keyring:", StringComparison.OrdinalIgnoreCase)
                        ? $"apiKeyRef 是「{t.ApiKeyRef}」，但不是 keyring: 形式"
                        : $"凭据管理器里找不到「{t.ApiKeyRef[8..]}」";
                Log.Warn($"翻译器不可用：{why}");
                TranslateUnavailable = why;
                return null;
            }

            try
            {
                return new OpenAiCompatTranslator(t, key);
            }
            catch (Exception ex)
            {
                Log.Error("创建翻译器失败", ex);
                TranslateUnavailable = $"创建翻译器失败：{ex.Message}";
                return null;
            }
        }

        public Ocr.IOcrEngine CreateOcrEngine()
        {
            VisionOcr ??= new VisionOcrConfig();
            return OcrProvider?.ToLowerInvariant() switch
            {
                "paddle" => new Ocr.PaddleOcrEngine(EffectivePaddleThreads, MovieMode),
                "openai-vision" => new Ocr.OpenAiVisionOcrEngine(VisionOcr),
                _ => new Ocr.WindowsOcrEngine(),
            };
        }
    }

    internal sealed class VisionOcrConfig
    {
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string Model { get; set; } = "gpt-4o-mini";
        public string ApiKeyRef { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 30;
    }

    internal sealed class ShortcutConfig
    {
        public string Capture { get; set; } = "Ctrl+Shift+T";
        public string Fullscreen { get; set; } = "Ctrl+Shift+F";
        public string Watch { get; set; } = "Ctrl+Shift+R";
        public string Pause { get; set; } = "Ctrl+Shift+P";
        public string Hide { get; set; } = "Ctrl+Shift+H";
        public string Stop { get; set; } = "Ctrl+Shift+Q";
        public string Panel { get; set; } = "Ctrl+Shift+G";
        public string Input { get; set; } = "Ctrl+Shift+I";
    }
}
