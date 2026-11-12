---
name: GlassLingo
description: Windows 实时通透玻璃面板与无边框游戏译文。
colors:
  base: "#14171C"
  surface: "rgba(50,70,95,0.12549)"
  line: "rgba(171,191,217,0.301961)"
  ink: "#F0F2F6"
  muted: "#D1DDEB"
  accent: "#98BAFF"
  button: "rgba(55,71,93,0.098039)"
  primary: "rgba(69,99,141,0.564706)"
  field: "rgba(17,24,35,0.458824)"
typography:
  headline: { fontFamily: "Segoe UI, Microsoft YaHei UI", fontSize: "28px", fontWeight: 600 }
  body: { fontFamily: "Segoe UI, Microsoft YaHei UI", fontSize: "14px", fontWeight: 400 }
  hint: { fontFamily: "Segoe UI, Microsoft YaHei UI", fontSize: "12px", fontWeight: 400, lineHeight: "19px" }
  translation: { fontFamily: "Segoe UI, Microsoft YaHei UI", fontSize: "19px", lineHeight: "30px" }
rounded: { control: "14px", button: "14px", panel: "Windows 11 system", popup: "Windows 11 system", inline: "0px" }
spacing: { small: "8px", medium: "12px", large: "20px" }
---

# Design System: GlassLingo 0.17.0

## Direction

**让译文融入画面。** 原生 WPF 操作工具，以真实环境透射、柔化和方向高光表达 Glass；游戏字幕将界面退到文字之后。保留明确的三种翻译入口及分模式显示偏好。

本轮修正的是用户反馈的实色材质、反复重画、覆盖边框和字号跳动，不承诺与苹果私有系统材质相同。实现没有物理折射 shader、逐帧截图作为假背景或持续装饰动画。

## Material

- 系统圆角内侧为 0.75 DIP 局部镜面高光，最亮白色 alpha 65，右下边缘为 38。左上和右下是 alpha 12/9 的宽幅柔和光照；指针接近边缘时轻微改变高光位置，40 ms 节流。无待机动画或游戏字幕折射，避免回到完整白描边。

- 主面板、输入和普通浮窗创建时设置 AllowsTransparency=True，使用 WPF 逐像素 alpha，让下层文字与图案保持清晰。Window Opacity 保持 1，只改变底色透明度。
- 当前清晰透明路径不启用 Accent Blur、Acrylic 或 DwmExtendFrameIntoClientArea。独立标题命中、边缘缩放和最大化工作区处理继续保留，失焦后透明度不变。
- 关闭玻璃效果或启用高对比度时，内层底色回退为实色，最外层窗口仍使用透明圆角轮廓；不在整个 Window 填方形实色。
- 默认不透明，透明背景为独立、默认关闭的开关。开启后才应用主面板 0.10 / 浮窗 0.12 的记忆浓度，范围 0–1，设置展示为反向透明度百分比。数值只改变底色，文字 Opacity 保持 1。原生材质只在启用状态或 HWND 改变时重设，拖动透明度不反复初始化合成器。
- 染色从 (0,0) 到 (0.75,1)，停点为 0 / 0.22 / 0.8 / 1；RGB 分别为 (38,48,64)、(17,25,39)、(23,34,49)、(26,38,54)。主体 alpha=浓度×255，首尾乘 1.18 / 1.12 并截断到 255；零浓度无固定染色。
- 所有图层使用最外层 Grid 的单个 8 DIP alpha 圆角轮廓，最大化为 0；不使用 HRGN、不叠加内部圆角、不绘制整圈白边。透明窗口不依赖系统材质阴影。
- 复杂明亮背景中通过增加底色浓度恢复可读性；低浓度不是所有任意背景下的对比度保证。

## Typography and inline text

前置 px 为 WPF DIP；源码八位颜色为 AARRGGBB。中文采用 Microsoft YaHei UI 回退，使用一致的 Segoe UI 系列层级。

0.17.0 持续字幕优先采用一域一段的规则：游戏和观影共用字号、中心与首行基线稳定器，小幅 OCR 框噪声不改变排版；显著改变需连续三次独立 OCR 证据，流式重绘不计数。流式换行向下展开，在屏幕允许时使用中心对称宽度。全屏逐点覆盖保留各自锚点，不共享整段稳定器。

