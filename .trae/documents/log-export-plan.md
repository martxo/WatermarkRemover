# 日志导出排障功能（设置 → 日志与排障）

## Context
软件分发到多台电脑，某台电脑「OCR 识别后不能自动重命名」却无从查起——因为**自动命名链路全链静默**：`WatermarkFeature.TryAutoName` 的 `catch { return null; }` 吞掉渲染/OCR 异常（[WatermarkFeature.cs](../../src/WatermarkRemover.Core/Features/WatermarkFeature.cs) L71-86），`SmartNamingService.TryBuildName` 三个失败点（OCR 无行 / 分类未识别 / 姓名提取失败）都只返回 null 无任何痕迹。日志里查不到「为什么没重命名」。

目标：设置窗口新增「日志与排障」栏目，可查看/复制/导出指定时间段（默认最近 1 小时）的执行日志拿回来分析；同时补齐命名链路决策日志，让日志真正能定位这类问题。

## 设计（沿用既有模式）
- **Core 纯逻辑**（可测、无 IO）仿 ProgressTextFormatter 先例；**App 层** IO + UI 代码后置 Click 风格仿 SettingsWindow 现状；无新 NuGet。
- 命名日志注入用 `Action<string, Exception?>? trace` 可选参数（Core 不引 AppLog，App 构造时传 `AppLog` 回调），不改 FeatureContext（它只有 Settings 快照，无可挂回调点）。

## 实施步骤

### 1. Core 日志解析纯逻辑（2 新文件）
- `src/WatermarkRemover.Core/Logging/LogEntry.cs`：`record LogEntry(DateTime Timestamp, string Level, string Message, string RawText)`。
- `src/WatermarkRemover.Core/Logging/LogFilter.cs`：
  - `Parse(IEnumerable<string> lines)`：正则 `^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(\w+)\] (.*)$` 解析头行；无时间戳续行归并进上条 `RawText`（保留异常堆栈），孤立续行丢弃。
  - `FilterByRange(entries, from, to)`：from 闭、to 开，跨日多文件混合行保持原序。
  - `Format(entry)`：还原 `RawText` 单条输出。

### 2. 测试（1 新文件）
`tests/WatermarkRemover.Tests/Core/LogFilterTests.cs`（xUnit [Fact]）：
1. 头行解析出 Timestamp/Level/Message；2. 异常续行归并进上条 RawText；3. 首行即续行被丢弃；4. 时间范围过滤边界（from 含 / to 不含）；5. 跨日多条混合排序稳定；6. 空输入返回空。

### 3. 命名链路日志补齐（2 改）
- `Core/Features/Naming/SmartNamingService.cs`：`TryBuildName(ocrLines, Action<string, Exception?>? trace = null)`，三个失败点 + 成功点各 trace 一条（如 `命名：OCR 行数 0` / `命名：分类未识别` / `命名：姓名提取失败（类型=营业执照）` / `命名：建议名 林孝明执照`）。
- `Core/Features/WatermarkFeature.cs`：构造加 `Action<string, Exception?>? trace = null`；`TryAutoName` trace 渲染字节数、OCR 行数，catch 记录完整异常（回调带 ex）。
- `App/ViewModels/MainViewModel.cs` L28-32：构造 WatermarkFeature 时传 `(m, ex) => AppLog.Error(m, ex)` / 无 ex 走 `AppLog.Info(m)`。

### 4. App 导出服务（1 新文件）
`src/WatermarkRemover.App/Logging/LogExportService.cs`：
- `Collect(TimeSpan range)`：枚举 `AppFolders.LogDirectory/app-*.log`，按文件名日期挑覆盖范围的每日文件 → 读行 → Core.Parse → FilterByRange。
- `BuildReport(entries)`：头部诊断块（复用 [FileJobViewModel.cs](../../src/WatermarkRemover.App/ViewModels/FileJobViewModel.cs) L281-292 的字段：应用版本 / 系统 / 位数 / 导出时间与范围 / 日志文件列表 / OcrDiagnostics 引擎链）+ 正文条目 RawText 原样。
- `Export(path, entries)`、`CopyToClipboard(report)`。

### 5. 设置窗口 UI（1 段 XAML + 1 改 cs）
- `SettingsWindow.xaml`「软件更新」组之后新栏目「日志与排障」，沿用 SectionLabel/SmallButton 样式：
  - 时间范围 ComboBox：最近 1 小时（默认）/ 24 小时 / 7 天；
  - 条数说明（11px 灰字，如 `共 42 条 · 日志保留 7 天`）；
  - 只读预览 TextBox：Consolas 等宽、高 160、自动滚动到底；
  - 一行按钮：刷新 / 复制日志 / 导出日志文件 / 打开日志文件夹。
- `SettingsWindow.xaml.cs`：`RefreshLogPreview` / `OnRefreshLog` / `OnCopyLog` / `OnExportLog`（`Microsoft.Win32.SaveFileDialog`，默认名 `WatermarkRemover-log-yyyyMMdd-HHmmss.txt`）/ `OnOpenLogFolder`（Process.Start 资源管理器，仿 MainViewModel.OpenOutput）。

## 验证
1. `C:\Users\dx\.dotnet\dotnet.exe test tests\WatermarkRemover.Tests` 全绿（155 现有 + 新增约 6-8 用例）。
2. 手动 GUI：处理 PDF 触发自动命名（成功/失败各一次），设置 → 日志与排障预览含「命名：…」决策条目与异常堆栈；切换时间范围条数变化；导出 txt 检查头部诊断 + 续行完整；复制、打开文件夹可用。
3. `dotnet publish` 过 VerifyPublishLayout 闸门（未动旁挂资源）。
4. 打包 v1.9.0 入 `d:\code\publish`，commit（英文祈使句）→ push → tag v1.9.0。
