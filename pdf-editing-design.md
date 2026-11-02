# PDF 打开页面编辑功能 —— 现状梳理与统一设计方案

> 项目：`d:\code`（WatermarkRemover，WPF / .NET 8）
> 本轮范围：只做现状梳理 + 统一设计，**不修改任何代码、不提交**。
> 撰写日期：2026-11-02。文中所有结论以代码实际为准（已逐一读源码核对）。

---

## 一、结论先行

### 1.1 推荐架构：一套「与数据源无关」的编辑内核，截图与 PDF 共用

现有截图编辑的核心资产不是 UI，而是两个**纯 Skia、零 WPF 依赖**的部件：

- `ScreenshotAnnotation`（[ScreenshotAnnotation.cs](file:///D:/code/src/WatermarkRemover.Imaging/Screenshot/ScreenshotAnnotation.cs)）——不可变元素模型，坐标为「画布局部物理像素」，本就与截图、PDF、还是普通图片无关；
- `ScreenshotComposer`（[ScreenshotComposer.cs](file:///D:/code/src/WatermarkRemover.Imaging/Screenshot/ScreenshotComposer.cs)）——「一张位图 + 一组标注 → 一张新位图」的渲染合成器，输入任何来源的位图都能工作。

也就是说，**截图编辑器早已是一个「位图标注器」**，PDF 编辑缺的不是渲染能力，而是缺：

1. 一个把「页面位图（PDFium 渲染产物）+ 按页标注列表」组织起来的**编辑会话**（撤销栈、脏页、保存编排）；
2. 一个把会话接到 PDF 预览页上的**UI 载体**（编辑模式入口、标注工具条、与翻页/缩放共存）；
3. 一套**导出适配**（PNG 导出 / 烧录成新 PDF / 回写原 PDF）。

推荐架构 = 在现有资产之上加三层薄壳，**不重写任何已成熟实现**：

```
┌─────────────────────────────────────────────────────────────┐
│  编辑内核 AnnotationEditCore（新增，App 层，可单测）            │
│  · 工具注册表 IAnnotationTool（新增工具 = 注册一个实现）        │
│  · AnnotationDocument：页 → List<ScreenshotAnnotation>        │
│  · AnnotationHistory：撤销/重做栈（收敛现有两份重复实现）        │
│  · 草稿状态机：按下/移动/抬起 → 生成标注（收敛现有两份）          │
├─────────────────────────────────────────────────────────────┤
│  渲染层（已存在，直接复用）                                    │
│  · ScreenshotComposer.Compose(位图, 标注) —— 屏幕预览与        │
│    最终导出走同一条路径，保证所见即所得                         │
├─────────────────────────────────────────────────────────────┤
│  宿主适配层                                                  │
│  · 截图：ScreenshotOverlaySession / ImageMarkupWindow（重构为   │
│    内核的薄壳，行为零变化）                                    │
│  · PDF：PdfPreviewViewModel.Editing 扩展（页级编辑编排）        │
│  · 导出适配：PngExporter / PdfExporter（新增，走 PdfSharp）     │
└─────────────────────────────────────────────────────────────┘
```

**「后续新增同类工具只需注册即可接入」的落点**：`IAnnotationTool` 注册表 + `ScreenshotAnnotationKind` 扩展 + `ScreenshotComposer.Draw` 分支。三者是纯增量，现有五个工具一个字节不改。

### 1.2 PDF 编辑怎么接（推荐路线）

**编辑模型：整页编辑（单页画布），保存时三选一。**

| 维度 | 方案 | 说明 |
|---|---|---|
| 编辑粒度 | 整页编辑 | PDF 页经 PDFium 渲染成 150 DPI 位图后就是一张「图片」，直接套用截图标注器的全部交互；不做元素级选中/移动（那是下一阶段，见 §7） |
| 多页/批量 | 第一阶段只做「当前页」；标注按页存放（`Dictionary<int, List<Annotation>>`），天然支持翻页续编；「应用到全部页/批量页」作为第二阶段增强 |
| 保存方式 | **三档可选**（详见 §5.3）：① 保留原文 + 叠加批注（**默认**）；② 烧录到页面（定稿/防改）；③ 导出图片型 PDF（脱敏安全出口）。另支持导出 PNG | ① ② ③ 共用同一条「标注层 Skia 渲染」资产，区别只在写进 PDF 的形态；②③ 复用现有 `PdfPageEditService.RunWriteAsync` 写门 + 缓存失效 + 锁占用另存副本模式 |

**关键决策：三档保存，默认「保留原文 + 叠加批注」。** 市调结论两条：(a) 把标注烧录成图像页会让全文不可检索、代价偏大；(b)「页面叠加黑块」只防看不防取（底层文字仍可搜可复制）。因此保存落成三档而不是一档：

- **默认档（非破坏）**：原文 content stream 不动，标注层用 Skia 渲染成一张透明 PNG（与预览**同一条 Compose 路径**），作为图像 XObject 按页尺寸叠加——文本/字体/矢量全部保留、可检索，叠加层与预览像素级一致；
- **定稿档**：整页 150–300 DPI 重渲染 + 烧标注 → 图像页替换页内容（预览同路径、防篡改；代价是全文不可检索、体积变大）；
- **脱敏档（安全出口）**：按定稿档烧录后导出为**图片型 PDF** 新文件（原件不动）——马赛克/黑块的唯一可靠安全出口，UI 明示「视觉遮盖不可靠，彻底脱敏请导出图片 PDF」。

技术约束已核实（见 §5.3）：三档无不可调和冲突、可兼得；默认推荐档 ①，脱敏场景默认引导档 ③。

**为什么不该新写一个 PDF 标注器**：截图侧已有经过生产验证的工具交互、颜色/粗细/字号体系、多行文字、马赛克算法、撤销重做。PDF 页面在「进入编辑态」后就是一张位图，唯一新增的是「位图从哪来（PDFium 150 DPI 渲染，`PdfPageRenderCache` 已缓存）」和「存到哪去（PdfSharp 写页）」。重复实现工具交互是纯粹的浪费。

---

## 二、现有截图编辑功能能力清单（以代码为准）

### 2.1 编辑能力总览

| 能力 | 状态 | 实现位置 |
|---|---|---|
| 矩形 | ✅ | `ScreenshotAnnotationKind.Rectangle` |
| 箭头（带三角箭头） | ✅ | `ScreenshotAnnotationKind.Arrow` |
| 画笔（自由路径，Round 接头） | ✅ | `ScreenshotAnnotationKind.Pen` |
| 文字（多行，Shift+Enter 换行，微软雅黑） | ✅ | `ScreenshotAnnotationKind.Text` |
| 马赛克（12px 块像素化，可叠加） | ✅ | `ScreenshotAnnotationKind.Mosaic` |
| 颜色 5 色（红/黄/蓝/绿/黑） | ✅ | 工具栏色板，ARGB |
| 笔画粗细 3 档（1.5/3/5 DIP，按显示器缩放转物理像素） | ✅ | 工具栏 |
| 字号 3 档（12/16/20 DIP） | ✅ | 工具栏下拉 |
| 撤销 / 重做（Ctrl+Z / Ctrl+Y，标注粒度） | ✅ | 两处独立实现（见 2.4） |
| 清空全部标注 | ✅ | 工具栏 |
| 选区创建/移动/8 向缩放（有标注后冻结） | ✅ | 截图流程专用 |
| 橡皮檫（删单个元素） | ❌ 无 | — |
| 贴图/插入图片 | ❌ 无 | — |
| 椭圆/直线/序号/高亮等更多形状 | ❌ 无 | — |
| 元素选中/移动/缩放/改色 | ❌ 无 | 元素为不可变对象，无命中测试 |
| 图层顺序调整 | ❌ 无 | 按绘制顺序，后画在上 |
| 裁剪工具 | ⚠️ 变相存在 | 截图流程本身就是「选区即裁剪」；`ImageMarkupWindow` 无裁剪 |

### 2.2 元素/图层数据模型

[ScreenshotAnnotation.cs](file:///D:/code/src/WatermarkRemover.Imaging/Screenshot/ScreenshotAnnotation.cs)：

```
ScreenshotAnnotation（不可变类，init 属性）
├─ Kind: Rectangle | Arrow | Pen | Text | Mosaic
├─ Points: IReadOnlyList<ScreenshotPointF>   // 画布局部「物理像素」坐标
├─ ColorArgb: uint                            // 0xAARRGGBB
├─ Thickness: float                           // 物理像素
├─ Text?: string
└─ FontSize: float                            // 物理像素（仅文字）
```

设计要点（PDF 复用时全部保留）：
- **坐标与来源解耦**：crop-local 物理像素，渲染时不需要知道显示器、DPI、PDF 页面尺寸——PDF 侧只需统一约定「页面位图像素坐标」即可；
- **不可变元素 + 列表即图层**：撤销重做 = 列表操作，简单可靠；
- 提供工厂方法（`Rectangle/Arrow/Pen/TextAt/Mosaic`）统一构造。

### 2.3 渲染与保存导出流程

两条已存在的编辑入口，**共用同一模型与合成器**：

| 入口 | 会话类 | 预览方式 | 保存 |
|---|---|---|---|
| 实时截图遮罩 | [ScreenshotOverlaySession.cs](file:///D:/code/src/WatermarkRemover.App/Services/ScreenshotOverlaySession.cs) + [ScreenshotOverlayWindow](file:///D:/code/src/WatermarkRemover.App/ScreenshotOverlayWindow.xaml.cs) | 草稿用 WPF Shape 实时画；已确认标注整幅 `RebuildPreview()` 重算 | `Confirm()` → `ScreenshotComposer.Compose(桌面位图, 选区, 标注)` → PNG 落盘（`ScreenshotNaming` 规则）+ 剪贴板（`SaveAndCopy`） |
| 已存截图再编辑 | [ImageMarkupWindow.xaml.cs](file:///D:/code/src/WatermarkRemover.App/ImageMarkupWindow.xaml.cs) | 同上（`RefreshPreview()`） | 另存新文件 `原名_标注_HHmmss.png`，原件不动 |

合成器 [ScreenshotComposer.cs](file:///D:/code/src/WatermarkRemover.Imaging/Screenshot/ScreenshotComposer.cs)：纯 Skia。`Compose(SKBitmap 源, SKRectI 裁剪, IReadOnlyList<ScreenshotAnnotation>)` → 裁剪 + 逐标注绘制 → BGRA 位图。**预览与导出走同一条路径**，这是「所见即所得」的根本保证，PDF 接入必须延续该原则。

### 2.4 快捷键与 UI 入口

- 快捷键（两个入口一致）：`R/A/P/T/M` 切换工具、`Ctrl+Z/Y` 撤销重做、`Ctrl+S` 保存（Markup 窗）、`Enter/Ctrl+C` 确认（截图遮罩）、`Esc` 取消/取消文字、`Shift+Enter` 文字换行；
- UI：截图遮罩底部工具条（`ScreenshotOverlayWindow.xaml`，多显示器只在选区所在屏显示）与 `ImageMarkupWindow` 工具条为**两份独立 XAML + 两份独立事件线**，按钮集完全相同——这是当前最明显的重复；
- 触发链：`ScreenshotHotkey`（Core）→ `MainViewModel.Screenshot.cs` → `ScreenshotOverlaySession.RunAsync`；截图结果进列表后，长按动作打开 `ImageMarkupWindow`。

---

## 三、PDF 打开/预览页面梳理

### 3.1 加载与渲染链路

```
PdfPreviewViewModel.LoadAsync()
  → IPdfPageRenderer.GetPageCountAsync()            // 抽象在 Core.Features.Ocr
  → PdfPageRenderCache.GetPageBytesAsync(page, dpi) // (页,dpi) → PNG 字节缓存
      → PdfiumPdfPageRenderer.RenderPageAsync()     // PDFium（Docnet.Core），进程级优先级门
  → Decode(png) → 冻结 BitmapImage                  // 可跨线程绑定的 CurrentImage
  → ImageSurface.Source = CurrentImage              // 共享看图平面
```

- [PdfiumPdfPageRenderer.cs](file:///D:/code/src/WatermarkRemover.Pdf/Rendering/PdfiumPdfPageRenderer.cs)：PDFium 渲染，150 DPI 预览（≈1240×1754）/ 96 DPI 缩略图 / 90 DPI 方向探测；输出 PNG 字节；
- [PdfPageRenderCache.cs](file:///D:/code/src/WatermarkRemover.Pdf/Preview/PdfPageRenderCache.cs)：按 (页,DPI) 缓存 + **单写入门 `RunWriteAsync`**（原地 PDF 编辑与渲染互斥）+ epoch 失效 + 进程级 192MB 预算 + 折叠裁剪；
- [PdfPreviewViewModel.cs](file:///D:/code/src/WatermarkRemover.App/ViewModels/PdfPreviewViewModel.cs)：页面计数、渲染、缩略图异步填充、±1 页预取、状态行；
- UI：[PdfPreviewPage.xaml](file:///D:/code/src/WatermarkRemover.App/PdfPreviewPage.xaml)（宿主 `FullscreenViewerWindow`）：左侧胶片轨 + 顶部标题栏（缩放/插入/刷新按钮）+ 中央 `ImageSurface` + 角标旋转/删除按钮 + 翻页箭头。

### 3.2 页面与图片对象的关系

PDF 页 **不是** 图片对象：`PdfDocumentFile`（路径跟踪）→ PDFium 渲染 → PNG 字节（缓存）→ 解码为**冻结的 BitmapSource**（`CurrentImage`）→ 绑定到 `ImageSurface`。`ImageSurface`（[ImageSurface.xaml.cs](file:///D:/code/src/WatermarkRemover.App/Controls/ImageSurface.xaml.cs)）只认 `ImageSource`，提供滚轮缩放/拖动平移/双击 100%/视图旋转，**不知道 PDF 的存在**——这正是复用的结合点：编辑态只需要在它上面叠一层标注画布。

### 3.3 PDF 侧已有的「编辑」（页面级，非内容级）

[PdfPageEditService.cs](file:///D:/code/src/WatermarkRemover.App/Services/PdfPageEditService.cs) + `PdfPageRotator`/`PdfPageOrderer`/`PdfPageInserter`（PdfSharp 原地改写，侧文件保存后原子替换，锁占用自动另存副本）：

- 单页旋转、整册旋转（自动摆正）、批量旋转；
- 删除页、批量删空白页；
- 拖拽调序、插入 PDF/扫描页；
- 全部走 `RunWriteAsync` 门 + `InvalidatePage/InvalidateAll` 缓存失效 + `RememberStamp`。

**结论：PDF 的「写盘基础设施」已经成熟且经过扫描场景考验，内容编辑只需新增加载入这条管线的新操作（烧录页），不需要新造写盘机制。**

### 3.4 可复用组件三分类

**A. 可直接复用（零改动）**

| 组件 | 位置 | 复用方式 |
|---|---|---|
| `ScreenshotAnnotation` / `ScreenshotPointF` | Imaging/Screenshot | PDF 标注直接用它；坐标约定 = 页面位图像素 |
| `ScreenshotComposer` | Imaging/Screenshot | 编辑预览与导出的唯一渲染器（输入 PDF 页位图） |
| `ScreenshotTool`（工具枚举） | App/Services | 工具条与快捷键直接绑定 |
| `ImageSurface` | App/Controls | PDF 编辑态的看图/缩放/平移底座（与截图查看器同一交互） |
| `PdfPageRenderCache` | Pdf/Preview | 编辑取页位图（150 DPI 已缓存）+ 写入门 + 失效 |
| `PdfiumPdfPageRenderer` | Pdf/Rendering | 页面渲染（编辑用高优先级渲染） |
| `PdfPageEditService` 的写盘范式 | App/Services | 新增「烧录回写」操作照抄其 RunWriteAsync/Follow/Invalidate 模式 |
| `PdfSharpRuntime` / `JpegPdfAssembler` / `PdfDocumentBuilder` | Pdf | 导出新 PDF / 图像页写回的现成路线 |

**B. 需抽象后复用（当前重复实现，先收敛）**

| 现有资产 | 问题 | 抽象方向 |
|---|---|---|
| 截图遮罩工具条 + `ImageMarkupWindow` 工具条 | 两份 XAML + 两份事件线，按钮集相同 | 抽 `AnnotationToolbar` UserControl，绑定统一编辑会话 |
| 撤销/重做（`ScreenshotOverlaySession` 与 `ImageMarkupWindow` 各一份 `List+List`） | 逻辑重复 | 抽 `AnnotationHistory`（快照式栈） |
| 草稿状态机（`NewDraft/UpdateDraft/CommitDraft` 两份；`Press/Move/Release` 与 `OnMouseDown/Move/Up`） | 交互重复 | 抽 `AnnotationInputController`（指针事件 → 草稿 → 提交） |
| 文字浮层编辑器（两份） | 重复 | 抽 `AnnotationTextEditor` 控件 |
| 预览刷新（`RebuildPreview` / `RefreshPreview`） | 同一意图两种写法 | 统一进编辑会话 |

**C. 必须新写（PDF 特有）**

| 新部件 | 职责 |
|---|---|
| `PdfAnnotationDocument`（或扩展 `PdfPreviewViewModel.Editing.cs`） | 按页标注存储 `Dictionary<int, List<ScreenshotAnnotation>>`、脏页集合、编辑态开关、翻页时挂载/卸载当前页标注 |
| 标注覆盖层控件（`AnnotationOverlay` 或扩展 `ImageSurface`） | 在页面位图之上承接指针、显示草稿、承载文字浮层；缩放平移由 `ImageSurface` 负责，覆盖层只做「显示坐标 ↔ 位图像素」换算 |
| `PdfAnnotationExporter` | ① 当前页/选中页导出 PNG；② 整册导出新 PDF（页位图+烧标注 → 图像页）；③ 回写原 PDF（走 `RunWriteAsync`，替换页内容） |
| 编辑模式 UI 接线 | 看图页进入/退出编辑按钮、编辑态提示条、未保存脏标记与关闭确认、快捷键在编辑态的抢占规则 |
| 多页应用/批量（第二阶段） | 把一组标注复制到 N 页 |

---

## 四、统一编辑内核设计

### 4.1 分层与职责

```
AnnotationEditCore（新增；App 层或 Imaging 下新目录；纯逻辑可单测）
├─ AnnotationDocument          页→标注 的存储（截图场景 = 固定单页）
├─ AnnotationHistory           撤销/重做（快照栈；收敛两份重复实现）
├─ AnnotationInputController   指针按下/移动/抬起 → 草稿 → 提交/取消
├─ IAnnotationTool 注册表      新增工具 = 加一个 IAnnotationTool 实现
│   ├─ 现有五工具：RectTool / ArrowTool / PenTool / TextTool / MosaicTool
│   └─ 未来：EllipseTool / HighlighterTool / StickerTool / EraserTool …
└─ AnnotationStyleState        颜色/粗细/字号（工具条绑定）

渲染（已存在）
└─ ScreenshotComposer.Compose(pageBitmap, annotations)  ← 预览与导出同路径

宿主壳（薄）
├─ 截图：ScreenshotOverlaySession / ImageMarkupWindow 重构为壳（行为零变化）
└─ PDF ：PdfPreviewViewModel 编辑分部 + AnnotationOverlay 控件

导出适配（新增）
├─ PngAnnotationExporter        位图 → PNG（截图/PDF 单页/整册序列图）
└─ PdfAnnotationExporter        位图+标注 → 图像页 → PdfSharp 写 PDF（新文件或回写）
```

### 4.2 工具注册表（可扩展性的核心）

```csharp
// 概念形态（本轮不落码，仅定契约）
interface IAnnotationTool
{
    ScreenshotAnnotationKind Kind { get; }
    Key? Shortcut { get; }            // R/A/P/T/M…
    bool NeedsDrag { get; }           // false = 点击放置（文字、贴图）
    AnnotationCursor Cursor { get; }
    ScreenshotAnnotation? Begin(Point start, AnnotationStyleState style);   // 按下
    ScreenshotAnnotation? Update(ScreenshotAnnotation draft, Point end, …); // 移动
    bool Commit(ScreenshotAnnotation draft, out ScreenshotAnnotation? final); // 抬起（含最小尺寸过滤）
}
```

新增一个工具（如椭圆）只需要：① 枚举加一个 `Kind`；② `ScreenshotComposer.Draw` 加一个 case（纯 Skia 绘制）；③ 注册表加一个 `IAnnotationTool` 实现。**UI、撤销、历史、导出、PDF 侧全部自动接入**——这就是「注册即接入」。

### 4.3 坐标系统一约定

| 场景 | 存储坐标 | 显示换算 | 说明 |
|---|---|---|---|
| 截图遮罩 | 选区局部物理像素 | 全局↔局部按显示器 scale | 现有，不动 |
| ImageMarkup | 图片像素 | ×_scale | 现有，不动 |
| PDF 编辑 | **页面位图像素** | 位图↔显示坐标由覆盖层按 ImageSurface 当前缩放算 | 新约定；150 DPI 渲染位图即「物理像素」 |

内核只认「画布局部像素」，三个宿主各自负责进出口换算——换算逻辑集中在宿主壳里，不进内核。

---

## 五、PDF 编辑能力接入方式

### 5.1 交互流程（第一阶段）

1. 用户在 PDF 看图页（`FullscreenViewerWindow` 内的 `PdfPreviewPage`）点击「编辑」→ 进入编辑态：
   - 当前页位图（缓存命中即 150 DPI PNG）送入编辑会话；
   - 标注工具条浮现（截图同款）；滚轮缩放/平移保持可用；
2. 画标注：与截图完全相同的工具/颜色/粗细/字号/文字/马赛克/撤销重做；草稿实时预览走 `ScreenshotComposer`；
3. 翻页：当前页标注存入 `AnnotationDocument`（脏页 +1），加载新页既有标注（若有），可跨页续编；
4. 保存（三档 + PNG，详见 §5.3）：
   - **默认档 · 保留原文 + 叠加批注**：原文 content stream 不动；标注层用 Skia 渲染为透明 PNG（与预览同一条 Compose 路径），作为图像 XObject 按页尺寸叠加写入——文本可检索、非破坏；
   - **定稿档 · 烧录到页面**：脏页 150–300 DPI 重渲染 → Skia 烧标注 → 编码（PNG/JPEG）→ 替换该页内容为图像页（照 `JpegPdfAssembler` 的 XObject 路线或 `PdfDocumentBuilder`）；回写原 PDF 时在 `RunWriteAsync` 门内执行，随后 `InvalidatePage` + `RememberStamp`，锁占用沿用现有「另存副本并跟随」回退；
   - **脱敏档 · 导出图片型 PDF**：对选中页/整册按定稿档烧录，写入**新 PDF**（原件不动）；UI 明示「视觉遮盖不可靠，彻底脱敏请导出图片 PDF」；
   - **导出 PNG**：当前页或勾选页逐页导出（复用 `ScreenshotNaming`）；
5. 退出编辑：有脏页时确认（保存 / 放弃 / 取消）。

### 5.2 多页与批量（第二阶段）

- 数据结构天然支持（按页 Map）；
- 「应用到所有页 / 所选页」：把当前页标注列表深拷贝到目标页，随保存一并烧录；
- 方向：旋转后的页在烧录时按当前 /Rotate 渲染即可一致（PDFium 已按 Rotate 出图）。

### 5.3 保存策略：三档（市调结论已收敛，2026-11-02）

调研指出两个关键事实：(a) 默认把标注烧录成图像页会让全文不可检索，代价偏大；(b)「页面叠加黑块」只防看不防取——底层文字仍可搜可复制。因此保存落成**三档**，而不是原来的一档烧录。

| 档位 | 做法 | 文本可检索 | 防篡改 | 文件大小 | 用途 |
|---|---|---|---|---|---|
| ① 保留原文 + 叠加批注（**默认**） | 原文 content stream 不动；标注层用 Skia 渲染为**透明 PNG**，作为图像 XObject 按页 MediaBox 尺寸铺在该页内容之上 | ✅ 原文全保留 | ❌ | 小（仅一张标注层位图） | 日常批注，非破坏 |
| ② 烧录到页面（定稿） | 脏页 150–300 DPI 重渲染 → Skia 烧标注 → 编码为图像页 → 替换该页内容；可另存新文件或回写原 PDF | ❌ 整页栅格化 | ✅ | 大 | 定稿 / 防改 |
| ③ 导出图片型 PDF（脱敏安全出口） | 按档 ② 烧录选中页/整册 → 写入**新 PDF**，原件不动 | ❌ | ✅ | 大 | 马赛克/黑块的唯一可靠脱敏出口 |

**技术评估（约束与可行性，不含糊）：**

1. **三档可兼得，无不可调和冲突。** 三档共用同一件核心资产——「标注层的 Skia 渲染」：预览本来就是「页位图 + 标注」，把 `ScreenshotComposer` 开一个「仅标注层（透明底）」的增量入口（现有 `Compose` 行为不动），档 ① 直接贴这张透明层；档 ②③ 复用现有「整页烧录」入口。
2. **像素级一致的保证**：档 ① 的叠加层与预览中的标注是**同一次 Skia 渲染**的产物；PDF 页点坐标 ↔ 页面位图像素的换算是确定的（dpi/72，150 DPI 预览 ⇒ 每页点 150/72 px），PDFium 渲染与 PdfSharp 写页用同一 dpi 约定即对齐。不会出现「看图里有箭头、导出后没有/偏移」。
3. **档 ① 的诚实边界（UI 必须明示）**：此档下马赛克/黑块是「画在原文上面的图」——**防看不防取**，底层文字仍可搜可复制。工具条/保存对话框中固定提示：「视觉遮盖不可靠，彻底脱敏请导出图片 PDF」。
4. **文字标注在档 ① 里也是位图**（Skia 渲染字形），但这只作用于用户新加的文字，不影响原文的检索/复制能力——「文本可检索」的承诺不变。
5. **中文/特殊字体无需嵌入 PDF**：标注层是位图，绕开了 PdfSharp 嵌入中文字体的坑；原文字体本就内嵌于原 PDF，不受影响。

**推荐默认档：①（保留原文 + 叠加批注）**；用户在马赛克/遮盖后点保存时，UI 引导提示可选档 ③。若后续调研证明「定稿防改」是高频主场景，可把默认切到 ②——切换成本只是保存对话框的一个单选，三条写盘流水线都已就位。

---

## 六、分文件改动清单

> 标注：🆕 新文件 · ✏️ 修改 · ♻️ 重构（行为不变） · 本轮不实施，仅规划

### 阶段 0：内核抽取（先收敛重复，为 PDF 接入打底）

| 文件 | 动作 | 内容 |
|---|---|---|
| `src/WatermarkRemover.App/Editing/AnnotationEditCore.cs`（或 Imaging 下） | 🆕 | 编辑内核：`AnnotationDocument` + `AnnotationHistory` + `AnnotationInputController` + 工具注册表 + 样式状态 |
| `src/WatermarkRemover.App/Editing/Tools/*.cs` | 🆕 | 五个现有工具的 `IAnnotationTool` 实现（逻辑从 Session/Markup 平移） |
| `src/WatermarkRemover.App/Controls/AnnotationToolbar.xaml(+.cs)` | 🆕 | 统一工具条 UserControl（按钮集 = 现有两份并集） |
| `src/WatermarkRemover.App/Controls/AnnotationTextEditor.xaml(+.cs)` | 🆕 | 文字浮层控件 |
| `ScreenshotOverlaySession.cs` | ♻️ | 工具/草稿/历史/颜色粗细字号改委托内核；**外部行为零变化** |
| `ScreenshotOverlayWindow.xaml(.cs)` | ♻️ | 工具条改用 `AnnotationToolbar`；草绘层保留 |
| `ImageMarkupWindow.xaml(.cs)` | ♻️ | 同上，改为内核壳 |
| `tests/.../ScreenshotComposerTests.cs` 等 | ✏️ | 补内核单测（历史、草稿最小尺寸、工具注册） |

### 阶段 1：截图查看器进入编辑（验证壳的第二个宿主）

| 文件 | 动作 | 内容 |
|---|---|---|
| `src/WatermarkRemover.App/Controls/ImageSurface.xaml(.cs)` | ✏️ | 开放「覆盖层」挂载点与「显示↔位图像素」换算 API（缩放逻辑不动） |
| `src/WatermarkRemover.App/Controls/AnnotationOverlay.xaml(.cs)` | 🆕 | 指针捕获 + 草稿 Shape + 文字浮层宿主，叠在 ImageSurface 上 |
| `ImageViewerWindow` / 截图结果列表 | ✏️ | 「编辑」入口 → 复用 `ImageMarkupWindow`（或直接 inline 覆盖层） |

### 阶段 2：PDF 编辑接入（核心目标）

| 文件 | 动作 | 内容 |
|---|---|---|
| `src/WatermarkRemover.App/ViewModels/PdfPreviewViewModel.Annotation.cs` | 🆕 | `PdfAnnotationSession`：按页标注、脏页集、编辑态、进入/退出/保存编排（文件命名参照 `.Editing.cs` 分部风格） |
| `src/WatermarkRemover.App/PdfPreviewPage.xaml(.cs)` | ✏️ | 编辑态 UI：进入/退出按钮、工具条挂载、未保存提示；Esc/滚轮在编辑态的规则 |
| `src/WatermarkRemover.Pdf/Annotation/PdfAnnotationExporter.cs` | 🆕 | 三档写盘（§5.3）：① 标注层透明 PNG → 图像 XObject 叠加（默认，原文可检索）；② 烧录定稿（图像页替换，另存或回写）；③ 导出图片型 PDF；外加导出 PNG |
| `src/WatermarkRemover.Imaging/Screenshot/ScreenshotComposer.cs` | ✏️ | 增量入口：`ComposeAnnotationLayer(尺寸, 标注)` 透明底仅绘标注层（档 ① 用）；现有 `Compose` 行为不动 |
| `src/WatermarkRemover.App/Services/PdfPageEditService.cs` | ✏️ | 新增 `BurnAnnotationsAsync(IReadOnlyList<(int Page, IReadOnlyList<ScreenshotAnnotation>)>)`，照抄 Rotate/Delete 的 RunWriteAsync + Invalidate + Follow 模式 |
| `src/WatermarkRemover.Pdf/Preview/PdfPageRenderCache.cs` | ✏️（可能零改动） | 回写前确保脏页位图用高优先级重新渲染（编辑用 300 DPI 时新增缓存键） |
| `tests/.../App/PdfAnnotationTests.cs` | 🆕 | 页位图+标注→图像页→回读校验；脏页失效；锁占用副本回退 |

### 阶段 3：增强（市调结论已收敛，2026-11-02；本轮不实施）

**MVP 必做（同类免费产品全有，不做显得不专业）——按此优先级排期：**

| 文件 | 动作 | 内容 |
|---|---|---|
| 内核工具注册表 | 🆕工具 | 图片标注侧：**椭圆、荧光笔/半透明高亮、图章/贴图（预置 + 自定义图片）**；序号标注列第二梯队 |
| `PdfAnnotationSession` | ✏️ | PDF 侧 MVP：**文本框（打字机式）、签名（手绘 + 键入两种形态）、页面级遮盖脱敏（黑块/马赛克/模糊，与截图打码同源）、页面裁剪** |
| `PdfAnnotationExporter` | ✏️ | **批注 Flatten**（= 档 ② 的显式入口）、**导出图片**（= 档 ③ / PNG）的 UI 化 |
| `PdfAnnotationSession` | ✏️ | 多页批量应用（第二阶段增强） |

实现口径：
- 「高亮/下划线/便签」若走标准 PDF 注释层成本过高，用**叠加绘制**实现，但 UI 表述不得误导（不得声称是「PDF 标准批注」）；
- 签名不做手机扫码与电子签章（仅本地手绘 + 键入）；
- 页面级遮盖脱敏与截图打码同源（同一马赛克/模糊实现，注册一个新工具 + Composer 分支）。

**明确不做（第二阶段方向，本轮不排期）**：Redaction 真脱敏（底层数据移除，非视觉遮盖）、OCR、表单填写、文字/图片内容编辑、注释管理面板、水印、压缩/密码。

---

## 七、实施顺序与依赖

```
阶段 0（内核抽取，行为不变，全量测试护航）
   └─ 阶段 1（截图查看器编辑 = 内核第二个宿主，验证抽象正确性）
        └─ 阶段 2（PDF 接入：会话 → UI → 导出 A/B → 回写 C）
             └─ 阶段 3（MVP 新工具注册：椭圆/荧光笔/图章；PDF 文本框/签名/遮盖脱敏/裁剪；批量；按市调收敛的优先级）
```

原则与既有偏好一致：**先在成熟实现上收敛重复（0、1），再接入新场景（2），最后按外部输入扩展（3）**。每阶段独立可发布、可回滚。

---

## 八、风险与验证

### 8.1 风险登记

| # | 风险 | 缓解 |
|---|---|---|
| R1 | 重构内核时改变截图编辑现有行为（回归） | 阶段 0 只做委托式重构；跑全量测试（当前 785+ 条）+ 关键路径手工回归（五工具/撤销重做/文字/马赛克/保存剪贴板） |
| R2 | 预览与导出路径不一致 → 所见非所得 | 铁律：预览与导出都走 `ScreenshotComposer.Compose`；导出单测做「位图级比对」 |
| R3 | PDF 回写与渲染缓存竞态 | 只走 `PdfPageRenderCache.RunWriteAsync` 门 + 回写后 `InvalidatePage/All` + `RememberStamp`（完全复刻现有页面编辑模式）；不新造锁 |
| R4 | 锁占用文件回写失败 | 沿用 `PdfPageEditService.Follow` 的「另存副本并跟随」回退，用户无感 |
| R5 | 烧录导致 PDF 变大、文本不可检索；叠加黑块「防看不防取」 | 三档保存（§5.3）：默认档 ① 保留原文可检索；脱敏场景引导档 ③ 图片型 PDF；档 ① UI 固定提示「视觉遮盖不可靠，彻底脱敏请导出图片 PDF」 |
| R6 | 编辑态与查看器交互冲突（Esc 关闭、滚轮缩放、双击 100%） | 编辑态键盘/指针抢占规则集中在覆盖层；Esc = 退出工具而非关窗（与截图遮罩语义一致） |
| R7 | 脏页标注仅存内存，崩溃/误关丢失 | 退出确认 + 状态行常驻「N 页已修改未保存」；不引入持久化（扫描原件不可侵犯的延伸原则） |
| R8 | PDFium 优先级门竞争（编辑渲染挤占扫描/OCR） | 编辑渲染显式 `priority: true`，缩略图填充让路（现有机制） |

### 8.2 验证方式

- **单测**（每阶段）：内核（历史栈、草稿最小尺寸、工具注册表）；导出（位图+标注 → PDF 页 → 回读渲染 → 与预期位图比对）；缓存失效与门序列；
- **集成/手工**：
  1. 截图五工具 + 撤销重做 + 保存/剪贴板（阶段 0/1 回归）；
  2. PDF 编辑：画箭头→翻页→画马赛克→导出 PNG；另存新 PDF（用系统查看器打开核对）；回写原 PDF（重开核对 + 缓存刷新）；
  3. 锁占用场景（WPS 开着原文件点回写 → 应另存副本并提示）；
  4. 旋转页 + 标注（烧录图与屏幕一致）；
  5. 长文档（100+ 页）翻页编辑的性能与内存。

---

## 九、市调结论（已收敛，2026-11-02）

调研结论已到位并落入本文档：阶段 3 按 MVP 清单排期（图片侧椭圆/荧光笔/图章；PDF 侧文本框/签名/遮盖脱敏/Flatten/导出图片/页面裁剪），保存策略定为三档（§5.3，默认「保留原文 + 叠加批注」）。第二阶段方向（Redaction 真脱敏、OCR、表单、内容编辑、注释管理面板、水印、压缩/密码）明确不做，签名不含手机扫码与电子签章。本文档的 §4–§7 按「注册即接入」设计，新工具不触动既有结构。
