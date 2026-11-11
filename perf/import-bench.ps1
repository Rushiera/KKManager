# ============================================================
#  import-bench.ps1 - wall-clock + CPU split benchmark for KMR index-import
#
#  Why: the 2026-11-11 handover quoted "18 s" for one index-import, but that
#  number was taken while typeperf + ts_jobwatch were also running. Solo runs
#  land near 12-16 s. A benchmark has to say which composition it measured.
#
#  Why the CPU split matters here: the fix under test (P1) wraps the import in
#  batched transactions. A per-row autocommit burns kernel time in syscalls and
#  journal writes; batching should move seconds out of Privileged time. Wall
#  clock alone cannot tell "faster algorithm" from "less kernel traffic".
#  Process.TotalProcessorTime / UserProcessorTime / PrivilegedProcessorTime stay
#  readable after exit, so no sampling observer is needed (and no observer means
#  the numbers are not polluted by one).
#
#  Method: alternate ExeA / ExeB on the SAME db + SAME index root, N rounds
#  each, one CSV line per run. index-import is idempotent (upsert + orphan
#  cleanup), so repeated runs are safe; the counts printed by the exe must stay
#  identical across runs.
#
#  Usage:
#    powershell -NoProfile -ExecutionPolicy Bypass -File import-bench.ps1
#      optional: -Runs 3 -Tag solo
#                -ExeA <baseline exe> -LabelA <name>
#                -ExeB <candidate exe> -LabelB <name>
#
#  Notes:
#    - Console output of KMR is GBK; this script does not parse it, it records
#      exit code + wall time + CPU split only.
#    - ElapsedMs includes process start (single-file publish extracts native
#      libs on first run, so keep the compare like-for-like).
# ============================================================
param(
    [string]$Db = 'D:\Mau\WorkSpace\KKManager\dist\data\kkmanager.db',
    [string]$Root = 'D:\Mau\WorkSpace\KKManager\dist\data\kkc',
    [string]$ExeA = 'D:\Mau\WorkSpace\tools\CatTemp\kmrperf-p0\KMR.exe',
    [string]$ExeB = 'D:\Mau\WorkSpace\tools\CatTemp\kmrperf\KMR.exe',
    [string]$LabelA = 'release-p0',
    [string]$LabelB = 'release-p1',
    [int]$Runs = 3,
    [string]$Tag = 'solo',
    [string]$Log = 'D:\Mau\WorkSpace\KKManager\perf\import-bench.csv'
)

$ErrorActionPreference = 'Continue'
$dir = Split-Path -Parent $Log
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$utf8 = New-Object System.Text.UTF8Encoding($false)
$header = 'Timestamp,Tag,Label,Exe,ElapsedMs,CpuTotalSec,CpuUserSec,CpuKernelSec,KernelSharePct,CpuPctOfWall,ExitCode'
if (-not (Test-Path $Log)) {
    [System.IO.File]::WriteAllText($Log, $header + "`r`n", $utf8)
}

$pairs = @(
    [pscustomobject]@{ Label = $LabelA; Exe = $ExeA },
    [pscustomobject]@{ Label = $LabelB; Exe = $ExeB }
)

for ($i = 1; $i -le $Runs; $i++) {
    foreach ($p in $pairs) {
        if (-not (Test-Path $p.Exe)) {
            Write-Output ('MISSING ' + $p.Label + ' ' + $p.Exe)
            continue
        }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $p.Exe
        $psi.Arguments = 'index-import --root "' + $Root + '" --db "' + $Db + '"'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        [void]$proc.Start()
        $null = $proc.StandardOutput.ReadToEnd()
        $null = $proc.StandardError.ReadToEnd()
        $proc.WaitForExit()
        $sw.Stop()
        $elapsed = [int][math]::Round($sw.Elapsed.TotalMilliseconds, 0)
        $code = $proc.ExitCode
        $cpuTotal = [math]::Round($proc.TotalProcessorTime.TotalSeconds, 2)
        $cpuUser = [math]::Round($proc.UserProcessorTime.TotalSeconds, 2)
        $cpuKernel = [math]::Round($proc.PrivilegedProcessorTime.TotalSeconds, 2)
        $proc.Dispose()
        $share = ''
        if ($cpuTotal -gt 0.01) { $share = [math]::Round(100.0 * $cpuKernel / $cpuTotal, 1) }
        $pctWall = ''
        if ($elapsed -gt 0) { $pctWall = [math]::Round(100.0 * $cpuTotal / ($elapsed / 1000.0), 1) }
        $line = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff') + ',' + $Tag + ',' + $p.Label + ',' + $p.Exe + ',' +
                $elapsed + ',' + $cpuTotal + ',' + $cpuUser + ',' + $cpuKernel + ',' + $share + ',' + $pctWall + ',' + $code
        [System.IO.File]::AppendAllText($Log, $line + "`r`n", $utf8)
        Write-Output ('RUN ' + $p.Label + ' ms=' + $elapsed + ' cpu=' + $cpuTotal + 's (u=' + $cpuUser + ' k=' + $cpuKernel + ') exit=' + $code)
    }
}
Write-Output ('BENCH_DONE tag=' + $Tag + ' log=' + $Log)
