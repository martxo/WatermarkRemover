# WatermarkRemover 备份统计推送脚本（客户端）
# 读取 automatic-backup/state.json + 当前监控目录（默认桌面），按活跃桶聚合后 POST 到腾讯云统计端点。
# 由计划任务每 10 分钟调用；成功静默，失败覆盖记录到最后错误文件。
# 服务器端配套：scripts/backup-stats-server/server.py（systemd: wm-backup-stats）
$ErrorActionPreference = 'Stop'

$PushUrl   = 'http://106.52.57.36/backup-stats/push'
$PushToken = 'wmbs-c850bb13004d426667a02d65bf824e0e'
$StatePath = Join-Path $env:APPDATA 'WatermarkRemover\automatic-backup\state.json'
$SettingsPath = Join-Path $env:APPDATA 'WatermarkRemover\WatermarkRemover.settings.json'
$ErrLog    = Join-Path $env:APPDATA 'WatermarkRemover\automatic-backup\stats-push.log'

# 与 AutomaticBackupFileFilter 保持一致：这些扩展名才参与自动备份。
$AllowedExtensions = @('.pdf', '.jpg', '.jpeg', '.png', '.gif', '.bmp',
    '.doc', '.docx', '.xls', '.xlsx', '.ppt', '.pptx', '.zip', '.rar')
$MaxFiles = 2000

function Fail($msg) {
    try { Set-Content -Path $ErrLog -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $msg) -Encoding UTF8 } catch {}
    exit 1
}

try {
    if (-not (Test-Path $StatePath)) { exit 0 }   # 没备份过，无需推送
    $raw = Get-Content $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
} catch { Fail "read state.json: $_" }

# 键 = "<指纹>\n<相对路径>"，按桶（目标指纹）分组
$buckets = @{}
foreach ($p in $raw.PSObject.Properties) {
    $nl = $p.Name.IndexOf("`n")
    if ($nl -lt 0) { continue }
    $fp = $p.Name.Substring(0, $nl)
    $rel = $p.Name.Substring($nl + 1)
    if (-not $buckets.ContainsKey($fp)) { $buckets[$fp] = New-Object System.Collections.ArrayList }
    [void]$buckets[$fp].Add(@{ path = $rel; value = $p.Value })
}
if ($buckets.Count -eq 0) { exit 0 }

# 活跃桶 = 全局最新 lastSuccessUtc 所在的桶（最近真正在跑备份的目标）
$activeFp = $null
$activeMax = [datetimeoffset]::MinValue
foreach ($fp in $buckets.Keys) {
    foreach ($e in $buckets[$fp]) {
        $ts = $e.value.lastSuccessUtc
        if ($ts) {
            $t = [datetimeoffset]::Parse($ts)
            if ($t -gt $activeMax) { $activeMax = $t; $activeFp = $fp }
        }
    }
}
if (-not $activeFp) { exit 0 }   # 从未成功过，无统计意义

