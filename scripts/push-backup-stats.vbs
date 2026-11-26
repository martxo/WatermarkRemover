' 备份统计推送的隐藏启动器（计划任务调用）
' 计划任务若直接执行 powershell.exe，Task Scheduler 会先在当前桌面会话创建控制台窗口，
' 每 10 分钟闪一次黑框。wscript.exe 属 GUI 子系统、本身没有控制台，WScript.Shell.Run
' 的第二个参数 0 表示隐藏目标程序窗口，第三个参数 False 表示不等待退出。
Dim fso, shell, scriptDir, ps1
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
ps1 = fso.BuildPath(scriptDir, "push-backup-stats.ps1")
shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & ps1 & """", 0, False