- 品牌 28、设置标题 26、分区 21、入口 18、正文 14、说明 12 DIP。
- 普通浮窗译文 19 DIP / 30 DIP 行高。
- 原位字幕按 OCR 行高 / DPI × 0.82 自适应，最低 10 DIP；用户设置为字号上限，默认 20、范围 12–40 DIP。相近文字框复用字号，流式输出不随句长缩放。
- 行高为字号×1.25；密集源行按中心间距限制字号，保持最小 10 DIP。译文列宽固定为源框宽度加 6 DIP，按内部行编号配对，流式不持续横向撑大；重复框去重，最终文字板不相交；同列相邻的多行对白合并输出时按段落显示；无法定位的分散标签隐藏覆盖，用户可手动选择侧栏，空间不足仍明确报告。文字内容不变而坐标移动时不重新翻译。
- 原位层无标题、原文、控件、诊断、边框或装饰圆角。文字底色默认启用，默认不透明度 0.82、范围 0–1；默认 RGB (16,24,32)，支持四种预设与自定义 #RRGGBB，仅覆盖文字的自然高度，内距 3/0/3/0 DIP。0 为完全透明。
- 轻量黑色字影：模糊 2 DIP、距离 1、Opacity 0.8。字幕层不启用实时背景模糊。
- 同数量的文字块复用 TextBlock；流式输出只更新内容，不清空再添加视觉树。

## Controls and interaction

简洁输入窗默认 400×276 DIP（最小 320×230），显示目标语言、输入、译文、复制和模式/关闭图标。输入停顿 420 ms 自动翻译，Enter 换行；复制位于译文标题右侧，完整版额外保留停止/重试操作。主界面先动作、后显示设置与引擎摘要，各入口标出实际显示模式。区域管理沿用主窗口返回导航，不新增窗口，最多八个。

ToolTip 使用深色面、清晰浅色内容与 10 DIP 圆角。已收到的流式内容以 16 ms 节拍追平，游戏追平窗 80 ms、观影 64 ms；没有待显示文字时停止计时器。字幕首次出现/恢复以 86%→100% 的 90 ms 短淡入衔接，第一帧即可读；不移动源锚点，保持字号、点击穿透与无边框。普通浮窗/侧栏内容以 78%→100% 的 140 ms 短淡入衔接，新句只触发一次，流式不重播。输入/译文占位提示用 #A6B7CC、70% 不透明度。系统关闭动画时直接显示。

原位跟随使用已译原文身份匹配当前 OCR 文字框；未知行留空，不把旧译文贴给新内容。快速窗口位移只更新坐标，字幕层不增加镜面边缘、持续动画或模糊。

按钮圆角 14 DIP，悬停高光 140 ms 进入 / 160 ms 离开，按下缩放到 0.985；禁用 Opacity 0.42，键盘焦点为清晰的 2 DIP 亮线。没有循环动画。

主面板头部保留用户认可的品牌、设置、最小化、最大化/还原、收起到托盘排列。设置在 PageHost 内替换主页，不打开第二个窗口。标题空白区可拖动，6 DIP 窗口边缘可缩放；控件保持自己的输入命中。设置保留固定底部保存按钮。

下拉框、输入框、复选框和滑块共享冷蓝灰色。滑块支持点击轨道到指定位置；主面板/设置与普通浮窗浓度分开。滚动条宽 10 DIP，透明轨道与细圆角拇指，移除系统白色粗滚动条。原文和详情使用简洁折叠箭头。

原位覆盖保持 Topmost、NOACTIVATE、点击穿透、截图排除。捕获排除失败、无坐标或行数不匹配时隐藏覆盖并提示，不自动打开弹窗；最后的完整结果保留，手动选侧栏/浮窗可读。

## Stable translation behavior

画面变化只触发文字检查。默认过滤纯符号、异常文字框、重复拉丁笔画和低分 Paddle 识别；无文字时不请求翻译。过滤可关闭，不能将规则当作完美的图形分类器。合并重复空白、换行和 CJK 字间噪声，保留英文单词边界。一般新文字连续 2 次确认，长句中疑似单字 OCR 噪声连续 3 次，数字变化不会当作近似噪声。持续空白至少 3 次且跨越 1400 ms 才清除字幕。

翻译期间即使新布局没有已译行，也保留上一条可读译文，新片段到达才替换；不会因为鼠标经过、短暂漏字或一次识别检查而先清空。版本检查仍阻止过期返回覆盖新对白。动态背景仍可能产生受限频率 OCR，不声称完全消除了识别工作。

## Verification

原生合成截图使用受控彩色背景，不把测试背景加入产品。三类窗口均检查红色背景的红通道、失焦后蓝色背景的蓝通道；不能只用接口成功、亮度变化或 WPF RenderTargetBitmap 证明背景透射。

实际 UI 检查包含最大化/还原、边缘缩放、拖动、设置导航、浓度滑块、Esc 关闭。自动检查覆盖 OCR 噪声、短暂空白、新对白、字号及无边框。发布目录 diagnostics/report.json 为最终测试记录；真实游戏 FPS、长时间动态游戏、HDR 与全部混合 DPI 配置仍需场景验收。

## Localization

UI languages: zh-CN / zh-TW / en-US / ja-JP / ko-KR. Each pack has 322 entries, embedded in the application. Dynamic resources update static labels; UiText formats runtime messages and tray labels. Language selection is separate from OCR/translation language, previews immediately, persists only on save and restores on cancel. Fonts use Segoe UI with Microsoft YaHei UI, Yu Gothic UI or Malgun Gothic by interface locale.

