# Verify a GitHub release's Velopack feed: the release must carry only its own version, and
# the last 10 releases must each expose their own full/delta packages.
#
# Why the last 10: GithubSource.GetReleaseFeed (Velopack 1.2.161) requests
# `releases?per_page=10&page=1`, downloads each release's own releases.win.json and
# concatenates the assets. Each release therefore only needs its current full + delta; the
# client assembles the delta chain from neighbouring releases itself. Re-uploading the whole
# history onto every tag (the old release.ps1 step 3) was pure duplication.
#
#   powershell -File d:\code\verify-release.ps1 -Version 1.16.5

param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 # PS 5.1 defaults

$Repo = 'martxo/WatermarkRemover'
$Gh = 'd:\code\.tools\gh\bin\gh.exe'
$Tag = "v$Version"

function Get-ReleaseAssetNames([string]$releaseTag)
{
    $json = (& $Gh release view $releaseTag --repo $Repo --json assets) -join ''
    return @(($json | ConvertFrom-Json).assets | ForEach-Object { $_.name })
}

# ── the current tag: current-only manifest, every referenced file present ──
$viewJson = (& $Gh release view $Tag --repo $Repo --json isDraft,assets) -join ''
$view = $viewJson | ConvertFrom-Json
"remote draft: $($view.isDraft), assets: $($view.assets.Count)"
$remoteAssets = @($view.assets | ForEach-Object { $_.name })

# In-memory fetch: no temp file, no dropper-shaped pattern for Defender to flag.
$cb = [guid]::NewGuid().ToString()
$remote = Invoke-RestMethod -Uri "https://github.com/$Repo/releases/download/$Tag/releases.win.json?cb=$cb"
$manifestVersions = @($remote.Assets | ForEach-Object { $_.Version } | Sort-Object -Unique)
"manifest entries: $($remote.Assets.Count), version(s): $($manifestVersions -join ', ')"
if ($manifestVersions.Count -ne 1 -or $manifestVersions[0] -ne $Version)
{
    throw "manifest must describe only $Version, but holds: $($manifestVersions -join ', ')"
}

$missing = @($remote.Assets | Where-Object { $remoteAssets -notcontains $_.FileName })
if ($missing.Count -gt 0)
{
    $missing | ForEach-Object { "MISSING FILE: $($_.FileName)" }
    throw "feed is broken: $($missing.Count) manifest-referenced file(s) missing from $Tag"
}

$expected = @("WatermarkRemover-$Version-full.nupkg", "WatermarkRemover-$Version-delta.nupkg")
$strays = @($remoteAssets | Where-Object { $_ -like '*.nupkg' -and $expected -notcontains $_ })
if ($strays.Count -gt 0)
{
    $strays | ForEach-Object { "STRAY PACKAGE: $_" }
    throw "$Tag carries $($strays.Count) package(s) from other versions"
}

# ── the last 10 releases: each version's own full/delta must be on its own tag ──
$listJson = (& $Gh release list --repo $Repo --limit 10 --json tagName) -join ''
$recentTags = @(($listJson | ConvertFrom-Json) | ForEach-Object { $_.tagName })
"recent releases checked for chain assets: $($recentTags.Count)"
foreach ($recentTag in $recentTags)
{
    $recentVersion = $recentTag -replace '^v', ''
    $names = Get-ReleaseAssetNames $recentTag
    $full = "WatermarkRemover-$recentVersion-full.nupkg"
    $delta = "WatermarkRemover-$recentVersion-delta.nupkg"
    if ($names -notcontains $full)
    {
        # Pre-Velopack tags (e.g. v1.14.8) have no nupkgs; GithubSource simply skips them.
        if (@($names | Where-Object { $_ -like '*.nupkg' }).Count -gt 0)
        {
            throw "$recentTag has packages but no $full — the feed cannot offer this version"
        }
        continue
    }
    if ($names -notcontains $delta)
    {
        "  note: $recentTag has no delta (first release, or packed without a previous full)"
    }
}
'every manifest-referenced file exists; every recent release carries its own packages'
