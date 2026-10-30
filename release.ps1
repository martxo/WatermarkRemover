# Release pipeline for WatermarkRemover — four parameterised stages.
#
#   powershell -ExecutionPolicy Bypass -File d:\code\release.ps1 -Version 1.16.51 -Stage local
#   powershell -ExecutionPolicy Bypass -File d:\code\release.ps1 -Version 1.16.51 -Stage cloud
#   powershell -ExecutionPolicy Bypass -File d:\code\release.ps1 -Version 1.16.51 -Stage portable
#   powershell -ExecutionPolicy Bypass -File d:\code\release.ps1 -Version 1.16.51 -Stage github
#   powershell -ExecutionPolicy Bypass -File d:\code\release.ps1 -Version 1.16.51        # = -Stage all
#
# Stages, in the order they are expected to run:
#   local  — stamp the csproj, dotnet publish, vpk pack, silent-install on this machine, then
#            verify every installed file matches the published payload (a Setup that was cut
#            off halfway used to leave a truncated WatermarkRemover.exe behind, which then
#            failed to start — the byte-size check catches that before anyone double-clicks).
#   cloud  — incremental publish to 阿里云盘. If the repository API download times out (seen
#            repeatedly on this network), the manifests are prefetched with the local aliyunpan
#            CLI and the publisher is retried with --latest-file/--versions-file --no-gc.
#   portable — zip the published payload into the green (unzip-and-run) build and upload it to
#            阿里云盘 /WatermarkRemover/portable/. A green copy carries no Update.exe, so the
#            app stores settings/journal beside the exe instead of %APPDATA% — testers can run
#            a new build next to an installed release without either touching the other.
#   github — upload to GitHub Releases, verify the remote current-only feed, commit the version
#            stamp and push.
#
# Why history is NOT re-uploaded: Velopack's GithubSource.GetReleaseFeed (checked against the
# 1.2.161 assembly) lists the latest 10 GitHub releases, downloads each release's own
# releases.win.json and concatenates its assets. A release therefore only needs its own full
# + delta package; the client finds the deltas of previous versions on their own tags.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    # local | cloud | portable | github | all — all runs local → cloud → portable → github.
    [ValidateSet('local', 'cloud', 'portable', 'github', 'all')]
    [string]$Stage = 'all',
    # Release note shown in the 云更新版本列表 (defaults to the version tag).
    [string]$Note = '',
    # local stage only: pack but do not install on this machine.
    [switch]$NoInstall,
    # local stage only: re-publish and re-pack even when this version's package already exists.
    [switch]$Rebuild,
    # Code signing. An unsigned installer is blocked by SmartScreen and can be replaced in the
    # update channel, so a shipped release must be signed. Supply ONE of:
    #   -SignPfx <path> -SignPassword <pwd>      → signtool with a .pfx file
    #   -SignThumbprint <sha1 thumbprint>        → a certificate already in CurrentUser\My
    #   -AzureSignMetadata <metadata.json>       → Azure Trusted Signing
    # CI can set WM_SIGN_PFX / WM_SIGN_PASSWORD / WM_SIGN_THUMBPRINT / WM_AZURE_SIGN_FILE.
    [string]$SignPfx = '',
    [string]$SignPassword = '',
    [string]$SignThumbprint = '',
    [string]$AzureSignMetadata = '',
    # Fail instead of silently producing an unsigned installer (use for real releases).
    [switch]$RequireSigning
)

$ErrorActionPreference = 'Stop'
$Repo = 'martxo/WatermarkRemover'
$ReleasesDir = 'd:\code\Releases'
# Kept outside Releases on purpose: `vpk upload github` ships that folder's assets, and the
# green zip is 200+ MB that has no business in every GitHub release (it lives on 阿里云盘).
$PortableDir = 'd:\code\dist\portable'
$Gh = 'd:\code\.tools\gh\bin\gh.exe'
$Dotnet = 'C:\Users\dx\.dotnet\dotnet.exe'
$Tag = "v$Version"
$Csproj = 'd:\code\src\WatermarkRemover.App\WatermarkRemover.App.csproj'
$Staging = 'd:\code\publish-staging'
$PublisherProject = 'd:\code\src\WatermarkRemover.CloudPublisher\WatermarkRemover.CloudPublisher.csproj'
$SetupName = 'WatermarkRemover-win-Setup.exe'
$InstallRoot = Join-Path $env:LOCALAPPDATA 'WatermarkRemover'
$CloudNote = if ([string]::IsNullOrWhiteSpace($Note)) { "v$Version" } else { $Note }

