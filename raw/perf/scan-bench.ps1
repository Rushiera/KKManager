# ============================================================
#  scan-bench.ps1 - cold card-scan benchmark (workers 4 vs 32)
#
#  Why: KKManager's scanner defaults to 32 workers on a 16-core box. The
#  2026-11-11 handover wants one cold-scan comparison (4 vs 32) before the
#  number goes into CHANGELOG. Running it on the real db would pollute the
#  real card rows, so this harness:
#    - scans a COPY of a card folder (default: 113 cards from kkc 原神)
#    - uses a throw-away db directory per run (fresh schema => cold path:
#      every card is an INSERT, not an UPDATE)
#    - samples Processor Queue Length + child CPU% at 500 ms while it runs
#    - deletes nothing on its own: the caller removes the temp roots
#
#  Usage:
#    powershell -NoProfile -ExecutionPolicy Bypass -File scan-bench.ps1
#      optional: -Workers 4,32 -Runs 2 -CardDir <dir> -WorkRoot <dir>
#                -Log <csv> -Tag <label>
#
#  Notes:
#    - KKM_SCAN_WORKERS is the env-var override the scanner reads first
#      (Scanner.cs:513 — env var wins over the db setting scan_workers).
#    - Console output is GBK; StandardOutputEncoding is set to 936 so the
#      "枚举 N / 新增-更新 N / 跳过 N" line can be parsed and recorded.
# ============================================================
param(
    [string]$Exe = 'D:\Mau\WorkSpace\tools\CatTemp\kmrperf\KMR.exe',
    [string]$CardDir = 'D:\Mau\WorkSpace\tools\CatTemp\scanbench-cards',
    [string]$WorkRoot = 'D:\Mau\WorkSpace\tools\CatTemp\scanbench-db',
    [int[]]$Workers = @(4, 32),
    [int]$Runs = 2,
    [string]$Tag = 'cold',
    [string]$Log = 'D:\Mau\WorkSpace\KKManager\perf\scan-bench.csv'
)

$ErrorActionPreference = 'Continue'
if (-not (Test-Path $CardDir)) { Write-Output ('MISSING carddir ' + $CardDir); exit 1 }
if (-not (Test-Path $WorkRoot)) { New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null }
$dir = Split-Path -Parent $Log
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$utf8 = New-Object System.Text.UTF8Encoding($false)
$header = 'Timestamp,Tag,Workers,Run,ElapsedMs,CpuTotalSec,CpuUserSec,CpuKernelSec,KernelSharePct,CpuPctOfWall,QueuePeak,Seen,Added,Skipped,ExitCode'
if (-not (Test-Path $Log)) {
    [System.IO.File]::WriteAllText($Log, $header + "`r`n", $utf8)
}

$pcQueue = $null
try {
    $pcQueue = New-Object System.Diagnostics.PerformanceCounter('System', 'Processor Queue Length', '', $true)
    [void]$pcQueue.NextValue()
} catch { $pcQueue = $null }

for ($r = 1; $r -le $Runs; $r++) {
    foreach ($w in $Workers) {
        $dbDir = Join-Path $WorkRoot ("w{0}-r{1}" -f $w, $r)
        if (Test-Path $dbDir) { Remove-Item -LiteralPath $dbDir -Recurse -Force }
        New-Item -ItemType Directory -Path $dbDir -Force | Out-Null
        $db = Join-Path $dbDir 'kkmanager.db'

        $env:KKM_SCAN_WORKERS = [string]$w
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $Exe
        $psi.Arguments = 'scan-cards "' + $CardDir + '" --db "' + $db + '"'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        try { $psi.StandardOutputEncoding = [System.Text.Encoding]::GetEncoding(936) } catch { }
        try { $psi.StandardErrorEncoding = [System.Text.Encoding]::GetEncoding(936) } catch { }
        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        [void]$proc.Start()
        $lastCpu = $proc.TotalProcessorTime
        $lastAt = Get-Date
        $peakCpu = 0.0
        $peakQ = 0
        while (-not $proc.HasExited) {
            Start-Sleep -Milliseconds 500
            try {
                $now = Get-Date
                $cpuNow = $proc.TotalProcessorTime
                $dt = ($now - $lastAt).TotalSeconds
                if ($dt -gt 0.2) {
                    $pct = 100.0 * ($cpuNow - $lastCpu).TotalSeconds / $dt
                    if ($pct -gt $peakCpu) { $peakCpu = $pct }
                    $lastCpu = $cpuNow
                    $lastAt = $now
                }
            } catch { }
            if ($null -ne $pcQueue) {
                try {
                    $q = [double]$pcQueue.NextValue()
                    if ($q -gt $peakQ) { $peakQ = [int]$q }
                } catch { }
            }
        }
        $out = $proc.StandardOutput.ReadToEnd()
        $err = $proc.StandardError.ReadToEnd()
        $sw.Stop()
        $code = $proc.ExitCode
        $elapsed = [int][math]::Round($sw.Elapsed.TotalMilliseconds, 0)
        $cpuTotal = [math]::Round($proc.TotalProcessorTime.TotalSeconds, 2)
        $cpuUser = [math]::Round($proc.UserProcessorTime.TotalSeconds, 2)
        $cpuKernel = [math]::Round($proc.PrivilegedProcessorTime.TotalSeconds, 2)
        $proc.Dispose()
        $env:KKM_SCAN_WORKERS = $null

        $seen = ''; $added = ''; $skipped = ''
        $m = [regex]::Match($out, '枚举\s+(\d+)\s+新增/更新\s+(\d+)\s+跳过\s+(\d+)')
        if ($m.Success) { $seen = $m.Groups[1].Value; $added = $m.Groups[2].Value; $skipped = $m.Groups[3].Value }
        $share = ''
        if ($cpuTotal -gt 0.01) { $share = [math]::Round(100.0 * $cpuKernel / $cpuTotal, 1) }
        $pctWall = ''
        if ($elapsed -gt 0) { $pctWall = [math]::Round(100.0 * $cpuTotal / ($elapsed / 1000.0), 1) }
        $line = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff') + ',' + $Tag + ',' + $w + ',' + $r + ',' +
                $elapsed + ',' + $cpuTotal + ',' + $cpuUser + ',' + $cpuKernel + ',' + $share + ',' + $pctWall + ',' +
                $peakQ + ',' + $seen + ',' + $added + ',' + $skipped + ',' + $code
        [System.IO.File]::AppendAllText($Log, $line + "`r`n", $utf8)
        Write-Output ('RUN workers=' + $w + ' r=' + $r + ' ms=' + $elapsed + ' cpu=' + $cpuTotal + 's (k=' + $cpuKernel + ') qpeak=' + $peakQ + ' seen=' + $seen + ' added=' + $added + ' exit=' + $code)
        if ($code -ne 0) { Write-Output ('  STDERR ' + $err) }
    }
}
Write-Output ('SCANBENCH_DONE tag=' + $Tag + ' log=' + $Log)
