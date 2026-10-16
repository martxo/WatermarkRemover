# 软件更新交互体验优化 — 实现计划

## Context（背景与问题）

用户反馈：「软件更新的时候，界面是完全没有任何进度显示的，希望增加友善的、提升交互体验的设计」。

调研结论（已核实）：
1. **直接 bug**：`UpdateProgressWindow`（进度条+百分比+取消按钮）已存在，但在 [UpdateCoordinator.cs](file:///d:\code\src\WatermarkRemover.App\Updates\UpdateCoordinator.cs#L97) 的 `DownloadAndApplyAsync` 里被创建后**从未调用 `Show()`**——下载 122MB 期间用户完全无感知。`finally { if (progressWindow.IsLoaded) Close(); }` 也依赖窗口曾被显示。
2. **进度信息单一**：`UpdateService.DownloadUpdateAsync` 只上报 `IProgress<double>` 百分比，没有速度/已下载字节/来源通道信息；直连失败切换 ghfast.top/ghproxy.net 镜像时用户无感知。
3. **检查阶段无反馈**：`CheckForUpdateAsync` 最长 15 秒网络请求，设置页按钮只是禁用，无"检查中"提示。
4. **校验阶段无反馈**：122MB SHA256 校验（`VerifyIntegrity`）期间无提示。
5. **重启突兀**：下载完直接换文件关程序，无"即将重启"过渡。
6. 失败提示是原始异常文案，不友好。

**目标**：一个阶段化的进度窗口贯穿更新全程（检查中→发现新版→下载中→校验中→即将重启），显示速度/字节/来源通道，检查与校验有明确状态反馈，失败提示友好化。风格沿用应用扁平圆角语言（Accent `#FF4F6EF7`、背景 `#FFF6F7F9`、文字 `#FF6B7280`）。

## 已拍板的交互取舍（按推荐项执行，审批时可改）

- **决策 1（进度载体）：方案 A — 强化现有 `UpdateProgressWindow`**，一个窗口贯穿全部阶段。不建 Toast 体系（工作量翻倍）、不做主界面横幅（归属混乱）。
- **决策 2（重启时机）：本期不做「稍后重启」**——下载+校验完成后显示"即将重启"约 1.2 秒后自动重启（现状行为）。挂起更新分支留二期。
- 其他默认：进度窗口**非模态**（`Show()`，下载期间可继续用主窗口）；检查阶段（仅手动路径）也用该窗口显示"正在检查更新…"。

## 变更清单

### 1. 进度数据富化（UpdateService 层，纯逻辑可测）

**新文件 `src\WatermarkRemover.App\Updates\UpdateProgress.cs`**（record，风格同 `UpdateInfo`）：

```csharp
public enum UpdateStage { Checking, Downloading, Verifying, ReadyToRestart }
public sealed record UpdateProgress(
    UpdateStage Stage, double? Percent, long BytesReceived, long? TotalBytes,
    double? SpeedBytesPerSecond, string? SourceDescription);
```

**`UpdateService.cs`**：
- `DownloadUpdateAsync` 签名 `IProgress<double>` → `IProgress<UpdateProgress>`。
- `DownloadCandidates` 改为产出 `(url, 显示名)`：直连="官方下载"，镜像="备用通道 ghfast.top / ghproxy.net"；候选失败切换时先报一条 `Downloading, Percent=0, SourceDescription="官方下载失败，正在切换备用通道…"`。
- `DownloadToFileAsync`：`Stopwatch` + EMA 速度（α≈0.2）；**节流上报**（≥200ms 或整数百分比变化才 Report，防止 80KB 缓冲每块都 `Dispatcher.Invoke`）；`ContentLength`/`SizeBytes` 均未知时仍上报已下载字节（当前代码此时完全不报进度，是隐藏坑）。
- 进入 `VerifyIntegrity` 前报 `Verifying`；成功返回前报 `ReadyToRestart`。

**新文件或并入 `UpdateProgress.cs`：`ProgressTextFormatter`（纯函数，供单测）**：字节格式化（"45.2 MB / 122.0 MB"）、速度（"3.1 MB/s"）、ETA（"约需 25 秒"），总量未知/速度过低时优雅降级只显示已下载量。

### 2. 进度窗口阶段化改造

**`UpdateProgressWindow.xaml`**（宽 ~440 高 ~200，沿用现有配色）：
- 标题行 `TitleText`：阶段文案（"正在检查更新…" / "正在下载 v1.7.0…" / "正在校验更新包…" / "更新完成，即将重启"）。
- 进度条 `Bar`：下载用确定模式；检查/校验用 `IsIndeterminate="True"`（WPF 自带动画）。
- 新增明细行 `DetailText`："已下载 45.2 MB / 122.0 MB · 3.1 MB/s · 约需 25 秒"。
- 新增来源行 `SourceText`（小字 `#FF6B7280`）："来源：备用通道 ghfast.top"。
- 底部 `PercentText` + 「取消更新」按钮。

**`UpdateProgressWindow.xaml.cs`**：
- `Report(double)` → `Report(UpdateProgress)`（保留 `Dispatcher.Invoke`）。
- 保留 `Closed += Cts.Cancel()` 取消语义；`Verifying` 阶段禁用取消按钮（秒级操作）。

### 3. UpdateCoordinator 编排

**`UpdateCoordinator.cs`**：
- `DownloadAndApplyAsync`：**修 Show() bug**——创建后 `progressWindow.Show()`。
- 检查阶段（仅 `silent: false`）：先 Show 窗口（`Stage=Checking` 不确定进度），检查完成切换内容或关闭；`silent: true` 启动路径保持无感不变。
- 发现新版 MessageBox 文案补充更新包大小（`info.SizeBytes` → "约 122 MB"）与新旧版本号。
- 下载成功后窗口显示 `ReadyToRestart` 约 1.2 秒（`await Task.Delay`）再 `ApplyAndRestart` + `Shutdown()`，消除"窗口一关程序就没了"的突兀感。
- 失败提示友好化：区分网络类失败（"可点击重试或稍后再试"）与全部通道失败（"所有下载通道均不可用，可到发布页手动下载" + `ReleasePageUrl`）。
- 防并发：`static int _running`（`Interlocked`）防止启动检查与手动检查重叠。

**`SettingsWindow.xaml.cs`**：`OnCheckUpdate` 期间按钮文案改"检查中…"`finally` 恢复"检查更新"。

### 4. 测试

- `tests\WatermarkRemover.Tests` 新增 `ProgressTextFormatter` 用例：字节/速度/ETA 格式化、总量未知降级、速度过低降级。
- 现有 `UpdateVersionTests` 不受影响（纯 Core，测试工程不引 App 层；formatter 建议放 Core 或测试工程直接链接该文件——**放 Core\Updates 更干净**，与 `UpdateVersion` 同居）。

## 不做的事（二期）

- 「稍后重启」挂起更新分支、Toast 体系、版本卡片化弹窗、断点续传（Range）。

## 验证

1. `dotnet build` 0 警告；`dotnet test` 全过（基线 147 + 新增）。
2. `dotnet publish -c Release` 过 `VerifyPublishLayout` 闸门。
3. 手动冒烟（需真实/模拟更新）：
   - 手动「检查更新」：窗口出现"正在检查更新…"→ 已是最新/发现新版反馈正确。
   - 模拟下载（可临时指向本地 HTTP 或慢镜像）：进度条+速度+字节+来源通道实时刷新，切换镜像时文案变化。
   - 点 X / 取消：下载停止，残留临时文件清理。
   - 校验阶段显示"正在校验更新包…"；校验失败（可改坏 digest 模拟）提示友好。
   - 下载完成显示"即将重启"约 1.2 秒后程序自动重启换文件。
   - 启动静默检查路径完全不弹窗。
4. 发布：Version 1.8.0 → commit（英文祈使句）→ push → tag v1.8.0 → CI 双资产（exe+zip）。

## 关键文件

- `src\WatermarkRemover.App\Updates\UpdateService.cs`（进度上报改造）
- `src\WatermarkRemover.App\Updates\UpdateProgress.cs`（新，进度模型 + formatter 或 formatter 入 Core）
- `src\WatermarkRemover.App\Updates\UpdateProgressWindow.xaml` / `.xaml.cs`（阶段化 UI）
- `src\WatermarkRemover.App\Updates\UpdateCoordinator.cs`（Show 修复 + 编排）
- `src\WatermarkRemover.App\SettingsWindow.xaml.cs`（按钮状态文案）
- `tests\WatermarkRemover.Tests\**`（formatter 用例）
