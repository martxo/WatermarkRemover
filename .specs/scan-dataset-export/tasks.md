﻿# 任务清单：扫描训练数据导出 + 未裁决页体验修复

- [x] 1. 设置字段与 UI
  - `AppSettings.ScanDatasetExportEnabled`（默认 false）+ `Clone()`
  - `SettingsViewModel.Commit()` 写回
  - `SettingsPage.xaml` 扫描设置段新增开关卡片（默认关闭 / 本地目录 / 环境变量说明）

- [x] 2. PDF `/Rotate` 回读
  - 新增 `src/WatermarkRemover.Pdf/PdfPageRotationReader.cs`
  - 只读 PdfSharp，归一到 0/90/180/270

- [x] 3. 数据集导出器
  - 新增 `src/WatermarkRemover.App/Services/ScanDatasetExporter.cs`
  - 开关判定（设置 ∪ `WMR_DATASET_EXPORT=1`）
  - 会话目录 `Documents\WatermarkRemover-Dataset\<timestamp>`，日志打印一次
  - `SaveCaptureFrame` 灰度 PNG；`Complete` 重排帧、写 `pN-probe.png` / `pN.txt` / `session.txt` / `truth.txt`
  - 全链路异常吞掉，不影响扫描

- [x] 4. 扫描流水线接入
  - `ScanOutcome` 增加 `UndecidedPageIndices`
  - `TimedEncode` 增加 `ProbePng`
  - 采集任务复用同一 `probe` byte；开关打开才保留
  - 写盘成功后调用 `Complete`（truth 来自回读）

- [x] 5. 体验修复：按实际已决页数跳过
  - `MainViewModel.Scan.cs` 改为 `allDecided` 才跳过
  - 摘要文案改为「N 页未能自动摆正，可点「批量摆正」一次改好」
  - `PreviewStripCoordinator.Open` 传递未定页索引与已决页数

- [x] 6. 未定页兜底 + 批量摆正
  - `PdfScanPostProcessor.OrientPagesAsync(pageCount, indices, dpi)`：只探测/旋转未定页，一次写盘
  - `PdfPreviewViewModel`：兜底分流（部分已决 → 定向兜底；全未决 → 整份兜底）
  - `UprightNoticeText` / `HasUprightNotice` / `BatchUprightCommand` / `ApplyBatchUprightAsync`
  - 新增 `BatchUprightDialog.xaml(.cs)`：方向 90/180/270 × 范围整份/仅未定
  - `MainWindow.xaml` rail 新增「批量摆正」按钮 + 提示块

- [x] 7. 测试
  - `ScanDatasetExporterTests`：关闭不落盘；开启每页文件齐全；探针 byte 一致；truth 与写盘 /Rotate 一致；采集序→最终页序重映射
  - `PdfPageRotationReader` 回读用例
  - 全量 `dotnet test` 无回归

- [x] 8. 构建、安装、验证
  - 版本 1.16.88
  - `release.ps1 -Version 1.16.88 -Stage local`
  - 启动验证版本 / 开关默认关闭 / 开关生效

- [x] 9. 中文 Markdown 汇报
  - 版本号、导出目录、开关一句人话、用户操作与交付方式、隐私说明

