# 规格：操作级可观测日志（细粒度追踪）

## 1. 目标

把现有“结果日志”升级为“可复盘、可做性能分析”的操作级日志，满足：

1. 任何一次扫描 / 命名 / 预览 / 渲染 / 编辑 / 改名，都能用同一个操作 ID 串起来。
2. 每条关键记录都带：时间、级别、类别、操作 ID、线程、页号/文件、耗时、结果。
3. 失败不再静默：所有被 `catch` 吞掉的路径至少留下 `DEBUG` / `WARN` 记录。
4. 慢操作自动告警，便于从 70 份/天的日志里直接定位热点。
5. 崩溃时尽量不丢日志；启动时记录环境基线，便于判断“是这台机器的问题还是代码的问题”。

参考成熟商业软件的结构化日志模型：`Serilog` 的 level + SourceContext + LogContext，`OpenTelemetry` 的 trace/span 思路，以及桌面端常见的“operation scope + slow operation warning + environment header”。

## 2. 现状与缺口（基于代码）

| 维度 | 现状 | 缺口 |
| --- | --- | --- |
| 级别 | `INFO` / `ERROR`；`AppLog.Trace(msg, null)` 实际写 INFO | 无 DEBUG/WARN；无法按需降噪 |
| 格式 | `yyyy-MM-dd HH:mm:ss.fff [LEVEL] message` | 无类别、操作 ID、线程 |
| 关联 | 无 | 扫描→命名→预览→改名无法串联 |
| 扫描 | 只记“扫描完成：路径（N 页…）” | 无逐页读取/编码耗时、页尺寸、payload 大小、设备探测耗时 |
| 命名 | 有过程文本，无耗时 | 无渲染/方向校正/定位/整页 OCR 各阶段耗时 |
| 预览 | 只记打开/旋转/删除结果 | 无页号、DPI、渲染耗时、缓存命中/淘汰、缩略图填充耗时 |
| 异常 | 多处 `catch` 静默（缩略图、空白判定、`RenderAsync`） | 故障无日志，只剩 UI 文案 |
| 性能 | OCR 管线有 `StageTimer` | 扫描/PDF 写入/渲染/排队/改名均无耗时 |
| 环境 | 启动只记版本、数据目录 | 无 OS/架构/CPU/用户/内存/GC |
| 可靠性 | 16 行缓冲，错误立即 flush | 崩溃最多丢 15 行 |
| 导出 | 有日志导出 | 解析器不认识新字段 |

## 3. 日志格式（唯一约定）

```
2026-10-24 11:10:07.123 [INFO ] [SCAN] [op=000123] [t=12] 操作开始[SCAN]：扫描会话（单面 / 黑白）
2026-10-24 11:10:08.456 [DEBUG] [SCAN] [op=000123] [t=12] 操作步骤[SCAN]：第 1 页 620 ms（累计 620 ms，读取 480 ms，2480×3508，编码 120 ms，342 KB）
2026-10-24 11:10:15.001 [WARN ] [JOB] [op=000124] [t=15] 慢操作[JOB]：OCR 识别 8420 ms（慢操作阈值 3000 ms；成功；内存 245 MB（托管 82 MB，GC2 3））
2026-10-24 11:10:15.002 [INFO ] [SCAN] [op=000123] [t=12] 操作完成[SCAN]：扫描会话 7750 ms（成功，8 页，12.3 MB，来源 Brother…；内存 …）
```

规则：

- 级别固定 5 字符左对齐：`TRACE` / `DEBUG` / `INFO ` / `WARN ` / `ERROR`。
- `[类别]`、`[op=六位序号]`、`[t=线程号]` 为可选段；无上下文时省略，保证旧日志仍可解析。
- 类别常量：`SCAN`、`NAMING`、`JOB`、`PREVIEW`、`OCR`、`UI`、`SETTINGS`、`TRANSFER`、`NETDISK`、`UPDATE`、`FILE`。
- 操作 ID：根操作分配递增六位序号；嵌套操作复用同一 ID，形成一条链路。
- 旧行 `2026-10-16 10:00:00.123 [INFO] 消息` 必须继续被解析。

## 4. 日志级别策略

