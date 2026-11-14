# 上传位置交互优化：目录选择器 + 记住最近上传目录

## 用户痛点

1. 设置 → 网盘设置 →「快速上传位置」的存储盘/目录只能手动输入路径文字，麻烦且易错。
2. 在网盘管理窗口里浏览到某目录上传过文件，这个位置不会被记住；下次任务行「上传到网盘」仍按设置里的旧目录上传。

## 方案（两条）

### A. 记住最近上传目录（自动默认，零点击）
- [NetdiskViewModel.UploadAsync](file:///d:/code/src/WatermarkRemover.App/ViewModels/NetdiskViewModel.cs) 上传成功后，把当前浏览位置（存储盘名 + `/a/b` 路径）通过新回调 `rememberUploadLocation` 报告出去。
  - 路径由面包屑栈计算：根目录 = 空串（沿用"留空=根目录"约定）；新增 `internal static string BuildRemotePath(...)` 便于测试。
  - 为可测试性，把上传核心从弹 OpenFileDialog 的入口拆出：`UploadAsync()`（弹窗）→ `UploadFilesAsync(string[])`（核心，internal）。
- [NetdiskCoordinator](file:///d:/code/src/WatermarkRemover.App/Services/NetdiskCoordinator.cs) 接收回调 → 写 `_settings.NetdiskUploadDrive / NetdiskUploadDirectory` 并 `SettingsService.Save`（复用 `RememberSelection` 的直写模式，崩溃不丢）。
- 效果：在网盘窗口哪个目录上传过，之后任务行/快速上传默认就传到那个目录。设置页下次打开能看到新值。

### B. 设置页「浏览…」云盘目录选择器
- 新建 `NetdiskFolderPickerWindow`（样式对齐 NetdiskMoveDialog）：
  - 打开自动连接账号（预选上次用的账号）；多盘产品显示存储盘下拉可切换。
  - 文件夹列表双击进入、「上一级」返回、当前路径文字实时显示。
  - 「使用此目录」返回 (DriveName, RemotePath)；「取消」返回 null。
  - 静态入口 `Prompt(owner, catalog, accounts, lastProviderId, lastAccountName, trace)`，会话随窗释放。
- [NetdiskCoordinator](file:///d:/code/src/WatermarkRemover.App/Services/NetdiskCoordinator.cs) 新增 `BrowseForUploadFolderAsync()`：无账号先提示；有则开窗。
- [SettingsViewModel](file:///d:/code/src/WatermarkRemover.App/ViewModels/SettingsViewModel.cs) 新增可选参数 `browseUploadFolder` + `BrowseUploadFolderCommand`，选中后回填存储盘/目录两个输入框（仍可手动改，走「保存」生效）。
- SettingsPage.xaml「目录」旁加「浏览…」按钮；说明文字改为"可点「浏览…」在网盘里选；在网盘窗口上传过会自动记住"。

## 不做（本轮范围外）

- 「移动到」对话框（NetdiskMoveDialog）仍为手动输入：它发生在已连接会话内部，改造需要把会话传进对话框，另起一轮。
- 不改上传账号选择逻辑（仍是第一个配置的账号）。

## 测试

新增 `tests/WatermarkRemover.Tests/App/UploadLocationMemoryTests.cs`（复用 DingTalkDriveTests 的 StubProvider 模式）：
1. `BuildRemotePath`：根目录=空串；一层 `/上传`；两层 `/上传/2026`。
2. 上传成功后回调携带 (存储盘名, 当前目录路径)：进入「上传」文件夹再传 → `("默认盘", "/上传")`。
3. 弹窗取消不上传也不记忆（OpenFileDialog 路径已拆出，直接测核心方法不上报）。

全量 `dotnet test` 必须全绿（当前基线 1037）。

## 发布

- 版本 1.16.125 → 1.16.126；`release.ps1 -Version 1.16.126 -Stage local` 装本机验证启动日志；`-Stage cloud` 推送并校验 latest.json SHA256；`-Stage github` 发完整包。不做便携版。
