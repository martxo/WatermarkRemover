# 乐观改名（先显示、后落盘）规格

## 1. 背景与现状

扫描落盘后的流程（[MainViewModel.Scan.cs](file:///d:/code/src/WatermarkRemover.App/ViewModels/MainViewModel.Scan.cs)）：

1. `RunScanPassAsync` 在扫描途中拿到首页命名图时启动 `SuggestEarlyAsync`（首屏命名），随后 `Apply(FeatureResult.Success(path,...))` 把该 PDF 的真实路径写进 `OutputPath`，`DisplayName = Path.GetFileName(outputPath)`（时间戳名）。
2. `RunScanPassAsync` 末尾 `_ = RenameScanInBackgroundAsync(job, path, earlyNaming, chainTicks)`：先 `await job.Strip.WaitForSettledAsync()`（等预览自动摆正/清空白页写完），再 `ApplyAsync` 执行 `File.Move`，成功后 `job.UpdateRenamed(finalPath)` 才把 `DisplayName` 换成识别名。

后果：用户从「首屏命名完成」到「看到 黄小婷.pdf」之间，还隔着整份文档剩余页扫描 + 预览后处理稳定 + 文件改名，表现为「扫描后 N 秒才改名」。

现有关键事实：
- `DisplayName` 是唯一给人看的名字（任务卡标题 [MainWindow.xaml#L1303](file:///d:/code/src/WatermarkRemover.App/MainWindow.xaml#L1303)、删除确认文案、预览条 `PdfPreviewViewModel.Title`）。
- 所有文件操作实际用 `InputPath` / `OutputPath` / `FileName` / `PdfDocumentFile.Path`，与 `DisplayName` 无耦合（`JobBoard.Delete`、`NetdiskCoordinator`、`JobQueueService`、truth 刷新均如此）。
- 预览条标题在 `PreviewStripCoordinator.Open` 里用 `Path.GetFileName(path)` 构造，不会随乐观显示变化。
- `ApplyAsync` 失败时吞掉异常并返回原路径；`RenameScanInBackgroundAsync` 目前把「返回原路径」一律当作「无更名」，无法区分「改名失败」。

## 2. 目标

首屏命名一完成，**所有给人看的名字立即显示识别名**（如 `黄小婷.pdf`），磁盘上仍是时间戳名；`File.Move` 时机不变。成功不跳变；失败回退真实名并提示。路径操作全部仍用真实路径。

## 3. 设计

### 3.1 `FileJobViewModel`（唯一显示覆盖层）

文件：[src/WatermarkRemover.App/ViewModels/FileJobViewModel.cs](file:///d:/code/src/WatermarkRemover.App/ViewModels/FileJobViewModel.cs)

新增字段/成员：
- `private bool _displayNameOptimistic;`
- `public bool IsDisplayNameOptimistic => _displayNameOptimistic;`
- `public void SetOptimisticDisplayName(string? name)`：空名 / `IsEditing` 时忽略；线程安全（与 `SetNamingNote` 同样的 Dispatcher 处理）；设置 `DisplayName = name`、`_displayNameOptimistic = true`，并 `SyncStripTitle()`。
- `public void RevertOptimisticDisplayName(string? note = null)`：若 `_displayNameOptimistic`，清标志并把 `DisplayName` 还原为 `Path.GetFileName(OutputPath ?? InputPath)`；`note` 非空时 `SetNamingNote(note)`；`SyncStripTitle()`。线程安全。
- `private void SyncStripTitle() => Strip?.SetDisplayName(DisplayName);`

修改现有方法：
- `Apply(FeatureResult)`：
  - `Succeeded`：`if (!_displayNameOptimistic) DisplayName = Path.GetFileName(outputPath);`（保持乐观名，不被时间戳名覆盖）。
  - `Failed` / `NoChange`：调用 `RevertOptimisticDisplayName()`（不写 note），避免失败行挂着不存在的名字。
- `UpdateRenamed(string newPath)`：先 `_displayNameOptimistic = false;` 再 `DisplayName = Path.GetFileName(newPath)`；末尾 `SyncStripTitle()`。同名时 `SetProperty` 不触发通知 → 显示不跳变；冲突名（`(2)`）会刷新为最终落盘名。
- `Restore` / `AdoptSource`：不改显示语义（恢复的记录本就没有乐观值）。

### 3.2 预览条标题

- [PdfPreviewViewModel.cs](file:///d:/code/src/WatermarkRemover.App/ViewModels/PdfPreviewViewModel.cs)：新增
  `internal void SetDisplayName(string name)`：与 `Title` 相同则跳过；否则赋值并 `OnPropertyChanged(nameof(Title))`。
- [PreviewStripCoordinator.cs](file:///d:/code/src/WatermarkRemover.App/Services/PreviewStripCoordinator.cs) `Open(...)`：构造 strip 时把 `Path.GetFileName(path)` 改为 `job.DisplayName`（预览条一出现即用乐观名）。strip 内部的 `_file` 仍持有真实路径。

### 3.3 首屏命名即时显示

文件：[src/WatermarkRemover.App/ViewModels/MainViewModel.Scan.cs](file:///d:/code/src/WatermarkRemover.App/ViewModels/MainViewModel.Scan.cs) `SuggestEarlyAsync`

`TrySuggestFromImageAsync` 返回非空后，立即：
```csharp
job.SetOptimisticDisplayName(suggestion.SuggestedStem + ".pdf");
```
放在 `stillScanning()` 判断之外（扫描刚落盘也要立即显示），现有 `SetNamingNote` 文案保持不变。

### 3.4 落盘 / 失败处理

文件：[src/WatermarkRemover.App/ViewModels/MainViewModel.Naming.cs](file:///d:/code/src/WatermarkRemover.App/ViewModels/MainViewModel.Naming.cs) `RenameScanInBackgroundAsync`

- `finalPath == pdfPath` 分支：
  - 若 `job.IsDisplayNameOptimistic`（说明确实识别到了名字但没落盘）→ `job.RevertOptimisticDisplayName("改名失败，已恢复原文件名")`、`AppLog.Warn(...)`、`op.Complete("改名失败，已恢复原名")`、`LogScanChain(..., "改名失败，已恢复原名")`，return。
  - 否则保持现有「未识别到标题，保留时间戳名」。
- `catch` 分支：先 `job.RevertOptimisticDisplayName()`；现有 note 保留。
- 成功分支不变：`job.UpdateRenamed(finalPath)` 内部清标志并按最终落盘名刷新。

### 3.5 手动改名优先（边界 4）

文件：[src/WatermarkRemover.App/Services/JobRenamer.cs](file:///d:/code/src/WatermarkRemover.App/Services/JobRenamer.cs) `Rename(...)`

在计算完 `newName`、开始异步改名之前调用 `job.RevertOptimisticDisplayName()`：手动改名以真实文件名为准，乐观值失效；随后成功 `UpdateRenamed(target)` 显示最终名。`ApplySuffix` 仍以用户当前看到的名字拼后缀（只是名字模板），移动源始终是 `job.OutputPath` 真实路径。

手动改名逻辑（`CommitEdit` 已按 `DisplayName` 比较）不变；`BeginEdit` 仍以当前显示名作为编辑初值。

## 4. 路径隔离（硬约束）

不做任何 `FilePath` 概念改造，乐观值只落在 `DisplayName`：
- `InputPath` / `OutputPath` / `FileName` / `PdfDocumentFile.Path` 在乐观显示与回退过程中完全不变。
- 删除（`JobBoard.Delete` 用 `job.OutputPath`）、打开所在位置、truth 刷新（`PdfDocumentFile`/`PdfPageEditService`）、复制/短信传输、批量去水印输入均继续读取真实路径字段。
- `DisplayName` 仅用于文案/标题（删除确认文案、卡片标题、预览标题）。

## 5. 测试（新增 `tests/WatermarkRemover.Tests/App/OptimisticRenameTests.cs`）

1. **首屏命名后显示名立即更新、路径不变**：scan 风格 job 先 `AdoptSource`/`Apply`（时间戳 PDF），再 `SetOptimisticDisplayName("黄小婷.pdf")`；断言 `DisplayName=="黄小婷.pdf"`、`InputPath`/`OutputPath` 仍为时间戳路径、`IsDisplayNameOptimistic`。
   补充「先乐观后 Apply」顺序：`SetOptimisticDisplayName` → `Apply` → 显示名保持识别名。
2. **落盘成功不跳变**：`UpdateRenamed(真实新路径)`，新名与显示名相同；断言 `DisplayName` 不变、`PropertyChanged(DisplayName)` 未触发、`IsDisplayNameOptimistic==false`。
3. **失败回退并提示**：`RevertOptimisticDisplayName("改名失败，已恢复原文件名")`；断言显示名回退为真实文件名、`NamingNote`/`StatusLine` 为该提示。
4. **重名冲突**：`UpdateRenamed(".../黄小婷 (2).pdf")`；断言显示名刷新为 `黄小婷 (2).pdf`、标志清除。
5. **路径隔离**：
   - `JobRenamer` 用真实路径为移动源：真实文件在时间戳路径，job 显示乐观名，点击后缀后断言文件从真实路径移动、`DisplayName` 为最终名；断言不产生以乐观名为路径的文件。
   - 手动改名调用 `Rename` 后 `IsDisplayNameOptimistic==false`。
   - 预览：`MainViewModel.OpenPreviewStrip(job)`（复用 `PreviewStripTests` 的 FakeDialogs/FakeShell 模式）断言 `job.Strip.Title == job.DisplayName`，而 `job.OutputPath` 仍为真实路径；回退后 `Strip.Title` 跟随真实名。

## 6. 验证与交付

1. 全量测试：`C:\Users\dx\.dotnet\dotnet.exe test tests\WatermarkRemover.Tests\WatermarkRemover.Tests.csproj --logger "console;verbosity=minimal"`，要求全绿（原 976 + 新增）。
2. Release 构建 + 本机安装：`powershell -NoProfile -ExecutionPolicy Bypass -File d:\code\release.ps1 -Version 1.16.92 -Stage local -Rebuild`。
3. 启动本机安装版，确认日志 `应用启动 v1.16.92`。
4. 汇报：
   - 实测时间线对比：从 AppLog 时间戳取「扫描开始 → 首屏命名完成（名字可见）」vs「扫描开始 → 改名落盘」；给出用户看到名字的时刻由「扫描后 N 秒」提前到「扫描中 ~5 秒」的实测数字。若无本次硬件扫描日志，引用现有日志样本并注明来源。
   - 显示名/真路径隔离证据：测试断言 + 代码引用（`OutputPath` 未变、`JobRenamer` 移动源为真实路径、`PdfDocumentFile.Path` 未变）。
5. 约束：不推云盘、不出便携版、不动 GitHub；不提交 git。

## 7. 明确不做

- 不改 `File.Move` 的时机与重试策略。
- 不改 `ContentNamingService.ApplyAsync` 的返回值契约（用「最终路径是否变化」+ 乐观标志判断失败）。
- 不改动其它功能行（非 scan）的显示逻辑。
- 不引入新的路径字段或持久化格式；乐观值不写 journal。
