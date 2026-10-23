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
$runsDir = Join-Path $tests 'bin\runs'               # trx results live here
$stampedRunsDir = Join-Path $tests 'WatermarkRemover.Tests\bin\runs'  # per-run build output (TestRunStamp)
New-Item -ItemType Directory -Force -Path $runsDir | Out-Null
New-Item -ItemType Directory -Force -Path $stampedRunsDir | Out-Null

# Sweep run dirs: keep the two newest before this run — the run about to start adds one, so
# three stay behind (each ~500 MB). Sorting by timestamp is not enough — ad-hoc stamps
# (stripfix1, solo2) are not clock-ordered. A dir still locked by a frozen (unkillable
# kernel-zombie) testhost just fails the delete silently; it cannot collide with anything
# anyway (timestamps are unique); it vanishes after the next reboot.
# The sweep MUST target WatermarkRemover.Tests\bin\runs (where Directory.Build.props puts
# BaseOutputPath). Pointing it at bin\runs (the trx folder) sweeps an empty dir while the
# real output piles up — that is how 69 runs / 27 GB accumulated unnoticed.
$runDirs = Get-ChildItem $stampedRunsDir -Directory -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending
$runDirs | Select-Object -Skip 2 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

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
# Disk visibility: how much the run dirs hold after this pass (and how many are
# locked by kernel zombies — those clear on the next reboot).
$leftDirs = Get-ChildItem $stampedRunsDir -Directory -ErrorAction SilentlyContinue
$runsMb = [int] (($leftDirs | Get-ChildItem -Recurse -File -ErrorAction SilentlyContinue |
    Measure-Object Length -Sum).Sum / 1MB)
Write-Output ("run dirs: {0} ({1} MB under WatermarkRemover.Tests\bin\runs)" -f @($leftDirs).Count, $runsMb)
if ($trxText -match 'outcome="Passed"' -and $trxText -notmatch 'failed="[1-9]"') { exit 0 }
Write-Output 'TRX SAYS NOT PASSED'
exit 1