# 当前配置的目标位置与监控目录（自动备份默认就是桌面）
$settings = $null
try {
    if (Test-Path $SettingsPath) {
        $settings = Get-Content $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
} catch {}

$target = ''
if ($settings) {
    $drive = [string]$settings.NetdiskUploadDrive
    $dir = [string]$settings.NetdiskUploadDirectory
    if ($drive) { $target = $drive + $dir }
}

$root = ''
if ($settings) { $root = [string]$settings.AutomaticBackupRootDirectory }
if (-not $root) { $root = [Environment]::GetFolderPath('Desktop') }
$root = $root.TrimEnd('\')
$rootError = ''
if (-not (Test-Path -LiteralPath $root)) { $rootError = "监控目录不存在：$root" }

# 活跃桶的状态表：相对路径 → { length, lastWriteUtc, lastSuccessUtc }
$stateMap = @{}
foreach ($e in $buckets[$activeFp]) { $stateMap[$e.path] = $e.value }

# 桌面当前有什么：列出会备份的文件，标记是否与已上传的记录完全一致
$desktop = New-Object System.Collections.ArrayList
$unsupported = 0
if (-not $rootError) {
    $stack = New-Object System.Collections.Stack
    $stack.Push([IO.DirectoryInfo]::new($root))
    while ($stack.Count -gt 0 -and $desktop.Count -lt $MaxFiles) {
        $dir = $stack.Pop()
        try { $items = $dir.GetFileSystemInfos() } catch { continue }
        foreach ($item in $items) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            if ($item -is [IO.DirectoryInfo]) { $stack.Push($item); continue }
            $name = $item.Name
            $ext = $item.Extension.ToLowerInvariant()
            if ($name.StartsWith('~$') -or $name.StartsWith('._') -or ($AllowedExtensions -notcontains $ext)) {
                # 快捷方式、程序、临时文件等：不参与备份，只报个数（隐藏的系统文件如 desktop.ini 不吓人）
                if (($item.Attributes -band ([IO.FileAttributes]::Hidden -bor [IO.FileAttributes]::System)) -eq 0) {
                    $unsupported++
                }
                continue
            }

            $rel = $item.FullName.Substring($root.Length + 1).Replace('\', '/')
            $length = [int64]$item.Length
            $write = [datetimeoffset]::new($item.LastWriteTimeUtc)
            $uploaded = $false
            $lastSuccess = $null
            $state = $stateMap[$rel]
            if ($state) {
                try {
                    $stateWrite = [datetimeoffset]::Parse([string]$state.lastWriteUtc)
                    if ([int64]$state.length -eq $length -and
                        $stateWrite.UtcDateTime.Ticks -eq $write.UtcDateTime.Ticks) {
                        $uploaded = $true
                        $lastSuccess = [string]$state.lastSuccessUtc
                    }
                } catch {}
            }

            [void]$desktop.Add(@{
                path           = $rel
                length         = $length
                lastWriteUtc   = $write.ToString('o')
                uploaded       = $uploaded
                lastSuccessUtc = $lastSuccess
            })
        }
    }
}

# 明细按最后成功时间倒序（旧版页面仍可读）
$files = @()
foreach ($e in $buckets[$activeFp]) {
    if (-not $e.value.lastSuccessUtc) { continue }
    $files += ,@{
        path           = $e.path
        length         = [int64]$e.value.length
        lastSuccessUtc = $e.value.lastSuccessUtc
    }
}

# 每日成功数（最近 14 天，本地日）：每个文件最后一次成功上传落在哪一天
$dayCount = @{}
foreach ($e in $buckets[$activeFp]) {
    if (-not $e.value.lastSuccessUtc) { continue }
    try {
        $d = ([datetimeoffset]::Parse([string]$e.value.lastSuccessUtc)).ToLocalTime().ToString('yyyy-MM-dd')
    } catch { continue }
    if ($dayCount.ContainsKey($d)) { $dayCount[$d]++ } else { $dayCount[$d] = 1 }
}
$days = @()
for ($i = 13; $i -ge 0; $i--) {
    $d = (Get-Date).Date.AddDays(-$i).ToString('yyyy-MM-dd')
    $days += ,@{ date = $d; success = [int]($dayCount[$d]) }
}

# 设备信息：计算机名 / 系统版本 / 登录用户
$osName = ''
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
    if ($os) { $osName = $os.Caption + ' ' + $os.Version }
} catch {}

# 公网 IP 与归属地（失败不影响推送）
$publicIp = ''
$ipLocation = ''
try {
    $resp = Invoke-WebRequest -Uri 'https://myip.ipip.net' -TimeoutSec 6 -UseBasicParsing
    $txt = [System.Text.Encoding]::UTF8.GetString($resp.RawContentStream.ToArray())
    if ($txt -match 'IP[：:]\s*([0-9a-fA-F\.:]+)') { $publicIp = $Matches[1] }
    if ($txt -match '来自于[：:]\s*(.+)$') { $ipLocation = $Matches[1].Trim() }
} catch {}

$doc = @{
    machine          = $env:COMPUTERNAME
    device           = @{
        name = $env:COMPUTERNAME
        os   = $osName
        user = $env:USERNAME
    }
    publicIp         = $publicIp
    ipLocation       = $ipLocation
    generatedUtc     = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    bucket           = $activeFp.Substring(0, [Math]::Min(12, $activeFp.Length))
    target           = $target
    root             = $root
    rootError        = $rootError
    unsupportedCount = $unsupported
    desktop          = $desktop
    days             = $days
    files            = $files
}
$json = $doc | ConvertTo-Json -Depth 5 -Compress
$bytes = [System.Text.Encoding]::UTF8.GetBytes($json)

try {
    Invoke-RestMethod -Method Post -Uri $PushUrl `
        -ContentType 'application/json; charset=utf-8' `
        -Headers @{ 'X-Push-Token' = $PushToken } `
        -Body $bytes -TimeoutSec 20 | Out-Null
    if (Test-Path $ErrLog) { Remove-Item $ErrLog -ErrorAction SilentlyContinue }
} catch { Fail "push: $_" }