- 默认最低级别：`DEBUG`（满足“非常细致”的要求）。
- 可用环境变量 `WMR_LOG_LEVEL=TRACE|DEBUG|INFO|WARN|ERROR|OFF` 覆盖，便于现场降噪。
- 分配原则：
  - `ERROR`：操作失败、未处理异常、写盘/渲染失败。
  - `WARN`：慢操作、扫描忙、缓存超预算、可恢复但需关注的降级。
  - `INFO`：操作开始/完成、扫描完成、命名成功、页面编辑成功、启动/退出。
  - `DEBUG`：逐页、逐图、缓存命中、排队、缩略图、参数明细。
  - `TRACE`：保留给未来高频细节；默认不产生。

## 5. 代码改动清单

### 5.1 新增 `src/WatermarkRemover.Core/Logging/LogLevel.cs`

```csharp
public enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4, Off = 5 }
```

### 5.2 新增 `src/WatermarkRemover.Core/Logging/LogCategory.cs`

提供上述类别常量，避免字符串拼写漂移。

### 5.3 新增 `src/WatermarkRemover.Core/Logging/LogLine.cs`

纯函数，便于测试：

```csharp
public static string Format(DateTime timestamp, LogLevel level, string? category,
    string? operationId, int threadId, string message);

public static string LevelText(LogLevel level); // 5 字符左对齐
```

### 5.4 修改 `src/WatermarkRemover.Core/Logging/LogEntry.cs`

```csharp
public sealed record LogEntry(
    DateTime Timestamp, string Level, string Category, string OperationId,
    int ThreadId, string Message, string RawText);
```

无字段时 `Category` / `OperationId` 为空串，`ThreadId` 为 0。

### 5.5 修改 `src/WatermarkRemover.Core/Logging/LogFilter.cs`

- 正则改为支持可选段，同时兼容旧格式：
  `^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[([A-Z]+)\s*\](?: \[([A-Z]+)\])?(?: \[op=([0-9A-Za-z]+)\])?(?: \[t=(\d+)\])? (.*)$`
- 解析结果填入 `LogEntry` 新字段。
- 续行（异常堆栈）逻辑不变。

### 5.6 新增 `src/WatermarkRemover.App/Logging/OperationScope.cs`

```csharp
public sealed class OperationScope : IDisposable
{
    public string Id { get; }
    public long ElapsedMs { get; }

    public void Step(string name, string? detail = null);
    public void Note(string message, LogLevel level = LogLevel.Debug);
    public void Complete(string? outcome = null);
    public void Fail(string message, Exception? exception = null);
    public void Dispose(); // 未显式结束时写 DEBUG「操作未标记结束」并清理根上下文
}
```

行为：

- 构造时写 `操作开始[类别]：名称（detail）`，`INFO`。
- `Step` 写 `操作步骤[类别]：名称 {本步ms} ms（累计 {总ms} ms，detail）`，`DEBUG`。
- `Complete` 写 `操作完成[类别]：名称 {总ms} ms（结果；内存 …）`；超过阈值改 `WARN` 并写 `慢操作[类别]`。
- `Fail` 写 `操作失败[类别]：名称 {总ms} ms（消息；内存 …）`，`ERROR`，附异常堆栈。
- 内存文本：`工作集 N MB（托管 M MB，GC2 K）`。
- 根操作阈值可配置：`BeginOperation(..., slowMs = 3000)`；`0` 表示不告警（长扫描用）。

### 5.7 修改 `src/WatermarkRemover.App/Logging/AppLog.cs`

新增：

```csharp
public static LogLevel MinimumLevel { get; set; } // 默认 Debug，可被 WMR_LOG_LEVEL 覆盖
public static void Debug(string message);
public static void Warn(string message, Exception? exception = null);
public static OperationScope BeginOperation(string category, string name,
    string? detail = null, LogLevel startLevel = LogLevel.Info, long slowMs = 3000);
public static void Flush();
public static void Shutdown();
```

实现要点：

- `AsyncLocal<OperationContext?>` 保存当前操作 ID/类别；无根操作时 `BeginOperation` 分配序号，有则复用。
- 行格式交给 `LogLine.Format`。
- 级别过滤在 `Write` 入口完成；`Error` 永远写。
- flush 策略：错误立即；普通行满 16 行或距上次 flush 超过 1000 ms；`Shutdown` 强制。
- 方法内部继续“绝不抛异常”。
- 保留现有 `Info` / `Error` / `Trace(string, Exception?)` 签名与语义（`Trace` 仍作为 Core 回调适配器：无异常→INFO，有异常→ERROR）。

