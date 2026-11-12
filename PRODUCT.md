# GlassLingo 产品上下文

<!-- impeccable:product-schema 1 -->

## Platform

Windows 桌面原生应用（WPF；不是 Web 页面）。

## Stack

C# / .NET 10 / WPF；Windows OCR、PaddleOCRSharp、OpenAI 兼容视觉 OCR 与翻译接口。

## Users

需要阅读屏幕外语内容的 Windows 用户；本轮重点是游戏中的菜单、对话与字幕。

## Product Purpose

通过一次框选、一次全屏截取，或固定区域持续检测，将屏幕文字识别并翻译。操作简短，游戏时控制资源占用，不干扰输入。

## Operating Context

- 托盘常驻，使用全局快捷键。
- 用户已选择全屏译文默认贴近原文覆盖，可切换侧栏。
- 用户已选择区域持续翻译默认每秒检查 2 次，优先低占用。
- 用户要求保留现有翻译浮窗，并新增可选择的“原位覆盖”：只显示译文，隐藏原文及全部诊断/窗口信息；尤其适合游戏区域持续翻译，运行时点击穿透、不抢焦点。
- 当前开发电脑为 Ryzen 9 7945HX（16 核 / 32 线程）与 RTX 4060 Laptop GPU；不能将该电脑性能视为所有用户的保证。

## Capabilities and Constraints

- 当前已有框选、OCR 引擎切换、翻译、结果覆层、API 凭据管理与设置页。
- 新需求：全屏翻译、固定区域持续翻译、流式译文、游戏资源控制、桌面 GUI 与交互重做。
- Windows OCR 依赖系统已安装的识别语言包。
- Paddle 当前使用原生推理库，完整发布目录约 618 MB；磁盘大小与运行内存需要分别评价。
- 视觉 API OCR 会上传截图；翻译 API 会上传识别文本。
- 当前已进入 0.6.0 功能与视觉实现；具体已测范围、性能样例和未验证项记录于 glasslingo/IMPLEMENTATION.md。

## Brand Commitments

名称沿用 GlassLingo。用户明确要求贴合 Glass 名字的毛玻璃、液态玻璃风格高光和通透层次；主面板与浮窗提供可调透明效果。保留游戏低占用目标，游戏原位覆盖默认使用轻量透明底板。最终实现与截图以 DESIGN.md 和发布记录为准。

## Product Principles

- 游戏输入与帧率优先。
- 变化才识别，相同文字复用译文。
- 始终有界：限制并发、队列、缓存和后台工作。
- 界面表达当前状态与下一步操作；诊断信息按需展开。
- 性能结论附带场景、测量条件和限制。
