# The one way to run this repo's test suite:
#   powershell -File d:\code\tests\run-tests.ps1
#
# Why this exists: testhost.exe processes frozen by the agent toolhost hold file locks on
# the bin dir they ran from and cannot be killed (taskkill fails, handles held). Any
# deterministic output dir therefore collides with those locks over time and the build dies
# in MSB3027 copy retries. So every run gets its own timestamped dir (TestRunStamp ->
# BaseOutputPath, see Directory.Build.props) and stale run dirs are swept below.
#
# The test process itself is started detached (Start-Process) with file redirection so a
# toolhost abort cannot freeze it, and this script blocks on it via Wait-Process.

$ErrorActionPreference = 'Continue'
$dotnet = 'C:\Users\dx\.dotnet\dotnet.exe'
$tests = $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runsDir = Join-Path $tests 'bin\runs'
New-Item -ItemType Directory -Force -Path $runsDir | Out-Null

# Sweep run dirs older than 2 days; a dir still locked by a frozen testhost just fails
# the delete silently — it cannot collide with anything anyway (timestamps are unique).
Get-ChildItem $runsDir -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.CreationTime -lt (Get-Date).AddDays(-2) } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

$out = Join-Path $env:TEMP "wm-tests-$stamp-out.log"
$err = Join-Path $env:TEMP "wm-tests-$stamp-err.log"

$p = Start-Process -FilePath $dotnet -ArgumentList @(
        'test', (Join-Path $tests 'WatermarkRemover.Tests\WatermarkRemover.Tests.csproj'),
        '-c', 'Release', "-p:TestRunStamp=$stamp", '--nologo',
        '--logger', "trx;LogFileName=test-results-$stamp.trx",
        '--results-directory', (Join-Path $runsDir 'trx')) `
    -RedirectStandardOutput $out -RedirectStandardError $err -PassThru -NoNewWindow
Wait-Process -Id $p.Id -Timeout 520 -ErrorAction SilentlyContinue
if (-not $p.HasExited)
{
    $p.Kill()
    Write-Output "TESTS TIMEOUT 520s (log: $out)"
    exit 1
}

Get-Content $out -Tail 25
Get-Content $err -Tail 10
# Exit by evidence from the trx result file (English XML attributes — immune to the
# console log's codepage flipping between GBK/UTF-8 depending on how we were invoked).
# $p.ExitCode is also unreliable through Start-Process redirection, hence not used.
$trx = Join-Path $runsDir "trx\test-results-$stamp.trx"
if (-not (Test-Path $trx))
{
    Write-Output 'NO TRX RESULT FILE'
    exit 1
}
$trxText = Get-Content $trx -Raw
if ($trxText -match 'outcome="Passed"' -and $trxText -notmatch 'failed="[1-9]"') { exit 0 }
Write-Output 'TRX SAYS NOT PASSED'
exit 1
