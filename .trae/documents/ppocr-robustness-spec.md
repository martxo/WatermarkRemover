# PP-OCR 引擎 + WinRT 修复 + 报错一键复制 — 规格说明

## 背景与结论

另一台电脑 OCR 任务失败，报错「没有注册类 (0x80040154 REGDB_E_CLASSNOTREG)」（截图：`林旗军执照.pdf`，OCR 识别任务）。

**根因结论：**
- `REGDB_E_CLASSNOTREG` = Windows COM/WinRT 类未注册，是 **WinRT 组件缺失/损坏**（典型：Windows N 版、LTSC、精简系统、系统组件被清理），**不是微信 OCR 的问题**（微信引擎报错文案是"微信 OCR 引擎调用失败"等，不会产生这个 HRESULT）。
- **不是每台电脑都会报**。WinRT 组件正常的机器（绝大多数 Win10/11）不受影响。
- 报错来源是两个 WinRT 依赖之一：`WinRtPdfPageRenderer`（`Windows.Data.Pdf`）或 `WindowsMediaOcrEngine`（`Windows.Media.Ocr`）。现有 `OcrFeature` 只保留 `ex.Message`，无法区分来源，所以同时补「一键复制报错详情」以便远程排障。

**修复思路（系统化、可扩展）：**
1. **渲染去 WinRT 化**：`Docnet.Core`（pdfium）实现 `IPdfPageRenderer`，替换 WinRT 渲染器，故障机器也能渲染。
2. **新增 PP-OCR ONNX 引擎**（RapidOcrNet + PP-OCRv5 mobile 中文模型 ≈ 21.5MB，无 v6），给没装微信的用户；引擎选择链可扩展为以后插新引擎只加一个类。
3. **报错一键复制**：`FeatureResult` 携带完整诊断详情，UI 失败行加「复制报错」按钮，一键复制可直接发给开发者调试。

## 变更清单（文件级）

### A. PP-OCR 引擎

| 文件 | 动作 | 内容 |
|---|---|---|
| `src/WatermarkRemover.App/WatermarkRemover.App.csproj` | 改 | 引用 `RapidOcrNet 4.2.0`、`Docnet.Core 2.6.0`；Version → 1.7.0；模型文件以 `None CopyToPublishDirectory` 保留到发布目录（更新机制热替换安全，见 D） |
| `src/WatermarkRemover.App/Models/*.onnx` + `ppocrv5_dict.txt` | 新增（已下载校验） | det 4.6MB + cls 0.97MB + rec 15.86MB + dict 0.07MB ≈ 21.5MB，SHA256 已对 RapidAI 清单校验通过 |
| `src/WatermarkRemover.App/Ocr/PpOcrEngine.cs` | 新增 | 实现 `IOcrEngine`；`DisplayName = "PP-OCR"`；`TryLocate(out engine, out reason)` 静态探测（模型文件齐全 + `RapidOcr.InitModels` 成功）；`RecognizeAsync` 用 `DetectAsync(SKBitmap.Decode(bytes))` 返回 `TextBlocks` 逐行文本；`SemaphoreSlim(1,1)` 串行化；内部临时 SKBitmap 必须 Dispose |
| `src/WatermarkRemover.App/ViewModels/MainViewModel.cs` | 改 | `CreateOcrEngine` 链式选择：微信 → PP-OCR → Windows 原生；每一级构造失败安全降级并 `AppLog.Info` 记录原因。三个 feature 引擎共享 `SharedOcrEngine` 保持不变 |

引擎选择链保持"注册表式"简单链条（3 个候选），不引入新抽象层——新增引擎=新增一个 `IOcrEngine` 实现+链条加一行，符合可扩展要求。

### B. 渲染去 WinRT（bug 修复）

| 文件 | 动作 | 内容 |
|---|---|---|
| `src/WatermarkRemover.App/Ocr/PdfiumPdfPageRenderer.cs` | 新增 | `IPdfPageRenderer`，Docnet（pdfium）实现：`GetDocReader`→按页 `GetImage()` 得 BGRA 字节→SkiaSharp 编码 PNG。分辨率 = 每页 `PageDimensions(scale)`（scale = dpi/72，与 WinRT 实现同一 2400px 边长上限）。`RenderPagesAsync` / `RenderFirstPageAsync` 两个接口成员都实现 |
| `src/WatermarkRemover.App/Ocr/WinRtPdfPageRenderer.cs` | 删除 | 彻底移除 WinRT 依赖（`Windows.Data.Pdf` 是 0x80040154 嫌疑源之一） |
| `src/WatermarkRemover.App/ViewModels/MainViewModel.cs` | 改 | 两处 `new WinRtPdfPageRenderer()` → `new PdfiumPdfPageRenderer()` |
| `src/WatermarkRemover.App/WatermarkRemover.App.csproj` | 改 | `TargetFramework` 保持 `net8.0-windows10.0.19041.0`（`WindowsMediaOcrEngine` 兜底仍需 WinRT 投影；它构造失败只降级不影响主链） |

`WindowsMediaOcrEngine` 保留为最后一级兜底：其构造异常（同样可能 0x80040154）在 `CreateOcrEngine` 里被 catch，不再冒泡到任务执行。

### C. 报错一键复制