function Invoke-LocalStage {
    $full = Join-Path $ReleasesDir "WatermarkRemover-$Version-full.nupkg"
    if ((Test-Path $full) -and -not $Rebuild) {
        Write-Host "  $Version 安装包已存在，跳过 publish/pack（需要重建请加 -Rebuild）"
    }
    else {
        # Step 0 exists because UpdateService.CurrentVersion reports the APP ASSEMBLY version
        # (read from the compiled dll, not the package metadata). Forgetting to bump the csproj
        # therefore shipped installers that said 1.16.3 but apps that reported 1.16.2 forever.
        Write-Host "== [local 1/3] stamp csproj version $Version + dotnet publish =="
        # Explicit UTF-8 via .NET: PS 5.1's Get-Content/Set-Content default to ANSI (GBK), which
        # mangles the file's Chinese comments and swallows quotes — MSB4025 on the next build.
        $text = [IO.File]::ReadAllText($Csproj, [Text.Encoding]::UTF8)
        if ($text -notmatch '<Version>[^<]+</Version>') {
            throw "version stamp failed — <Version> not found in $Csproj"
        }
        $stamped = $text -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
        [IO.File]::WriteAllText($Csproj, $stamped, [Text.UTF8Encoding]::new($true))
        & $Dotnet publish $Csproj -c Release -o $Staging --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

        Write-Host "== [local 2/3] vpk pack $Version =="
        # --noPortable: the Portable zip is a second copy of the same payload, uploaded only for
        # people who unzip instead of installing — which we no longer support (Setup installs get
        # deltas; an unzipped copy never can).
        $signPfx = if ($SignPfx) { $SignPfx } else { $env:WM_SIGN_PFX }
        $signPassword = if ($SignPassword) { $SignPassword } else { $env:WM_SIGN_PASSWORD }
        $signThumbprint = if ($SignThumbprint) { $SignThumbprint } else { $env:WM_SIGN_THUMBPRINT }
        $azureSign = if ($AzureSignMetadata) { $AzureSignMetadata } else { $env:WM_AZURE_SIGN_FILE }

        $packArgs = @('pack', '--packId', 'WatermarkRemover', '--packVersion', $Version,
            '--packTitle', '办公助理', '--packDir', $Staging,
            '--mainExe', 'WatermarkRemover.exe', '--noPortable')
        if ($azureSign) {
            $packArgs += @('--azureTrustedSignFile', $azureSign)
        } elseif ($signPfx) {
            $packArgs += @('--signParams',
                "/f `"$signPfx`" /p `"$signPassword`" /fd SHA256 /tr http://timestamp.digicert.com /td SHA256")
        } elseif ($signThumbprint) {
            $packArgs += @('--signParams',
                "/sha1 $signThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256")
        } elseif ($RequireSigning) {
            throw ('签名证书缺失：请传 -SignPfx/-SignThumbprint/-AzureSignMetadata（或设置 WM_SIGN_*），' +
                   '或去掉 -RequireSigning 来生成未签名包。未签名安装包会被 SmartScreen 拦截。')
        } else {
            Write-Host '  WARN: 未配置签名证书 — 本次生成未签名安装包（SmartScreen 会拦截）。'
        }

        & vpk @packArgs
        if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

        # The setup bundle is what users actually double-click: check the signature is really
        # valid rather than trusting that the parameters were applied.
        $setupPath = Join-Path $ReleasesDir $SetupName
        $signature = Get-AuthenticodeSignature $setupPath
        $signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '(unsigned)' }
        Write-Host ("  setup signature: " + $signature.Status + "  " + $signer)
        if ($RequireSigning -and $signature.Status -ne 'Valid') {
            throw "安装包签名校验失败：$($signature.Status)"
        }
    }

    if ($NoInstall) {
        Write-Host '  -NoInstall：跳过本机安装'
        return
    }

    Write-Host "== [local 3/3] install locally + verify =="
    $setup = Join-Path $ReleasesDir $SetupName
    if (-not (Test-Path -LiteralPath $setup)) { throw "找不到安装包：$setup" }
    # -Wait matters: without it the shell returns while Setup is still extracting and the install
    # can be cut short, leaving a truncated main exe that exits on double-click.
    $proc = Start-Process -FilePath $setup -ArgumentList '--silent' -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "静默安装失败（退出码 $($proc.ExitCode)）" }

    $current = Join-Path $InstallRoot 'current'
    # vpk excludes .pdb; every other published file must be present and byte-identical.
    $expected = @(Get-ChildItem -LiteralPath $Staging -File | Where-Object { $_.Extension -ne '.pdb' })
    foreach ($file in $expected) {
        $installed = Join-Path $current $file.Name
        if (-not (Test-Path -LiteralPath $installed)) {
            throw "安装不完整：缺少 $($file.Name)（Setup 是否被中断？）"
        }
        $actual = (Get-Item -LiteralPath $installed).Length
        if ($actual -ne $file.Length) {
            throw ("安装文件不完整：{0} 期望 {1:N0} 字节，实际 {2:N0} 字节" -f $file.Name, $file.Length, $actual)
        }
    }

    $installedExe = Join-Path $current 'WatermarkRemover.exe'
    $product = (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion
    if (-not $product.StartsWith($Version)) {
        throw "安装版本不一致：$product（期望 $Version）"
    }
    Write-Host "  已安装 $product → $current"
}