### 5.8 修改 `src/WatermarkRemover.App/App.xaml.cs`

- 启动：在现有版本行后追加环境行：
  `运行环境：<OSDescription>；<ProcessArchitecture>；N 逻辑核；用户 <User>@<Machine>；区域 <Culture>；.NET <Version>`。
- 启动与退出各记录一次内存：`AppLog.Debug($"内存[启动]：…")`。
- 新增 `OnExit`：`AppLog.Info("应用退出"); AppLog.Shutdown();`。
- 三个未处理异常处理器：记录后调用 `AppLog.Flush()`，保证崩溃前落盘。

### 5.9 修改 `src/WatermarkRemover.App/Services/ScanService.cs`

- `ScanAsync`：`using var op = AppLog.BeginOperation(SCAN, "扫描会话", $"{duplex} / {color}", slowMs: 0)`。
  - 成功 `op.Complete($"成功，{pages} 页，{kb} KB，{device}")`。
  - 取消 `op.Complete($"已取消，保留 {pages} 页")`。
  - 失败/未发现设备 `op.Fail(...)`。
- `RunAsync` 接收 `OperationScope op`，逐段 `Step`：
  - 每个扫描源探测：耗时、可用性、设备名。
  - 打开会话：设备、DPI、模式。
  - 每页：读取耗时、像素尺寸、编码耗时、payload KB（编码任务内部计时）。
  - 释放设备、页面排序、生成 PDF、写入文件：各一段耗时。
- 编码队列类型改为携带耗时的结构，不改变 PDF 产物。

### 5.10 修改 `src/WatermarkRemover.App/Services/ScanCoordinator.cs`

- 闸门占用：`AppLog.Warn("扫描被拒绝：设备忙")`。
- `CancelScan`：`AppLog.Info("请求取消扫描")`。
- 闸门释放：`AppLog.Debug("扫描闸门已释放")`。

### 5.11 修改 `src/WatermarkRemover.App/Services/ContentNamingService.cs`

- `TrySuggestAsync` / `TrySuggestFromImageAsync` / `ApplyAsync` 各包一个 `NAMING` 操作。
- `SuggestFromPageAsync` 接收当前 `OperationScope`，记录：
  - 方向校正耗时；
  - 固定区域定位耗时与命中结果；
  - 整页 OCR 耗时、行数、渲染 KB；
  - 文本规则命名耗时与结果。
- `ApplyAsync`：记录重试次数、移动耗时、目标文件名；失败 `Fail`。

### 5.12 修改 `src/WatermarkRemover.App/Services/JobQueueService.cs`

- `RunAsync`：记录每个任务的排队等待耗时（`DEBUG`）。
- `ExecuteAsync`：包 `JOB` 操作，`Step` 记录 `CanHandle`、`feature.Run` 耗时；完成时记录状态、输出路径、总耗时；意外异常 `Fail`。
- 不把整批任务包进同一个根操作，避免并发任务共用 ID。

### 5.13 修改 `src/WatermarkRemover.App/Services/JobRenamer.cs`

- `RenameCoreAsync` 包 `FILE` 操作：旧名 → 新名、重试次数、总耗时；失败 `Fail` 并保留对话框。

### 5.14 修改 `src/WatermarkRemover.App/ViewModels/PdfPreviewViewModel.cs`

这是“哪张图的哪个操作”的核心，新增：

- `LoadAsync`：包 `PREVIEW / 打开预览`，Step 记录读取页数、首屏渲染、总耗时；失败 `Fail`。
- `RunScanPostProcessAsync`、`CleanBlankPagesAsync`、`AutoOrientAsync`：各包操作；逐页检测与单次写盘分别 `Step`。
- `LoadThumbnailsAsync`：包 `缩略图填充`，记录页数、成功数、总耗时；单页异常写 `DEBUG`。
- `RotatePageAsync` / `DeletePageAsync` / `InsertScanAfterAsync`：各包操作，Step 记录写盘、缩略图、画布刷新耗时；失败 `Fail`。
- `RenderAsync`：成功写 `DEBUG`（页号、DPI、总耗时）；失败写 `WARN`（此前只设置 `StatusText`）。
- `GetPageBytesAsync`：
  - 缓存命中：`DEBUG` 页号、DPI、字节数；
  - 缓存未命中：`DEBUG` 页号、DPI、等待闸门耗时、渲染耗时、字节数、优先级；
  - 超预算清空缓存：`WARN` 清空前的字节数。