| 文件 | 动作 | 内容 |
|---|---|---|
| `src/WatermarkRemover.Core/Features/FeatureResult.cs` | 改 | 新增 `ErrorDetails`（string?）属性；`Fail(inputPath, error, details = null)` 可选参数；新增静态 `Fail(inputPath, error, Exception ex, context?)` 重载：details = 异常 ToString()（类型+HResult+堆栈+内层异常）+ 环境摘要（OS 版本、进程位数、应用版本、时间、输入文件名） |
| `src/WatermarkRemover.Core/Features/Ocr/OcrFeature.cs` | 改 | `catch (Exception ex)` → `FeatureResult.Fail(inputPath, ex.Message, ex)`（保留简短 Message 作行内展示，完整详情进 ErrorDetails） |
| `src/WatermarkRemover.Core/Features/WatermarkFeature.cs` | 改 | `ProcessingResult.Failed` 分支同上带 Exception 详情（WatermarkProcessor 的 catch 已带 Message，此处补 Detail 传递；最小改动：在 WatermarkFeature.Run 的 Failed 分支补 `ErrorDetails` 由 processor 透传——见 tasks.md 第 5 步） |
| `src/WatermarkRemover.Core/Processing/ProcessingResult.cs` | 改 | `Failed(inputPath, error, details = null)` 增加 `ErrorDetails` 属性 |
| `src/WatermarkRemover.Core/Processing/WatermarkProcessor.cs` | 改 | catch 里 `Failed(inputPath, ex.Message, ex.ToString())` |
| `src/WatermarkRemover.App/ViewModels/FileJobViewModel.cs` | 改 | `Apply` 失败分支保存 `ErrorDetails`；新增 `CopyErrorCommand`（RelayCommand，仅 Failed 且有详情时可用）：把详情文本放 `Clipboard`，成功后 Detail 临时显示「报错信息已复制」（3 秒后恢复）或直接 MessageBox 提示——采用**Clipboard.SetText + Detail 前缀提示**（轻量、无弹窗打断） |
| `src/WatermarkRemover.App/MainWindow.xaml` | 改 | 失败行（`State == Failed` 时可见）在删除按钮左侧加「复制报错」小按钮（`SmallButton` 风格），ToolTip="复制完整报错信息用于调试"，绑定 `CopyErrorCommand` |
| `src/WatermarkRemover.App/ViewModels/MainViewModel.cs` | 改 | `ProcessOneAsync` 的意外异常分支 `Fail(..., ex)` 带详情 |

一键复制文本格式（供用户直接粘贴给开发者）：
```
应用版本：v1.7.0
系统：Microsoft Windows 10.0.19041 ...
进程：64 位
文件：林旗军执照.pdf
功能：OCR 识别
时间：2026-10-16 08:13:08
错误：没有注册类 (0x80040154 REGDB_E_CLASSNOTREG)
---- 异常详情 ----
System.TypeInitializationException: ...
   at ...
```

### D. 分发与更新兼容

- 模型文件（~21.5MB）**随发布目录分发**（`CopyToPublishDirectory=PreserveNewest`），不打进单文件 exe——与 `wcocr.dll` 同策略。
- 更新机制冲突点：`UpdateApplier` 用 `move /y` 只替换 exe。**models\ 与 pdfium 相关原生库不在 exe 内时**，旧版升级后缺模型 → PP-OCR 引擎 `TryLocate` 返回 false 自动降级到微信/Windows 引擎，不崩溃（降级路径已在 A 中保证）。
- **决策**：v1.7.0 发布物为 **zip 包**（exe + Models\），Release 上传 `WatermarkRemover.zip` + `WatermarkRemover.exe` 双资产；`UpdateConfig.AssetName` 改为 `WatermarkRemover.zip`，`UpdateApplier` 解压 zip 覆盖（exe+Models）而非仅 move exe。旧版本（只认 exe 资产）升级到 v1.7.0 仍走 exe 资产，之后的版本走 zip——Release 同时保留两个资产即兼容两个时代的客户端。
- Docnet 的 `pdfium.dll` 由 NuGet `runtimes\win-x64\native` 自动随单文件打包（`IncludeNativeLibrariesForSelfExtract`），无需手工放置；SkiaSharp 原生库同理。
- CI（`.github/workflows/release.yml`）：publish 后压缩 `publish/`（含 exe+Models）为 `WatermarkRemover.zip`，Release 上传 zip 与 exe 两个资产。

### E. 验证步骤

1. `dotnet build` 无警告错误；`dotnet test` 全部通过（基线 141 + 新增用例）。
2. 新增 Core 测试：`FeatureResult` 详情格式（含 HResult/堆栈/环境段）；`ProcessingResult.Failed` 透传 details。
3. 冒烟（本机）：真样张 PDF 跑「OCR 识别」出文本；跑「去水印」自动命名仍产出「林孝明执照.pdf」；人为让引擎抛错（如临时改坏模型路径）验证失败行「复制报错」按钮产出完整文本。
4. `dotnet publish -c Release` 后确认 `publish/` 含 exe + `Models\`4 个文件；运行 exe 验证 PP-OCR 引擎被选中（无微信机器场景）与微信优先场景。
5. 发布：版本 1.7.0 → commit → push → tag `v1.7.0` → CI 出 exe+zip。

## 不做的事

- 不用 PP-OCRv6 / 二期增强包（用户明确排除）。
- 不改 `IOcrEngine` 接口形状（只加实现），不重构 Feature/队列架构。
- 不为 Windows 原生 OCR 删除 WinRT 投影（仅兜底，构造失败已隔离）。
- 不做 GPU 推理（CPU 默认，`GetDefaultSessionOptions` 默认即可）。
