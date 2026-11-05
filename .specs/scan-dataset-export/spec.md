# 扫描训练数据导出 + 未裁决页体验修复（步骤 1）

## 目标

在不引入训练代码的前提下，让产品在**生产同域**下导出可用于自监督微调的数据集，并修掉「逐页裁决有未定页时，预览什么都不做」的体验问题。本轮只做导出与体验修复，装到本机，不推云盘、不出便携版、不动 GitHub。

## 结论先行（交付口径）

- 新版本：1.16.88（仅 local 段安装）
- 开关：`ScanDatasetExportEnabled`，默认 `false`；环境变量 `WMR_DATASET_EXPORT=1` 亦可开启
- 导出目录：`%USERPROFILE%\Documents\WatermarkRemover-Dataset\<yyyyMMdd-HHmmss-fff>\`
- 用户操作：把开关打开 → 正常扫描若干份（建议正面/背面各覆盖 0°/90°/180°/270°）→ 把整个导出目录压缩后交付；程序不自动上传

## 数据契约（逐页）

会话目录内每页（N 从 1 开始，按最终 PDF 阅读序）必须具备：

| 文件 | 内容 | 硬约束 |
|---|---|---|
| `pN-probe.png` | 真正喂给 doc_ori 的 256px 探针 | 必须是采集期 `Probe256(page)` 的**同一份 byte**（1bpp 域、生产预处理），不得重新渲染 |
| `pN-frame.png` | 该页原始帧灰度 PNG | 原始扫描比例；灰度 PNG（Gray8 编码），仅复核用 |
| `pN.txt` | `pageIndex, probeTurns, probeConf, baseline, landscapeGuard, finalRotate, source` | 与最终 PDF 该页 `/Rotate` 一致；baseline 可空（弃权） |
| `session.txt` | 机型、分辨率、单双面、装订 LongEdge/ShortEdge、页数、空白页数、扫描耗时 | key=value 文本 |
| `truth.txt` | 最终落盘 PDF 每页 `/Rotate` | **写盘后回读**得到，禁止使用内存值 |

- `source` 取值：`Classifier` / `Parity` / `Majority` / `LandscapeGuard` / `Undecided` / `None`；非逐页模式下整份裁决记为 `Classifier`，无裁决记为 `None`。
- 隐私：只写本地目录，不上传；会话创建时日志打印一次目录路径。
- 体积：探针约几十 KB/页；灰度帧仅开关打开时写，5 页约 1 MB 级。

## 实现决策

### 1. 设置

- `src/WatermarkRemover.Core/Settings/AppSettings.cs`
  - 新增 `public bool ScanDatasetExportEnabled { get; set; }`，默认 `false`。
  - `Clone()` 复制该字段。
- `src/WatermarkRemover.App/ViewModels/SettingsViewModel.cs`
  - `Commit()` 写回 `_target.ScanDatasetExportEnabled = Settings.ScanDatasetExportEnabled`。
- `src/WatermarkRemover.App/SettingsPage.xaml`（扫描设置段）
  - 新增「扫描数据导出（训练用）」卡片：一个 CheckBox + 说明文字（默认关闭、只写本地、目录位置、环境变量开关）。

### 2. 导出器（新文件）

`src/WatermarkRemover.App/Services/ScanDatasetExporter.cs`

```csharp
internal sealed class ScanDatasetExporter
{
    // 关闭时返回 null；开启时创建会话目录并打印一次路径
    public static ScanDatasetExporter? Create(AppSettings settings,
        string? rootOverride = null, string? envOverride = null);

    public string SessionDirectory { get; }
    public bool IsEnabled { get; }

    // 采集期调用：把原始帧写成 c{N}-frame.png（灰度 PNG）；异常吞掉，绝不影响扫描
    public void SaveCaptureFrame(int capturePageNumber, RasterImage frame);

    // 收尾：按最终阅读序重命名帧、写 pN-probe.png / pN.txt / session.txt / truth.txt，
    // 并删除未保留页的临时帧；异常吞掉，绝不影响扫描
    public void Complete(
        IReadOnlyList<PageRecord> pages,     // 最终阅读序，PageIndex 1-based
        SessionRecord session,
        string pdfPath);
}

internal sealed record PageRecord(
    int PageIndex, int CapturePageNumber,
    byte[] ProbePng, int? ProbeTurns, float ProbeConfidence, bool? BaselineHorizontal,
    bool LandscapeGuard, int FinalRotate, string Source);

internal sealed record SessionRecord(
    string ScannerModel, string Resolution, string Duplex, string Binding,
    int PageCount, int BlankPageCount, long ScanDurationMs);
