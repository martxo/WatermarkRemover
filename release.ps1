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
$fill = "protocol=https`nhost=github.com`n" | git credential fill
$token = ($fill | Select-String '^password=' | Select-Object -First 1)
if (-not $token) { throw 'no GitHub credential in Windows credential store' }
vpk upload github --repoUrl "https://github.com/$Repo" --token $token.ToString().Substring(9) `
    --releaseName "WatermarkRemover v$Version" --tag $Tag --publish
if ($LASTEXITCODE -ne 0) { throw "vpk upload failed ($LASTEXITCODE)" }

Write-Host "== 3/4 re-attach cumulative history (keeps the delta chain) =="
$pkgs = Get-ChildItem $ReleasesDir -File -Filter *.nupkg | ForEach-Object { $_.FullName }
& $Gh release upload $Tag @pkgs `
    "$ReleasesDir\releases.win.json" "$ReleasesDir\RELEASES" `
    --repo $Repo --clobber
if ($LASTEXITCODE -ne 0) { throw "history attach failed ($LASTEXITCODE)" }

Write-Host "== 4/4 verify =="
$view = & $Gh release view $Tag --repo $Repo --json isDraft,assets
$manifest = Get-Content "$ReleasesDir\releases.win.json" -Raw | ConvertFrom-Json
Write-Host ("release draft : " + ($view.isDraft -eq $false))
Write-Host ("manifest entries: " + $manifest.Assets.Count)
$manifest.Assets | Where-Object { $_.Type -eq 'Delta' } | ForEach-Object {
    Write-Host ("  delta {0}  {1:N0} KB" -f $_.Version, ($_.Size / 1KB))
}
if ($view.isDraft) { throw 'release is still a draft' }
Write-Host "released $Tag"
