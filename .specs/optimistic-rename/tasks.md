# 任务清单：乐观改名

> 实现前需用户确认 `spec.md`。

- [x] 1. **`FileJobViewModel` 显示覆盖层**
  - 新增 `_displayNameOptimistic` / `IsDisplayNameOptimistic`
  - 新增 `SetOptimisticDisplayName`、`RevertOptimisticDisplayName`、`SyncStripTitle`
  - `Apply`：成功时不覆盖乐观名；失败/无变化时回退
  - `UpdateRenamed`：清标志、刷新最终名、同步预览标题

- [x] 2. **预览条标题**
  - `PdfPreviewViewModel.SetDisplayName(string)`
  - `PreviewStripCoordinator.Open` 使用 `job.DisplayName`

- [x] 3. **首屏命名即时显示**
  - `MainViewModel.Scan.SuggestEarlyAsync` 在 suggestion 非空时 `SetOptimisticDisplayName(stem + ".pdf")`

- [x] 4. **落盘/失败路径**
  - `MainViewModel.Naming.RenameScanInBackgroundAsync`：
    - 识别到名字但 `finalPath == pdfPath` → 回退 + 提示「改名失败，已恢复原文件名」
    - `catch` → 回退
    - 成功 → `UpdateRenamed`（不跳变 / 冲突名刷新）

- [x] 5. **手动改名优先**
  - `JobRenamer.Rename` 开始前 `RevertOptimisticDisplayName()`
  - 确认 `ApplySuffix` 移动源为 `OutputPath`

- [x] 6. **测试** `tests/WatermarkRemover.Tests/App/OptimisticRenameTests.cs`
  - 首屏显示即时更新 + 路径不变
  - 落盘成功不跳变（无 PropertyChanged 抖动）
  - 失败回退 + 提示
  - 重名冲突刷新
  - 路径隔离（JobRenamer 真实源、预览标题、手动改名失效）

- [x] 7. **全量测试**
  - `dotnet test` 全绿（984/984）

- [x] 8. **Release 构建 + local 安装 + 启动验证**
  - `release.ps1 -Version 1.16.92 -Stage local -Rebuild`
  - 日志确认 `应用启动 v1.16.92`

- [x] 9. **汇报**
  - 结论 + 实测时间线对比（首屏命名可见 vs 改名落盘）
  - 显示名/真路径隔离证据
