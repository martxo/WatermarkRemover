# 任务清单：上传位置交互优化（v1.16.126）

## T1 记住最近上传目录
- [x] `NetdiskViewModel`：拆 `UploadFilesAsync(string[])` 核心；新增 `rememberUploadLocation` 回调 + `BuildRemotePath`
- [x] `NetdiskCoordinator`：`RememberUploadLocation` 持久化 + 接线到管理窗口

## T2 目录选择器
- [x] 新建 `NetdiskFolderPickerWindow.xaml(.cs)`（自动连接、存储盘切换、进入/上一级、使用此目录）
- [x] `NetdiskCoordinator.BrowseForUploadFolder()`
- [x] `SettingsViewModel` 参数 + `BrowseUploadFolderCommand`；`MainViewModel.Settings.cs` 接线
- [x] `SettingsPage.xaml` 加「浏览…」按钮与说明文字

## T3 测试
- [x] 新增 `UploadLocationMemoryTests.cs`（路径拼接 / 上传后记忆 / 不上传不记忆，共 6 条）
- [x] 全量 dotnet test 通过（1043/1043）

## T4 发布
- [x] 版本 → 1.16.126；local 装机验证（日志 应用启动 v1.16.126）；cloud 推送校验（远端 18 文件 SHA256 全对）；github 完整包（非 draft、清单仅 1.16.126、delta 738KB、版本戳已推送）
