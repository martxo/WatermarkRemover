# 任务清单：更新链路修复（v1.16.125）

## T1 修复下载总超时与续传（P1）
- [x] 改 `src/WatermarkRemover.App/Updates/MirrorFileDownloader.cs`：
  - `DownloadFile` 的 `timeout` 按分钟使用（整体上限），逐次读加 60 秒停滞判定
  - 写入 `targetFile + ".part"`，存在则 `Range: bytes=N-` 续传；206 追加、200 重写、416 清残片重试一次
  - 成功 Move 覆盖目标；失败/取消保留 `.part`
  - 进度按续传后的总字节计算
- 验收：70MB 级下载不再 30 秒被取消；断流后下次请求带 Range。

## T2 感叹号点击重试（P2）
- [x] `src/WatermarkRemover.App/Updates/UpdateCoordinator.cs`
  - `RunCheckCoreAsync` 仅在 `State == Ready` 时短路
  - `RetryAsync`：Failed 先 `Clear()` 再检查
- 验收：Failed 状态下点击图标会重新发起检查/下载，图标转为 Downloading 或恢复正常。

## T3 云更新窗口绑定（P3）
- [x] `src/WatermarkRemover.App/CloudUpdateWindow.xaml` 的 `ProgressBar.Value` 加 `Mode=OneWay`
- 验收：打开云更新中心不再抛 `ProgressPercent` 绑定异常。

## T4 云分块超时（P4）
- [x] `src/WatermarkRemover.App/Updates/CloudUpdateService.cs` 删除分块 45 秒总超时
- 验收：慢速分块不再被误判失败转 CLI；仍受 30 秒停滞 / 90 秒取链保护。

## T5 测试
- [x] 新增 `tests/WatermarkRemover.Tests/App/MirrorDownloadResilienceTests.cs`（4 条：分钟语义 / Range 续传 / 忽略 Range / 416 重来）
- [x] `dotnet test` 全量通过（1037/1037）

## T6 版本与发布
- [x] `WatermarkRemover.App.csproj` 版本 → `1.16.125`
- [x] `release.ps1 -Version 1.16.125 -Stage local`，启动并确认日志 `应用启动 v1.16.125`
- [x] `-Stage cloud` 推送，校验远端 `latest.json` 文件数与 SHA256
- [x] `-Stage github` 推完整包（不做便携版）

## T7 汇报
- [x] 给出根因证据链、改动点、测试数、发布校验结果、以及"是否需要换网盘"的结论
