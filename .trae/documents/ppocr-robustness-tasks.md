# 任务分解 — PP-OCR 引擎 + WinRT 修复 + 报错一键复制

规格见 `ppocr-robustness-spec.md`。按依赖顺序执行。

## 1. 渲染去 WinRT（bug 修复，独立可测）
- [x] 新增 `src/WatermarkRemover.App/Ocr/PdfiumPdfPageRenderer.cs`：Docnet `GetDocReader(path, PageDimensions(scale))` → 逐页 `GetImage()`（BGRA）→ SkiaSharp `SKBitmap` → `Encode(Png)`；实现 `RenderPagesAsync` 与 `RenderFirstPageAsync`；每页边长上限 2400px（与原 WinRT 版一致）；异常带文件名上下文。
- [x] 删除 `src/WatermarkRemover.App/Ocr/WinRtPdfPageRenderer.cs`。
- [x] `MainViewModel.cs` 两处 `new WinRtPdfPageRenderer()` → `new PdfiumPdfPageRenderer()`。
- [x] csproj 添加 `Docnet.Core 2.6.0` + `SkiaSharp`。
- 验证：build 通过；本机冒烟渲染出图（2.6MB PNG）。

## 2. PP-OCR 引擎
- [x] csproj 添加 `RapidOcrNet 4.2.0`；`Models\` 4 文件随发布目录分发（`AppAssets` 统一解析路径）。
- [x] 新增 `src/WatermarkRemover.App/Ocr/PpOcrEngine.cs`（TryLocate + InitModels + DetectAsync + 串行化）。
- [x] `MainViewModel.CreateOcrEngine`：微信 → PP-OCR → Windows 原生（构造失败安全降级 + `UnavailableOcrEngine` 终端兜底）。
- 验证：冒烟确认 PP-OCR 识别真样张 25 行中文（~1.2s），命名「林孝明执照」。

## 3. 报错一键复制
- [x] `FeatureResult.cs`：+`ErrorDetails`、+`Fail(..., Exception)` 重载、+`FailWithDetails`。
- [x] `ProcessingResult.cs`：`Failed(..., errorDetails)` + `ErrorDetails`。
- [x] `WatermarkProcessor.cs` catch → `Failed(..., ex.ToString())`。
- [x] `OcrFeature.cs` catch → `Fail(..., ex, DisplayName)`。
- [x] `WatermarkFeature.cs` Failed 分支 `FailWithDetails` 透传。
- [x] `FileJobViewModel.cs`：+`CopyErrorCommand` + `ShowCopyError`（Clipboard 完整诊断文本：版本/系统/位数/文件/功能/时间/错误/异常详情/OCR 引擎链）。
- [x] `MainWindow.xaml` 失败行加「复制报错」按钮（`RowCopyErrorButton` 样式，BoolToVis）。
- [x] `OcrDiagnostics.cs`：引擎链一次性探测写入报错报告（微信/PP-OCR/Windows OCR/WinRT PDF 组件）。
- [x] `MainViewModel.ProcessOneAsync` 意外异常分支带 ex 详情。
- 验证：6 个新 Core 测试断言 HResult/堆栈/内层异常保留。

## 4. 发布链（zip 资产 + 更新兼容）
- [x] `UpdateConfig.AssetName` → `WatermarkRemover.zip`；`UpdateApplier` 解压 staging → 覆盖 exe + Models；`UpdateService` 下载扩展名跟随资产。
- [x] `.github/workflows/release.yml`：压缩 zip，Release 上传 zip + exe 双资产。
- [x] csproj `Version` → 1.7.0。
- 验证：zip 解包根目录含 exe + Models\，与 UpdateApplier 假设一致。

## 4b. 部署布局契约（架构修复，杜绝单文件陷阱）
- [x] `AppAssets.cs`：旁挂资源路径唯一解析入口（新增功能只在此注册）。
- [x] csproj `SidecarAsset` 清单（5 项）+ `VerifyPublishLayout` 发布闸门（缺资源/开发文件泄露 → 构建期报错）。
- [x] 全部旁挂资源补 `ExcludeFromSingleFile="true"`（闸门实测抓到 wcocr.dll 也被吞进 exe，已修）。
- [x] 清理 ONNX Runtime `*.lib` 开发文件 + RapidOcrNet latin 模型包（`Models\v5`）。
- 验证：publish 闸门通过「5 个旁挂资源就位，无开发文件泄露」；exe 109.6MB（模型未被打包）。

## 5. 测试 + 发布
- [x] `dotnet test` 147 全过（基线 141 + 新增 6）。
- [x] 真样张冒烟：PP-OCR 识别 + 自动命名「林孝明执照」正常。
- [x] commit（英文祈使句）→ push → tag `v1.7.0`（CI 构建成功，Release 双资产已上传：exe 109.5MB + zip 122.3MB）。
- [x] 更新 `project_memory.md`：REGDB_E_CLASSNOTREG 根因、PP-OCR 引擎注册模式、部署布局契约、zip 更新资产策略。
