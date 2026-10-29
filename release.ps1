# Release pipeline for WatermarkRemover.
#
#   powershell -ExecutionPolicy Bypass -File d:\code\release.ps1 -Version 1.15.30
#
# Prerequisites: publish-staging already holds the freshly published output (dotnet publish -c
# Release -o d:\code\publish-staging), git working tree committed.
#
# Why history is NOT re-uploaded: Velopack's GithubSource.GetReleaseFeed (checked against the
# 1.2.161 assembly) lists the latest 10 GitHub releases, downloads each release's own
# releases.win.json and concatenates their assets. A release therefore only needs its own full
# + delta package; the client finds the deltas of previous versions on their own tags. The old
# "re-attach every historical nupkg" step pushed ~3.7GB per release (~30 packages) for no
# benefit and put every manifest entry into the feed up to ten times. `vpk upload github`
# already publishes the current-only manifest, which is exactly the documented shape.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    # Release note shown in the 云更新版本列表 (defaults to the version tag).
    [string]$Note = '',
    # Skip the 阿里云盘 step (GitHub-only publish).
    [switch]$SkipCloud,
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
$Gh = 'd:\code\.tools\gh\bin\gh.exe'
$Tag = "v$Version"

# Step 0 exists because UpdateService.CurrentVersion reports the APP ASSEMBLY version (read
# from the compiled dll, not the package metadata). Forgetting to bump the csproj therefore
# shipped installers that said 1.16.3 but apps that reported 1.16.2 forever. Stamping the
# csproj and publishing here makes the whole chain — installer, UI, update feed — agree.
Write-Host "== 0/4 stamp csproj version $Version + dotnet publish =="
$Csproj = 'd:\code\src\WatermarkRemover.App\WatermarkRemover.App.csproj'
# Explicit UTF-8 via .NET: PS 5.1's Get-Content/Set-Content default to ANSI (GBK), which
# mangles the file's Chinese comments and swallows quotes — MSB4025 on the next build.
$text = [IO.File]::ReadAllText($Csproj, [Text.Encoding]::UTF8)
if ($text -notmatch '<Version>[^<]+</Version>') { throw "version stamp failed — <Version> not found in $Csproj" }
$stamped = $text -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
[IO.File]::WriteAllText($Csproj, $stamped, [Text.UTF8Encoding]::new($true))
& 'C:\Users\dx\.dotnet\dotnet.exe' publish $Csproj -c Release -o d:\code\publish-staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Write-Host "== 1/4 vpk pack $Version =="
# --noPortable: the Portable zip is a second 236MB copy of the same payload, uploaded only for
# people who unzip instead of installing — which we no longer support (Setup installs get
# deltas; an unzipped copy never can). Skipping it cuts ~1/5 of every release upload.
$signPfx = if ($SignPfx) { $SignPfx } else { $env:WM_SIGN_PFX }
$signPassword = if ($SignPassword) { $SignPassword } else { $env:WM_SIGN_PASSWORD }
$signThumbprint = if ($SignThumbprint) { $SignThumbprint } else { $env:WM_SIGN_THUMBPRINT }
$azureSign = if ($AzureSignMetadata) { $AzureSignMetadata } else { $env:WM_AZURE_SIGN_FILE }

$packArgs = @('pack', '--packId', 'WatermarkRemover', '--packVersion', $Version,
    '--packTitle', '办公助理', '--packDir', 'd:\code\publish-staging',
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

# The setup bundle is what users actually double-click: check the signature is really valid
# rather than trusting that the parameters were applied.
$setupPath = 'd:\code\Releases\WatermarkRemover-win-Setup.exe'
$signature = Get-AuthenticodeSignature $setupPath
$signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '(unsigned)' }
Write-Host ("  setup signature: " + $signature.Status + "  " + $signer)
if ($RequireSigning -and $signature.Status -ne 'Valid') {
    throw "安装包签名校验失败：$($signature.Status)"
}

if (-not $SkipCloud) {
    Write-Host "== 2/4 publish 阿里云盘 incremental (only changed chunks) =="
    $cloudNote = if ([string]::IsNullOrWhiteSpace($Note)) { "v$Version" } else { $Note }
    & 'C:\Users\dx\.dotnet\dotnet.exe' run --project d:\code\src\WatermarkRemover.CloudPublisher\WatermarkRemover.CloudPublisher.csproj `
        -c Release -- --version $Version --source d:\code\publish-staging --note $cloudNote
    if ($LASTEXITCODE -ne 0) { throw "cloud publish failed ($LASTEXITCODE)" }
} else {
    Write-Host "== 2/4 cloud publish skipped =="
}

Write-Host "== 3/4 vpk upload github $Tag =="
# git credential fill via a temp file: piping into a native command from a -File child
# process proved encoding-unreliable (the protocol line could arrive mangled).
$tmpCredReq = Join-Path $env:TEMP 'credential-request.txt'
[IO.File]::WriteAllText($tmpCredReq, "protocol=https`nhost=github.com`n")
$fill = cmd /c "git credential fill < `"$tmpCredReq`" 2>nul"
$token = ($fill | Select-String '^password=' | Select-Object -First 1)
if (-not $token) { throw 'no GitHub credential in Windows credential store' }
vpk upload github --repoUrl "https://github.com/$Repo" --token $token.ToString().Substring(9) `
    --releaseName "WatermarkRemover v$Version" --tag $Tag --publish
if ($LASTEXITCODE -ne 0) { throw "vpk upload failed ($LASTEXITCODE)" }

Write-Host "== 4/4 verify the REMOTE release (current-only feed) =="
# gh prints JSON lines; PS 5.1 does not auto-parse native output — join and convert, or every
# property read below silently yields $null and the check passes without looking at anything.
$viewJson = (& $Gh release view $Tag --repo $Repo --json isDraft,assets) -join ''
$view = $viewJson | ConvertFrom-Json
if ($view.isDraft) { throw 'release is still a draft' }
$remoteAssets = @($view.assets | ForEach-Object { $_.name })

# The truth is what a client actually downloads: the manifest served from the release URL.
# It must describe this version only — GithubSource concatenates the manifests of the last 10
# releases itself, so a cumulative manifest here would multiply every entry in the feed and
# force old packages onto a new tag. Every referenced file must sit on this same tag.
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
Write-Host "released $Tag — current-only feed; older deltas stay on their own releases"

# Commit the version stamp so the next build doesn't drift back to a stale assembly version.
git add $Csproj
git commit -m "chore: bump version to $Version" | Out-Null
git push | Out-Null
