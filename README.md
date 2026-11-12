# GlassLingo

Windows 屏幕翻译工具，使用 C#、.NET 10 和 WPF 构建。当前版本 **0.17.0**。

## 功能

- 框选翻译、全屏翻译，以及最多 8 个区域的持续翻译。
- 译文浮窗、侧栏和点击穿透的原位覆盖；每种翻译入口单独记住显示方式。
- 游戏模式优先低占用，观影模式优先快速响应。
- Windows OCR、内置 Paddle OCR、OpenAI 兼容视觉 API OCR。
- OpenAI 兼容翻译 API、流式输出、翻译缓存和自定义全局快捷键。
- 输入翻译小窗：输入停顿后自动翻译、Enter 换行、复制译文。
- 可调透明背景、文字衬底与透明度；简体中文、繁体中文、英语、日语和韩语界面。

0.17.0 改善了持续字幕的识别响应、字号与位置稳定性及流式显示。一个持续区域使用一段译文和连续底色，避免 OCR 拆框将句尾单独显示。游戏模式保留低占用检测周期。

## 在 Windows 构建

需要 Windows x64 和 **.NET 10 SDK**。Windows OCR 识别语言依赖系统安装的语言包。项目目标为 `net10.0-windows10.0.19041.0`；界面和原生 OCR 应在 Windows 环境运行。

在仓库根目录使用 PowerShell 7：

```powershell
dotnet restore ./glasslingo/src/GlassLingo/GlassLingo.csproj
dotnet build ./glasslingo/src/GlassLingo/GlassLingo.csproj -c Release
dotnet publish ./glasslingo/src/GlassLingo/GlassLingo.csproj -c Release -r win-x64 --self-contained true -o ./artifacts/GlassLingo-0.17.0
```

运行 `artifacts/GlassLingo-0.17.0/GlassLingo.exe`。请保留整个输出目录，包括相邻 DLL、Paddle 原生依赖及 `inference` 模型目录；单独复制 EXE 不能运行全部功能。自包含发布无需另外安装 .NET 运行时。

本仓库存放源码和文档，不提交历次发布目录、运行日志、个人配置或密钥；本次不提供程序压缩包。

## 使用与数据

首次启动后在设置中配置翻译服务地址、模型和 API 密钥。API 密钥通过 Windows 凭据管理器保存；配置文件保存于 `%LOCALAPPDATA%/glasslingo/config.json`，密钥字段保存凭据引用。

Windows OCR 和 Paddle OCR 在本机识别。API OCR 会向用户配置的服务发送所选截图，翻译 API 会发送待翻译文字，输入翻译会在输入停顿后发送内容。使用 API 功能需要用户自己的服务与凭据。

完整操作说明和快捷键见 [使用说明](glasslingo/USER_GUIDE.md)。

## 验证

0.17.0 已有 **293 项断言通过，0 项失败**，包含真实 Paddle 识别及游戏、观影模式的字幕布局回归；实际网络延迟、游戏 FPS 和全部显示器组合未作保证。

在交互式 Windows 桌面运行自检：

```powershell
$test = Start-Process ./artifacts/GlassLingo-0.17.0/GlassLingo.exe -ArgumentList '--self-test', '--with-paddle' -PassThru -Wait
$test.ExitCode
```

自检会打开测试窗口，使用受控 OCR/翻译服务，并运行本地 Paddle。结果保存在输出目录的 `diagnostics/report.json`；失败时还会生成 `self-test-error.txt`。报告含观测数据，只有带 `passed` 字段的条目属于断言。源码内不包含用户 API 密钥。

## 项目结构

| 路径 | 内容 |
|---|---|
| `glasslingo/src/GlassLingo` | WPF 应用、识别、翻译、覆盖层、设置和本地化 |
| `glasslingo/src/GlassLingo/Diagnostics` | 内置回归与诊断 |
| `glasslingo/tools/Localize` | 本地化维护工具 |
| `DESIGN.md` | 界面和交互规范 |
| `glasslingo/RELEASE_NOTES.md` | 版本记录 |
| `glasslingo/IMPLEMENTATION.md` | 实现、验证范围与已知限制 |
| `licenses` | 第三方组件说明和随附许可证 |

PaddleOCRSharp、Paddle 原生运行库及模型通过 NuGet 依赖还原。第三方许可说明见 [THIRD_PARTY_NOTICES.md](licenses/THIRD_PARTY_NOTICES.md)。
