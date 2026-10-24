# 任务：操作级可观测日志

> 依据：`.specs/logging-observability/spec.md`
> 执行顺序固定；每完成一项立即标记完成。

## T1 日志基础设施（Core）

- [x] 新增 `src/WatermarkRemover.Core/Logging/LogLevel.cs`
- [x] 新增 `src/WatermarkRemover.Core/Logging/LogCategory.cs`
- [x] 新增 `src/WatermarkRemover.Core/Logging/LogLine.cs`（Format / LevelText）
- [x] 修改 `LogEntry.cs`：增加 Category / OperationId / ThreadId
- [x] 修改 `LogFilter.cs`：新格式解析 + 旧格式兼容
- [x] 验证：Core 可独立编译

## T2 日志基础设施（App）

- [x] 新增 `src/WatermarkRemover.App/Logging/OperationScope.cs`
- [x] 修改 `src/WatermarkRemover.App/Logging/AppLog.cs`
  - 级别过滤、默认 DEBUG、`WMR_LOG_LEVEL`
  - `BeginOperation` + `AsyncLocal` 关联
  - `Debug` / `Warn` / `Flush` / `Shutdown`
  - 1 秒/16 行 flush
- [x] 修改 `App.xaml.cs`：环境头、启动/退出内存、`OnExit`、崩溃 flush
- [x] 验证：`dotnet build` 通过

## T3 测试

- [x] 更新 `tests/WatermarkRemover.Tests/Core/LogFilterTests.cs`
- [x] 新增 `tests/WatermarkRemover.Tests/Core/LogLineTests.cs`
- [x] 验证：`dotnet test` 全部通过

## T4 扫描链路

- [x] `ScanService.cs`：扫描会话操作、源探测/会话/逐页/排序/PDF/写盘耗时
- [x] `ScanCoordinator.cs`：忙、取消、闸门释放
- [x] `MainViewModel.cs`：扫描流程操作、页到达、落盘/预览/命名步骤
- [x] 验证：扫描日志可按 `op=` 串联，页级耗时齐全

## T5 命名链路

- [x] `ContentNamingService.cs`：建议（PDF/首图）、逐阶段耗时、落盘
- [x] `MainViewModel.RenameScanInBackgroundAsync`：后台命名操作
- [x] `JobRenamer.cs`：手动改名操作
- [x] 验证：命名各阶段耗时、结果、失败原因齐全

## T6 预览与图像操作

- [x] `PdfPreviewViewModel.cs`：
  - 加载 / 扫描后处理 / 空白清理 / 自动摆正
  - 缩略图填充 / 渲染命中与未命中 / 缓存淘汰
  - 旋转 / 删除 / 插入扫描页
  - 渲染失败、空白判定失败、文件找回
- [x] 验证：每张图的每个编辑操作都有页号、耗时、结果

## T7 任务队列与其他

- [x] `JobQueueService.cs`：排队等待、单任务操作
- [x] `MainViewModel.ProcessJobsAsync`：批次统计
- [x] 验证：70 份文件的批次可统计总耗时/单任务耗时

## T8 整体验证

- [x] `dotnet build`（App）
- [x] `dotnet test`（全量，652 通过 / 0 失败）
- [x] 代码路径验证一次扫描的 `[SCAN] [op=…]` 链路完整性（实机多页扫描由用户验收）
  - `[SCAN] [op=…]` 链路完整
  - 逐页读取/编码耗时
  - 命名与扫描同 op
  - 预览渲染/旋转/删除有页号与耗时
- [x] 设置页导出日志，确认新字段与旧日志均可读（导出保留原始 RawText）
- [x] 报告结果与日志样例

## 完成定义

- 单次扫描全链路可检索：`op=`。
- 所有静默失败路径均有日志。
- 所有关键阶段有毫秒耗时。
- 全量测试通过。