```

- 开关判定：`settings.ScanDatasetExportEnabled || (envOverride ?? Environment.GetEnvironmentVariable("WMR_DATASET_EXPORT")) == "1"`。
- 目录根：`Environment.GetFolderPath(SpecialFolder.MyDocuments)`，空时退回 `AppFolders.Root`；子目录 `WatermarkRemover-Dataset`；会话名 `yyyyMMdd-HHmmss-fff`。
- `pN-probe.png`：直接 `File.WriteAllBytes` 传入的 `ProbePng`（与生产探针同一 byte 数组）。
- `pN-frame.png`：把 `RasterImage` 的 BGRA 像素读成 `SKBitmap` `Gray8` 后 PNG 编码；失败则跳过该帧并在日志留 Debug。
- `pN.txt`：首行 `# pageIndex,probeTurns,probeConf,baseline,landscapeGuard,finalRotate,source`，次行逗号分隔值；baseline 输出 `-` 表示弃权。
- `session.txt`：key=value 多行。
- `truth.txt`：首行 `# pageIndex,rotate`，每页一行；值来自 `PdfPageRotationReader.Read(pdfPath)` 回读。

### 3. PDF `/Rotate` 回读（新文件）

`src/WatermarkRemover.Pdf/PdfPageRotationReader.cs`

```csharp
public static class PdfPageRotationReader
{
    // 返回每页 /Rotate（已归一到 0/90/180/270），按页序
    public static IReadOnlyList<int> Read(string pdfPath);
}
```

- 用 `PdfSharp` `PdfReader.Open(path, Import)`，对每页 `(page.Rotate % 360 + 360) % 360`。
- 只读，不修改文件。

### 4. 采集流水线接入

`src/WatermarkRemover.App/Services/ScanService.cs`

- `ScanOutcome` 增加 `IReadOnlyList<int>? UndecidedPageIndices = null`（最终页序、0-based），供预览做兜底与批量摆正。
- `TimedEncode` 增加 `byte[]? ProbePng = null`。
- `RunAsync` 开始时：`var exporter = ScanDatasetExporter.Create(_settings);` 记 `scanStart`。
- 采集编码任务内，在 `byte[] probe = Probe256(page)` 之后：
  - `exporter?.SaveCaptureFrame(pageNumber, page);`
  - `TimedEncode` 携带 `ProbePng: exporter is not null ? probe : null`（仅在开关打开时保留探针，控制内存）。
- PDF 写盘成功后（含 recovery 路径），调用 `exporter?.Complete(...)`：
  - `pages` 从 `keptEncodes` 按阅读序映射：`PageIndex = i+1`，`FinalRotate = perPageTurns?[i] ?? orientationTurns ?? 0` 转成 90° 值；
  - `Source`：逐页模式取 `pageDecision.Sources[i].ToString()`；否则 `orientationTurns is null ? "None" : "Classifier"`；
  - `session` 用 `device.Name`、`settings.Resolution`、`duplex`、绑定、`pages.Count`、`skippedBlanks`、耗时。
- `ScanOutcome.UndecidedPageIndices = pageDecision?.Sources.Select((s,i)=>(s,i)).Where(...Undecided).Select(i).ToArray()`。

### 5. 体验修复：按实际已决页数决定跳过

`src/WatermarkRemover.App/ViewModels/MainViewModel.Scan.cs`

- 跳过条件改为：
  ```csharp
  bool allDecided = outcome.OrientationPerPage
      ? outcome.UndecidedPages == 0
      : settledTurns is not null;
  _preview.Open(job, livePages, skipScanPostProcess: allDecided,
      undecidedPageIndices: outcome.UndecidedPageIndices,
      decidedPages: outcome.PageCount - outcome.UndecidedPages);
  ```
- 摘要文案：`N 页未能自动摆正，可点「批量摆正」一次改好`。

`src/WatermarkRemover.App/Services/PreviewStripCoordinator.cs`

- `Open(...)` 增加 `IReadOnlyList<int>? undecidedPageIndices = null, int decidedPages = 0`，传给 `PdfPreviewViewModel` 构造参数。

`src/WatermarkRemover.App/ViewModels/PdfPreviewViewModel.cs`

- 新增字段 `_undecidedPageIndices`、`_decidedPages`。
- `RunScanPostProcessAsync`：
  - 有未定页且 `_decidedPages > 0` → 调用 `_postProcessor.OrientPagesAsync(pageCount, _undecidedPageIndices, OrientationDpi)`（只探测/旋转未定页，不碰已决页）；
  - 有未定页且 `_decidedPages == 0` → 沿用现有 `AutoOrientAsync()` 整份兜底；
  - 无未定页 → 原逻辑不变。
