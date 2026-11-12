using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassLingo.Translate
{
    /// <summary>
    /// 翻译引擎抽象。
    ///
    /// 为什么先定接口再写实现：P0 结论是「中文 OCR 只有 85%，擦线」，
    /// 翻译这边同样要能换。默认走 OpenAI 兼容协议（DeepSeek / OpenAI /
    /// 任何第三方网关都是这套），但接口不绑死协议——以后要接 DeepL
    /// 或本地模型，加个实现就行。
    /// </summary>
    internal interface ITranslator
    {
        /// <summary>给用户看的引擎名。</summary>
        string Name { get; }

        /// <summary>
        /// 翻译。失败抛异常，由调用方决定降级还是报错。
        /// </summary>
        /// <param name="text">原文（可以是多行）</param>
        /// <param name="from">源语言标签，如 zh-Hans-CN / en-US；null 表示自动</param>
        /// <param name="to">目标语言标签</param>
        System.Threading.Tasks.Task<TranslateResult> TranslateAsync(
            string text, string? from, string to, System.Threading.CancellationToken ct);
        System.Threading.Tasks.Task<TranslateResult> TranslateStreamingAsync(
            string text, string? from, string to, Action<string>? onText,
            System.Threading.CancellationToken ct);
    }

    internal sealed class TranslateResult
    {
        public string Text { get; set; } = "";
        public string EngineName { get; set; } = "";
        public long ElapsedMs { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }

        /// <summary>0 = 命中缓存，1 = 真实请求</summary>
        public int ApiCalls { get; set; }
    }

    /// <summary>
    /// OpenAI 兼容协议的配置。存在 %LOCALAPPDATA%\glasslingo\config.json。
    /// api_key 不写这里，只存引用（keyring:xxx），真值在 Windows 凭据管理器。
    /// </summary>
    internal sealed class TranslatorConfig
    {
        public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";
        public string Model { get; set; } = "deepseek-chat";

        /// <summary>形如 "keyring:glasslingo/deepseek"，不存明文。</summary>
        public string ApiKeyRef { get; set; } = "";

        public int TimeoutSeconds { get; set; } = 20;
        public double Temperature { get; set; } = 0.2;

        /// <summary>系统提示词。改这个就能让译文风格变。</summary>
        public string SystemPrompt { get; set; } =
            "你是翻译引擎。把用户给的文本翻译成目标语言。" +
            "只输出译文，不要解释、不要加引号、不要复述原文。" +
            "保持原文的换行结构和标点风格。专有名词、代码标识符和文件路径默认保持原样。";

        /// <summary>保留路径和代码原文，并为可读英文补充中文释义。</summary>
        public bool ExplainTechnicalText { get; set; }
    }
}