Keep UI selectors short; explanations belong in hints/tooltips. Wrap action descriptions. Settings use a fixed right-hand save/cancel group with remaining space for wrapping status text. Input frames and dropdowns use 14 DIP rounding. Scrollbar hit region is 10 DIP with an inset rounded thumb and transparent track; horizontal direction is handled separately. Translation-only game overlays keep their borderless rule.

## Motion and focus in 0.7.0

The navigation is one continuous tool: header geometry remains in place while the content enters from its navigation direction. Page/selected-tab feedback lasts 160–190 ms. Mouse wheel input accumulates a target over 170 ms, and pointer/key interruption discards the prior animation. No idle render loop, shader refraction or Window opacity animation is added.

Mouse focus does not alter button borders. Keyboard-only FocusVisualStyle retains an accessible focus indicator. Windows 11 DWM owns the standard rounded contour using a full client area, FRAMECHANGED and corner preference ROUND, with no custom region; no second WPF rounded plate is drawn; maximized windows use zero corner radius, and DPI changes recalculate the region. The full-window border is removed.

OCR progress belongs in the ordinary reading popup, never the translation-only game layer. Prepared Paddle loading overlaps selection, and a newly confirmed sentence does not incur another arbitrary translation delay. Faster confirmation is bounded to one accelerated recheck after regular OCR so background animation cannot establish an unlimited fast loop.

Shortcut fields record actual Ctrl/Alt chords on primary-key release. While recording, suspend the application global hotkeys; reject duplicates, reserved and occupied combinations. Esc cancels, Tab navigates, and loss of focus restores bindings.

Overlay backing settings expose an enable toggle, RGB presets/custom hex, a 0-100% transparency slider and a live preview. Preview does not mutate persisted configuration. Light, sufficiently opaque backgrounds use dark text. Text opacity remains 1.

Dropdown motion: 160ms entrance / 100ms exit, 4 DIP translation and opacity with exponential ease-out, synchronized chevron rotation. Commit selection immediately, disable input on the exiting visual, interrupt from the current frame and clear the popup on unload. Respect the system animation switch; no idle animation.

Input translation extends the existing Operate surface: default 400x276 DIP compact glass tool window, expandable to 480x470 DIP. Independent target selector, multiline input, selectable result and adjacent copy icon. Translation submits automatically 420 ms after typing pauses, waits for IME commit, and cancels stale work. Enter adds a line, Esc closes, Ctrl+Shift+C copies. No OCR initialization. Full mode adds stop/retry/clear. Empty, waiting, streaming, complete, stopped, API/clipboard error and settings recovery are explicit states.


## 观影操作模式

主页引擎摘要上方提供游戏/观影模式下拉，与设置性能页共用持久化字段。主操作仍为区域持续翻译；切模式恢复已选框。游戏保持低占用，观影采用本地 80ms 检查节奏和 4 线程策略。观影单行覆盖以源高/DPI 匹配 8–72 DIP 字号，按原文中心扩展至选区边界、保持单行，超长省略并提示；不随流式长度缩放字号。观影收到文本后约 32ms 追平，入场沿用现有 90ms 短淡入。网络 API 和视觉 OCR 限频的限制应在说明中如实呈现。


## 原文中心覆盖（0.15.1）

游戏与观影覆盖都围绕原文中心摆放，以自然宽度向两侧展开，受相邻源框的分界与选区边缘约束。取消省略号和观影强制单行；必要时自然换行，保持源字号，用测量后的多行高度定位。边缘以完整显示优先，必要时平移，不能严格维持中心的情形不溢出选区。空间实在不足仍提示，不叠画相邻字幕。该规则取代旧版固定源列宽、观影 NoWrap 的描述。


## OCR 与显示分离（0.15.2）

OCR 选区只决定采集内容，不再裁切显示。译文、衬底与原生覆盖窗口按实际测量的文字板并集扩展，保持源中心锚点，可超出选区，最终边界为当前显示器和相邻字幕单元。显示窗口坐标独立于源截图坐标，移动时同步偏移；底色自然高度向上取整，留出上下绘制余量。该规则取代 0.15.1 仍以选区边缘为界的限制。


## 连续字幕的整句单元（0.15.4）

一个持续区域内，同一基线的相邻字幕片段先合成整句，显示层也将旧锚点、流式片段和尾句缓存按同一整句源框定位。整行字幕只使用一个 TextBlock 和一块连续底色，不能因 OCR 字数、片段末尾标点或部分流式结果而把尾句单独定位到右端。不同基线和明显分开的栏目仍保留独立。该规则取代仅合并短尾框的旧规则。


## 持续区域的单段约束（0.16.0）

持续/跟随字幕呈现不再依赖识别框合并结果。一个区域只有一个文字块和一个连续底色，所有可用译文合段，以原文整体中心定位；中日韩片段不添加人工空格。字体参考原始框的代表字高，不以段落高度放大。完整桌面/普通逐点呈现保留既有规则。该约束取代旧版受间距、基线、数量和字号限制的持续字幕合行条件。
