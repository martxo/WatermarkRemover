# 更新链路修复：70MB 包下载失败 / 感叹号点击无效 / 云更新窗口打不开

## 现象（另一台电脑，v1.16.106）

1. 增量更新下载 70 多 MB 的包失败；历史上只有十几 MB 以下的包成功过。
2. 标题栏右侧出现红色感叹号（`UpdateReady.State = Failed`），点击没有反应。
3. 设置 → 软件更新 →「检查更新」无效；打开「云更新中心」弹框：
   `无法对 WatermarkRemover.App.ViewModels.CloudUpdateViewModel 类型的只读属性 ProgressPercent 进行 TwoWay 或 OneWayToSource 绑定。`

## 根因（已取证）

| # | 位置 | 问题 |
|---|------|------|
| 1 | [MirrorFileDownloader.cs](file:///d:/code/src/WatermarkRemover.App/Updates/MirrorFileDownloader.cs) `DownloadFile` → `DownloadToFileAsync` | 把 Velopack `IFileDownloader.DownloadFile` 的 `timeout` 参数当成**秒**：`CancelAfter(TimeSpan.FromSeconds(timeout))`。Velopack 官方 XML 注释明确为 **minutes**（"The maximum time in minutes"），默认 30。结果：**任何超过 30 秒的下载必被取消**。十几 MB 的增量包能在 30 秒内下完所以正常，70 MB 全量包必失败。这就是"只有小包能更新"的直接原因。 |
| 2 | [UpdateCoordinator.cs](file:///d:/code/src/WatermarkRemover.App/Updates/UpdateCoordinator.cs#L142-L153) `RunCheckCoreAsync` 首分支 | `if (UpdateReadyNotifier.Current.HasUpdate)` —— `Failed` 状态也满足 `HasUpdate`。点击感叹号 → `RetryAsync` → 该分支走 silent 路径，只写日志「已下载待重启，跳过启动检查」后 return。按钮有焦点但没有任何网络动作，表现为"点击没反应"。 |
| 3 | [CloudUpdateWindow.xaml](file:///d:/code/src/WatermarkRemover.App/CloudUpdateWindow.xaml#L74-L78) | `ProgressBar.Value="{Binding ProgressPercent}"` 未指定 `Mode=OneWay`。`RangeBase.Value` 的默认绑定模式是 TwoWay，而 VM 属性只有私有 setter，XAML 加载即抛 `InvalidOperationException`，被全局异常处理弹框。这就是截图中的错误，等于云更新中心完全不可用。 |
| 4 | [CloudUpdateService.cs](file:///d:/code/src/WatermarkRemover.App/Updates/CloudUpdateService.cs#L518-L521) | 每个分块套了 45 秒**总**超时。慢网下 1 MB 分块超过 45 秒即被判定失败，转去 CLI 兜底（每块起一个 `aliyunpan` 进程），既慢又掩盖真实进度。`AliyunDriveSession.DownloadAsync` 本身已有 30 秒停滞检测 + 90 秒取链超时，外层 45 秒是多余且有害的。 |

## 断点续传现状

- 阿里云盘通道：**有**。1 MB 内容分块，缓存在 `ChunkCacheDirectory`，重启后已下载分块直接复用；但单块内部失败即丢弃 `.part`。
- GitHub/Velopack 通道：**没有**。`MirrorFileDownloader` 每次 `FileMode.Create` 从头写，切换镜像/重试都重头下载；`UpdateService.DownloadUpdateAsync` 失败即删临时文件。
- 结论：不需要换网盘。慢与失败的主因是第 1 条的 30 秒总超时和缺续传，不是阿里云盘限速。

## 改造方案（本次范围）

### P1 下载不再被 30 秒杀死 + 支持 Range 续传
文件：`src/WatermarkRemover.App/Updates/MirrorFileDownloader.cs`

- `DownloadFile`：`timeout` 按 **分钟** 使用，整体上限 `CancelAfter(TimeSpan.FromMinutes(max(1, timeout)))`。
- 新增 60 秒「无数据停滞」判定（逐次读），代替总时长判定；只要还在出数据就继续。
- 续传：下载写入 `targetFile + ".part"`；若 `.part` 已存在，请求头加 `Range: bytes={已有长度}-`：
  - 响应 206 → 追加写；
  - 响应 200（服务端忽略 Range）→ 从头写，不拼接；
  - 响应 416（残留比远端还长/失效）→ 删除 `.part`，重试一次；
  - 成功 → `File.Move(.part → targetFile, overwrite)`；失败/取消 → 保留 `.part` 供下次续传。
- 进度按 `已有字节 + 本次 ContentLength` 计算，续传时从对应百分比继续。

### P2 感叹号点击真正重试
文件：`src/WatermarkRemover.App/Updates/UpdateCoordinator.cs`

- 首分支短路条件改为 `State == UpdateVisualState.Ready`（并带 `Apply`），Failed/Downloading 不再被误判为"已下载待重启"。
- `RetryAsync`：若当前为 `Failed`，先 `UpdateReadyNotifier.Current.Clear()` 复位，再走 `RunCheckAsync(silent: true)`；结果仍由图标反馈（下载中/失败/待重启）。

### P3 云更新中心可打开
文件：`src/WatermarkRemover.App/CloudUpdateWindow.xaml`

- `Value="{Binding ProgressPercent, Mode=OneWay}"`。

### P4 云分块不再被 45 秒误杀
文件：`src/WatermarkRemover.App/Updates/CloudUpdateService.cs`

- 删除分块的 45 秒 `CancelAfter`，改用调用方 `cancellationToken`（`DownloadAsync` 内部已有 30 秒停滞 / 90 秒取链保护）。

### P5 版本与发布
- `src/WatermarkRemover.App/WatermarkRemover.App.csproj`：`1.16.124` → `1.16.125`。
- 发布：`release.ps1 -Version 1.16.125 -Stage local` 后启动验证版本号；再 `-Stage cloud` 推送并校验远端 `latest.json` 各文件 SHA256；再 `-Stage github` 推完整包。**不做便携版**。

## 不做（明确排除）

- 不换网盘/不接第三方对象存储：云盘分块续传本身可用，故障在 GitHub 通道。
- 不并行下载云分块：避免触发平台限流，先用确定性修复验证效果。
- 不改 Velopack 官方包管理逻辑，不改安装/回滚流程。
- 不做便携版。

## 验证

1. 新增 `tests/.../App/MirrorDownloadResilienceTests.cs`（`HttpMessageHandler` 假件）：
   - `timeout` 以分钟计：`timeout = 0.02`（1.2 秒）、响应延迟 300 ms，仍成功；若误当秒用会立刻取消。
   - 中途断流后切换候选：第二次请求带 `Range`，206 只返回剩余字节，最终文件与源完整一致。
   - 服务端忽略 Range 返回 200：从头重写，结果不重复拼接。
   - 残留导致 416：删除残片并重来一次后成功。
2. `dotnet test` 全量必须全绿（当前基线 1033 条）。
3. Release 构建 → local 段装机 → 日志出现 `应用启动 v1.16.125`。
4. 云盘：远端 `latest.json` 的文件数与 SHA256 与本地构建一致。
5. GitHub：完整包（Setup/zip/nupkg + releases.win.json）发布成功，不含便携版。

## 风险

- 若某镜像不支持 Range，自动退化为从头下载（已有 200 分支），不会产生拼接损坏。
- `.part` 会留在包缓存目录，成功后即被 Move；失败时保留是续传的前提，不影响 Velopack 校验。
- Velopack 若在某版本把 `timeout` 改成秒，30 分钟上限只会更宽松，不会误杀。

## 待确认

按上述 P1–P5 实施，发布顺序 local → cloud → github（无便携版）。