- `IsPageBlankAsync` 异常：`DEBUG`。
- `TryRelocatePdf`：成功 `INFO` 旧路径→新路径、候选数；失败原因 `DEBUG`。
- `ReleaseThumbnailImages`：`DEBUG` 释放的缩略图数、渲染缓存字节数。
- `WritePdfAsync`：`DEBUG` 写盘总耗时（调用方 Step 已有更细阶段）。

### 5.15 修改 `src/WatermarkRemover.App/ViewModels/MainViewModel.cs`

- `RunScanAsync`：包 `SCAN / 扫描流程` 操作；Step 记录扫描调用、落盘接收、打开预览、启动命名。
- `pageProgress` 回调：`DEBUG` 页到达（页号、尺寸、缩略图 KB）。
- `ProcessJobsAsync`：批次开始/结束各一行（文件数、并发数、总耗时），不创建操作上下文。
- `AddFilesAsync`：`DEBUG` 列出前 20 个文件名。
- `RenameScanInBackgroundAsync`：包 `NAMING / 后台命名` 操作，Step 记录等待预览稳定、早命名复用、PDF 探测、落盘。
- 设置变更：记录旧值→新值（扫描版面、色彩）。

## 6. 验证

### 自动化

```powershell
dotnet build d:\code\src\WatermarkRemover.App\WatermarkRemover.App.csproj -c Debug --nologo -v q
dotnet test d:\code\tests\WatermarkRemover.Tests\WatermarkRemover.Tests.csproj --nologo
```

新增/更新测试：

- `tests/WatermarkRemover.Tests/Core/LogFilterTests.cs`
  - 新格式解析出 Category / OperationId / ThreadId；
  - 旧格式仍解析；
  - 续行堆栈仍合并。
- `tests/WatermarkRemover.Tests/Core/LogLineTests.cs`
  - 级别 5 字符对齐；
  - 可选段缺省时无多余空格；
  - `Format` → `Parse` 往返一致。

### 手工验收

1. 启动应用，日志应出现环境行、`[t=]`、内存行。
2. 扫描一份多页文档：
   - `[SCAN] [op=…]` 下能看到逐页读取/编码耗时；
   - 命名日志与扫描日志同 `op`；
   - 预览打开、缩略图填充、首屏渲染都有耗时；
   - 旋转/删除/插入页各有独立操作与页号。
3. 用设置页“导出日志文件”导出，确认新字段与旧日志都能导出、堆栈完整。
4. 强制结束进程后重启，日志文件应保留到最后一秒内的记录（最多丢 1 秒）。

## 7. 不做的事

- 不引入 Serilog / NLog 等第三方依赖，保持现有零依赖日志实现。
- 不改日志文件位置、不降低保留天数（仍为 7 天）。
- 不新增设置页 UI 开关；级别通过环境变量覆盖。
- 不改业务行为、不改 PDF 产物、不改扫描/命名/预览的现有交互。

## 8. 风险与对策

| 风险 | 对策 |
| --- | --- |
| 日志量增大 | 逐页/逐图细节用 DEBUG；默认级别可被环境变量调高 |
| 高频 IO 拖慢渲染 | 保持缓冲写 + 1 秒 flush，错误立即 flush |
| 旧日志导出失效 | 正则兼容旧格式，测试覆盖 |
| AsyncLocal 串号 | 每批并发任务各自建根操作；嵌套复用父 ID |
| 长扫描被误报慢操作 | 扫描/预览加载可传 `slowMs: 0` 或较大阈值 |

## 9. 验收标准

- 一次扫描的日志可以用一个 `op=` 值检索出完整链路（扫描、逐页、命名、预览、编辑）。
- 任一页面渲染/旋转/删除/插入失败都能在日志中找到页号、文件、异常。
- 任一阶段的耗时可直接从日志算出，无需再埋点。
- 全部现有测试通过，新增日志解析测试通过。
