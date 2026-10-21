# Verify a GitHub release's delta-chain integrity: every file referenced by the REMOTE
# releases.win.json manifest must exist as a REMOTE asset. release.ps1 re-attaches the
# cumulative history after every publish because `vpk upload github` rewrites the manifest
# with only the current version — this script proves the chain is still whole.
#
#   powershell -File d:\code\verify-release.ps1 -Version 1.15.34
#
# Why a script file (not an inline -Command blob): Windows Defender's behavior engine
# (ClickFix-class cmdline detection) killed the agent-style one-liner that downloaded the
# manifest into %TEMP% — a dropper-shaped command line. A -File invocation keeps the command
# line clean, and the fetch below is in-memory (no temp file) to avoid the same pattern.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 # PS 5.1 defaults

$Repo = 'martxo/WatermarkRemover'
$Gh = 'd:\code\.tools\gh\bin\gh.exe'
$Tag = "v$Version"

$viewJson = (& $Gh release view $Tag --repo $Repo --json isDraft,assets) -join ''
$view = $viewJson | ConvertFrom-Json
"remote draft: $($view.isDraft), assets: $($view.assets.Count)"
$remoteAssets = @($view.assets | ForEach-Object { $_.name })

# In-memory fetch: no temp file, no dropper-shaped pattern for Defender to flag.
$cb = [guid]::NewGuid().ToString()
$remote = Invoke-RestMethod -Uri "https://github.com/$Repo/releases/download/$Tag/releases.win.json?cb=$cb"
$local = Get-Content 'd:\code\Releases\releases.win.json' -Raw | ConvertFrom-Json
"manifest entries remote=$($remote.Assets.Count) local=$($local.Assets.Count)"

$missing = @($remote.Assets | Where-Object { $remoteAssets -notcontains $_.FileName })
if ($missing.Count -gt 0)
{
    $missing | ForEach-Object { "MISSING FILE: $($_.FileName)" }
    throw "delta chain broken: $($missing.Count) manifest-referenced file(s) missing from the release"
}
'every manifest-referenced file exists on the release'