- 新增 `UprightNoticeText` / `HasUprightNotice`：`N 页未能自动摆正，可点此批量摆正`。
- 新增 `BatchUprightCommand` 与 `ApplyBatchUprightAsync(int quarterTurns, bool onlyUndecided)`：
  - 整份：`_edits.RotateAllAsync(quarterTurns)`；
  - 仅未定页：`_edits.RotatePagesAsync(indices.Select(i => (i, quarterTurns)))`；
  - 保存后重建缩略图、刷新画布、清空提示、状态文案「已批量摆正（顺时针 X°），已保存」。
- 方向/范围由宿主回调 `Func<int, BatchUprightChoice?>? chooseBatchUpright` 提供（测试可空）。

`src/WatermarkRemover.App/Services/PdfScanPostProcessor.cs`

- 新增 `OrientPagesAsync(int pageCount, IReadOnlyList<int> pages, int probeDpi)`：
  - 仅遍历给定页；空白/横版/OCR 不可读保持原样；
  - 收集所有需转页后**一次** `RotatePagesAsync`；
  - 返回实际转动页数。

### 6. 批量摆正对话框（新文件）

`src/WatermarkRemover.App/BatchUprightDialog.xaml` + `.xaml.cs`

- 与 `AppConfirmDialog` 同一视觉语言；标题「批量摆正」。
- 说明：「N 页未能自动摆正。选择方向和应用范围，一次改好并保存。」
- 范围单选：整份文档 / 仅未摆正的 N 页（无未定页时禁用后者并默认整份）。
- 方向单选：顺时针 90° / 180° / 270°。
- 按钮：取消 / 开始摆正；返回 `BatchUprightChoice(int QuarterTurns, bool OnlyUndecided)`。
- `PreviewStripCoordinator` 传入回调：`count => BatchUprightDialog.Show(Application.Current?.MainWindow, count)`。

### 7. 预览条 UI

`src/WatermarkRemover.App/MainWindow.xaml`（缩略图 rail）

- 在自动摆正按钮下方新增「批量摆正」按钮，绑定 `Strip.BatchUprightCommand`，ToolTip「批量摆正：按选择的方向整份或逐页旋转」。
- 状态行下方新增提示块：
  ```xml
  <StackPanel Visibility="{Binding Strip.HasUprightNotice, Converter=...}" Orientation="Horizontal">
      <TextBlock Text="{Binding Strip.UprightNoticeText}" .../>
  </StackPanel>
  ```
- 提示文案严格为「N 页未能自动摆正，可点此批量摆正」。

## 测试（新增）

`tests/WatermarkRemover.Tests/App/ScanDatasetExporterTests.cs`

1. `Switch_off_writes_nothing`：`ScanDatasetExporter.Create(settings=false, rootOverride=temp, envOverride:null)` 返回 null，目录为空。
2. `Switch_on_writes_every_required_file_per_page`：3 页数据集收尾后断言每页 `pN-probe.png`、`pN-frame.png`、`pN.txt` 存在，且 `session.txt`、`truth.txt` 存在。
3. `Probe_bytes_are_the_production_bytes`：写入的探针文件字节与传入 `ProbePng` 完全相同。
4. `Truth_matches_written_pdf_rotate`：用 `PdfDocumentBuilder.Build(..., perPageQuarterTurns: [1,0,3])` 写盘，`truth.txt` 与 `PdfPageRotationReader.Read` 都为 `[90,0,270]`，且 `pN.txt` 的 `finalRotate` 一致。
5. `Capture_order_is_remapped_to_final_page_order`：采集页序与最终页序不同时，`pN-frame.png` 来自正确采集页。

`tests/WatermarkRemover.Tests/Ocr/…`（可选，纯逻辑即可）：`PdfPageRotationReaderTests` 回读 0/90/180/270。

现有 960+ 用例必须全绿。

## 验证与交付

1. `dotnet build` 全量成功。
2. `dotnet test` 全量通过，无回归。
3. 版本升至 1.16.88。
4. 执行 `release.ps1 -Version 1.16.88 -Stage local`（禁止 cloud/portable/github）。
5. 启动验证：版本号 1.16.88；开关默认关闭；开启后日志出现一次导出目录路径。
6. 中文 Markdown 汇报（先结论后细节）：版本、导出目录、开关一句人话、用户要扫几份/怎么交付、隐私说明。

## 非目标

- 不写训练代码、不做模型推理接入。
- 不上传云盘、不出便携版、不动 GitHub。
- 不改变现有 1.16.87 的裁决规则与默认扫描行为（开关关闭时零行为差异）。
