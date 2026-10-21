# Release pipeline for WatermarkRemover.
#
#   powershell -ExecutionPolicy Bypass -File d:\code\release.ps1 -Version 1.15.30
#
# Prerequisites: publish-staging already holds the freshly published output (dotnet publish -c
# Release -o d:\code\publish-staging), git working tree committed.
#
# Why step 3 exists: `vpk upload github` rewrites the remote releases.win.json to contain ONLY
# the current version, which severs the delta chain — any client older than the previous
# release finds no delta path and falls back to the ~197MB full package. Re-attaching every
# historical package plus the cumulative local manifest keeps the chain intact, so machines a
# few versions behind upgrade through a few hundred KB of deltas instead.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$Repo = 'martxo/WatermarkRemover'
$ReleasesDir = 'd:\code\Releases'
$Gh = 'd:\code\.tools\gh\bin\gh.exe'
$Tag = "v$Version"

Write-Host "== 1/4 vpk pack $Version =="
vpk pack --packId WatermarkRemover --packVersion $Version `
    --packDir d:\code\publish-staging --mainExe WatermarkRemover.exe
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

Write-Host "== 2/4 vpk upload github $Tag =="
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

Write-Host "== 3/4 re-attach cumulative history (keeps the delta chain) =="
# Manifests go FIRST, alone: they are a few KB, while the nupkgs are ~200MB each. A partial
# package upload must never leave the remote manifest stale — vpk upload github has already
# rewritten it to current-only at this point, and a current-only manifest is exactly what
# severs the delta chain and pushes every client onto the ~197MB full package. Sorting the
# packages by size keeps the chain-critical small deltas ahead of the interchangeable fulls.
& $Gh release upload $Tag "$ReleasesDir\releases.win.json" "$ReleasesDir\RELEASES" `
    --repo $Repo --clobber
if ($LASTEXITCODE -ne 0) { throw 'manifest attach failed (delta chain would be severed)' }

$pkgs = Get-ChildItem $ReleasesDir -File -Filter *.nupkg | Sort-Object Length |
    ForEach-Object { $_.FullName }
& $Gh release upload $Tag @pkgs --repo $Repo --clobber
if ($LASTEXITCODE -ne 0) { throw 'package attach failed' }

Write-Host "== 4/4 verify the REMOTE feed (a local manifest proves nothing) =="
# gh prints JSON lines; PS 5.1 does not auto-parse native output — join and convert, or every
# property read below silently yields $null and the check passes without looking at anything
# (which is how the severed chain of v1.15.34 shipped unnoticed).
$viewJson = (& $Gh release view $Tag --repo $Repo --json isDraft,assets) -join ''
$view = $viewJson | ConvertFrom-Json
if ($view.isDraft) { throw 'release is still a draft' }
$remoteAssets = @($view.assets | ForEach-Object { $_.name })

# The truth is what a client actually downloads: the manifest served from the release URL.
# It must be the cumulative one (not vpk's current-only rewrite), and every package it
# references must exist as a release asset — one missing delta file breaks the whole chain.
$cb = [guid]::NewGuid().ToString()
$remoteManifestPath = Join-Path $env:TEMP "releases.win.remote.$Tag.json"
Invoke-WebRequest -UseBasicParsing `
    -Uri "https://github.com/$Repo/releases/download/$Tag/releases.win.json?cb=$cb" `
    -OutFile $remoteManifestPath
$remote = Get-Content $remoteManifestPath -Raw | ConvertFrom-Json
$local = Get-Content "$ReleasesDir\releases.win.json" -Raw | ConvertFrom-Json
Write-Host ("remote manifest entries: " + $remote.Assets.Count +
    " (local: " + $local.Assets.Count + ")")
if ($remote.Assets.Count -ne $local.Assets.Count) {
    throw "remote manifest holds $($remote.Assets.Count) entries but the local cumulative manifest holds $($local.Assets.Count) — the attach did not take effect"
}

$missing = @($remote.Assets | Where-Object { $remoteAssets -notcontains $_.FileName })
if ($missing.Count -gt 0) {
    $missing | ForEach-Object { Write-Host ("  MISSING FILE: " + $_.FileName) }
    throw "remote release is missing $($missing.Count) package file(s) referenced by the manifest"
}

$deltas = @($remote.Assets | Where-Object { $_.Type -eq 'Delta' })
Write-Host ("remote delta packages: " + $deltas.Count)
$deltas | ForEach-Object {
    Write-Host ("  delta {0}  {1:N0} KB" -f $_.Version, ($_.Size / 1KB))
}
Write-Host "released $Tag — delta chain intact"