function Invoke-CloudPublish([string[]]$extraArgs) {
    $publisherArgs = @('run', '--project', $PublisherProject, '-c', 'Release', '--',
        '--version', $Version, '--source', $Staging, '--note', $CloudNote) + $extraArgs
    # Out-Host keeps the publisher's progress out of this function's return value, so the
    # caller receives just the exit code.
    & $Dotnet @publisherArgs | Out-Host
    return $LASTEXITCODE
}

function Find-AliyunpanCli {
    $candidates = @()
    if ($env:WATERMARKREMOVER_ALIYUNPAN_CLI) { $candidates += $env:WATERMARKREMOVER_ALIYUNPAN_CLI }
    $tools = Join-Path $env:USERPROFILE 'tools'
    if (Test-Path -LiteralPath $tools) {
        $candidates += Get-ChildItem -LiteralPath $tools -Directory -Filter 'aliyunpan-*' |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'aliyunpan.exe' }
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

function Invoke-CloudStage {
    $full = Join-Path $ReleasesDir "WatermarkRemover-$Version-full.nupkg"
    if (-not (Test-Path -LiteralPath $full)) {
        throw "缺少 $full — 请先运行 -Stage local"
    }

    Write-Host '== [cloud] publish 阿里云盘 incremental (only changed chunks) =='
    $exit = Invoke-CloudPublish @()
    if ($exit -eq 0) { return }

    # The repository manifests live behind a signed download URL; on this network that hop
    # sometimes times out while the rest of the API works. Prefetch the two manifests with the
    # CLI (it downloads through its own transport) and let the publisher work offline.
    Write-Host "  云端清单接口不可用（退出码 $exit），改用 aliyunpan CLI 预取清单后重试…"
    $cli = Find-AliyunpanCli
    if (-not $cli) { throw "云发布失败（$exit），且未找到 aliyunpan CLI 可供回退。" }

    $env:ALIYUNPAN_CONFIG_DIR = Join-Path $env:APPDATA 'WatermarkRemover\aliyunpan'
    $dir = Join-Path $env:TEMP ("wr-cloud-" + $Version + "-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    & $cli download --saveto $dir --ow /WatermarkRemover/latest.json /WatermarkRemover/versions.json
    if ($LASTEXITCODE -ne 0) { throw "aliyunpan CLI 下载清单失败（$LASTEXITCODE）" }

    $latest = Get-ChildItem -LiteralPath $dir -Recurse -File -Filter 'latest.json' | Select-Object -First 1
    $versions = Get-ChildItem -LiteralPath $dir -Recurse -File -Filter 'versions.json' | Select-Object -First 1
    if (-not $latest -or -not $versions) { throw 'CLI 未下载到 latest.json / versions.json' }

    $exit = Invoke-CloudPublish @('--latest-file', $latest.FullName,
        '--versions-file', $versions.FullName, '--no-gc')
    if ($exit -ne 0) { throw "云发布重试仍失败（$exit）" }
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}

function Invoke-PortableStage {
    $zip = Join-Path $PortableDir "WatermarkRemover-$Version-Portable.zip"
    if (-not (Test-Path -LiteralPath $Staging)) {
        throw "缺少发布目录 $Staging — 请先运行 -Stage local（绿色版直接复用 publish 产物，不重新编译）"
    }
    New-Item -ItemType Directory -Path $PortableDir -Force | Out-Null

    Write-Host "== [portable 1/3] zip $Version 绿色版 =="
    # Green copy = the published payload verbatim, in a folder named after the version. No
    # Update.exe and no .portable marker on purpose: AppFolders then keeps settings, journal
    # and reports beside the exe, so a test build never touches the installed release's data.
    $folderName = "WatermarkRemover-$Version"
    $root = Join-Path $env:TEMP "wm-portable-$Version"
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    $folder = Join-Path $root $folderName
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Copy-Item -Path (Join-Path $Staging '*') -Destination $folder -Recurse -Force
    # vpk strips .pdb from every package; the green copy ships the same payload, not debug symbols.
    Get-ChildItem -LiteralPath $folder -Recurse -File -Filter '*.pdb' | Remove-Item -Force
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path $folder -DestinationPath $zip -CompressionLevel Optimal

    Write-Host "== [portable 2/3] verify zip entries =="
    # A truncated archive only shows up later, on the tester's machine, as a missing model
    # file — so every entry's size is compared against the source before the upload runs.
    $source = @(Get-ChildItem -LiteralPath $folder -Recurse -File)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            $entries[($entry.FullName -replace '/', '\')] = $entry.Length
        }
        foreach ($file in $source) {
            $relative = $file.FullName.Substring($folder.Length + 1)
            $key = "$folderName\$relative"
            if (-not $entries.ContainsKey($key)) { throw "zip 缺少 $relative" }
            if ($entries[$key] -ne $file.Length) {
                throw ("zip 内 {0} 大小不符：期望 {1:N0} 字节，实际 {2:N0} 字节" -f $relative, $file.Length, $entries[$key])
            }
        }
        Write-Host ("  {0} 个文件，{1:N0} MB，全部与发布目录一致" -f
            $source.Count, ((Get-Item -LiteralPath $zip).Length / 1MB))
    }
    finally {
        $archive.Dispose()
    }

    Write-Host '== [portable 3/3] upload 阿里云盘 /WatermarkRemover/portable =='
    $cli = Find-AliyunpanCli
    if (-not $cli) {
        throw '未找到 aliyunpan CLI（设置 WATERMARKREMOVER_ALIYUNPAN_CLI，或安装到 %USERPROFILE%\tools\aliyunpan-*）'
    }
    $env:ALIYUNPAN_CONFIG_DIR = Join-Path $env:APPDATA 'WatermarkRemover\aliyunpan'
    # mkdir fails when the folder already exists; the upload below is the real check.
    & $cli mkdir /WatermarkRemover/portable | Out-Null
    # -ow overwrites the same-named zip (old copy goes to the netdisk recycle bin). Without it
    # every rerun would be stored as "WatermarkRemover-1.16.50-Portable(1).zip" and the tester
    # could not tell which file is current.
    & $cli upload -ow $zip /WatermarkRemover/portable | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "绿色版上传失败（$LASTEXITCODE）" }

    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  已上传 $zip → /WatermarkRemover/portable/"
}

function Get-GitHubToken {
    $token = (& $Gh auth token) -join ''
    if ($token) { return $token }

    # git credential fill via a temp file: piping into a native command proved
    # encoding-unreliable (the protocol line could arrive mangled).
    $tmpCredReq = Join-Path $env:TEMP 'credential-request.txt'
    [IO.File]::WriteAllText($tmpCredReq, "protocol=https`nhost=github.com`n")
    $fill = cmd /c "git credential fill < `"$tmpCredReq`" 2>nul"
    $match = $fill | Select-String '^password=' | Select-Object -First 1
    if (-not $match) {
        throw 'no GitHub credential (gh auth token and the git credential store are both empty)'
    }
    return $match.ToString().Substring(9)
}

function Invoke-GitHubStage {
    $full = Join-Path $ReleasesDir "WatermarkRemover-$Version-full.nupkg"
    $delta = Join-Path $ReleasesDir "WatermarkRemover-$Version-delta.nupkg"
    if (-not (Test-Path -LiteralPath $full) -or -not (Test-Path -LiteralPath $delta)) {
        throw "缺少 $Version 的安装包 — 请先运行 -Stage local"
    }

    Write-Host "== [github 1/3] vpk upload github $Tag =="
    $token = Get-GitHubToken
    & vpk upload github --repoUrl "https://github.com/$Repo" --token $token `
        --releaseName "WatermarkRemover v$Version" --tag $Tag --publish
    if ($LASTEXITCODE -ne 0) { throw "vpk upload failed ($LASTEXITCODE)" }

    Write-Host "== [github 2/3] verify the REMOTE release (current-only feed) =="
    # gh prints JSON lines; PS 5.1 does not auto-parse native output — join and convert, or every
    # property read below silently yields $null and the check passes without looking at anything.
    $viewJson = (& $Gh release view $Tag --repo $Repo --json isDraft,assets) -join ''
    $view = $viewJson | ConvertFrom-Json
    if ($view.isDraft) { throw 'release is still a draft' }
    $remoteAssets = @($view.assets | ForEach-Object { $_.name })

    # The truth is what a client actually downloads: the manifest served from the release URL.
    # It must describe this version only — GithubSource concatenates the manifests of the last 10
    # releases itself, so a cumulative manifest here would multiply every entry in the feed.
    $cb = [guid]::NewGuid().ToString()
    $remoteManifestPath = Join-Path $env:TEMP "releases.win.remote.$Tag.json"
    Invoke-WebRequest -UseBasicParsing `
        -Uri "https://github.com/$Repo/releases/download/$Tag/releases.win.json?cb=$cb" `
        -OutFile $remoteManifestPath
    $remote = Get-Content $remoteManifestPath -Raw | ConvertFrom-Json
    $manifestVersions = @($remote.Assets | ForEach-Object { $_.Version } | Sort-Object -Unique)
    Write-Host ("remote manifest entries: " + $remote.Assets.Count +
        " version(s): " + ($manifestVersions -join ', '))
    if ($manifestVersions.Count -ne 1 -or $manifestVersions[0] -ne $Version) {
        throw "remote manifest must contain only $Version, but holds: $($manifestVersions -join ', ')"
    }

    $missing = @($remote.Assets | Where-Object { $remoteAssets -notcontains $_.FileName })
    if ($missing.Count -gt 0) {
        $missing | ForEach-Object { Write-Host ("  MISSING FILE: " + $_.FileName) }
        throw "remote release is missing $($missing.Count) package file(s) referenced by the manifest"
    }

    $expected = @("WatermarkRemover-$Version-full.nupkg", "WatermarkRemover-$Version-delta.nupkg")
    $strays = @($remoteAssets | Where-Object {
        $_ -like '*.nupkg' -and $expected -notcontains $_
    })
    if ($strays.Count -gt 0) {
        $strays | ForEach-Object { Write-Host ("  STRAY PACKAGE: " + $_) }
        throw "release carries $($strays.Count) package(s) from other versions — history must not be re-uploaded"
    }

    $deltas = @($remote.Assets | Where-Object { $_.Type -eq 'Delta' })
    Write-Host ("remote delta packages: " + $deltas.Count)
    $deltas | ForEach-Object {
        Write-Host ("  delta {0}  {1:N0} KB" -f $_.Version, ($_.Size / 1KB))
    }

    Write-Host '== [github 3/3] commit version stamp + push =='
    # Commit the version stamp so the next build doesn't drift back to a stale assembly version.
    git add $Csproj
    git diff --cached --quiet
    if ($LASTEXITCODE -ne 0) {
        git commit -m "chore: bump version to $Version" | Out-Null
    }
    git push
    if ($LASTEXITCODE -ne 0) { throw "git push failed ($LASTEXITCODE)" }
}

switch ($Stage) {
    'local' { Invoke-LocalStage }
    'cloud' { Invoke-CloudStage }
    'portable' { Invoke-PortableStage }
    'github' { Invoke-GitHubStage }
    'all' {
        Invoke-LocalStage
        Invoke-CloudStage
        Invoke-PortableStage
        Invoke-GitHubStage
    }
}
