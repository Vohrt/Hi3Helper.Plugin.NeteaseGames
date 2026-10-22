#Requires -Version 5.1
<#
.SYNOPSIS
    Captures the file names the official launcher downloads, and the hosts it contacts.

.DESCRIPTION
    The launcher has no "install to this folder" option, so a fresh-install probe is not
    possible. What can be observed is *what it downloads*: it writes every object into
    LocalData\Patch\tmp_file\com.temp.orbit\ using the pattern

        <16 hex>-<section>_<name>_<from>_<to>[<from>-<to>].tmp

    which carries the section and the object name - the two parts that make up the
    content URL. Those temp files are deleted when the download finishes, so this script
    snapshots the directory several times per second and keeps every name it ever sees.

    It also records the remote endpoints of the launcher process, which narrows down the
    host once DNS resolution is accounted for.

.PARAMETER GameDir
    The installation the launcher manages (read from setting.ini when omitted).

.PARAMETER OutputDir
    Where the report is written. Defaults to ..\probe-output.

.PARAMETER Minutes
    How long to watch. Defaults to 10.

.EXAMPLE
    .\capture-launcher-downloads.ps1 -Minutes 10

.NOTES
    Read-only with respect to the installation: it only lists file names.
#>
[CmdletBinding()]
param(
    [string] $GameDir,
    [string] $OutputDir,
    [int]    $Minutes = 10,
    [string] $LauncherPath = "D:\ywzh\ywzh_launcher\Win64Release\PCLauncher\PCLauncher.exe",
    [string] $LauncherSettings = "D:\ywzh\ywzh_launcher\Win64Release\setting.ini"
)

$ErrorActionPreference = 'Continue'

function Write-Step { param([string]$m) Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Note { param([string]$m) Write-Host "    $m" -ForegroundColor DarkGray }

# ---------------------------------------------------------------- resolve paths

if (-not $GameDir) {
    if (Test-Path -LiteralPath $LauncherSettings) {
        $line = Select-String -LiteralPath $LauncherSettings -Pattern '^\s*InstallPath\s*=' |
                Select-Object -First 1
        if ($line) {
            $GameDir = ($line.Line -split '=', 2)[1].Trim() -replace '\\\\', '\'
        }
    }
}

if (-not $GameDir -or -not (Test-Path -LiteralPath $GameDir)) {
    throw "Cannot determine the game directory. Pass -GameDir explicitly."
}

if (-not $OutputDir) {
    $OutputDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'probe-output'
}

$patchDir = Join-Path $GameDir 'LocalData\Patch'
$tmpDir   = Join-Path $patchDir 'tmp_file'

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$nameLog  = Join-Path $OutputDir 'downloads.txt'
$hostLog  = Join-Path $OutputDir 'endpoints.txt'
$tmpLog   = Join-Path $OutputDir 'tmp-files.txt'

Write-Host ""
Write-Host "Launcher download capture" -ForegroundColor White
Write-Host ("=" * 60)
Write-Note ("Game directory : {0}" -f $GameDir)
Write-Note ("Watching       : {0}" -f $tmpDir)
Write-Note ("Reports        : {0}" -f $OutputDir)
Write-Note ("Duration       : {0} minutes" -f $Minutes)

if (-not (Test-Path -LiteralPath $tmpDir)) {
    Write-Host ""
    Write-Host "  NOTE: $tmpDir does not exist yet." -ForegroundColor Yellow
    Write-Note "It is created by the launcher when a download starts. That is fine."
}

# ---------------------------------------------------------------- state

$seenNames = @{}          # file name -> first seen timestamp
$seenHosts = @{}          # ip:port -> first seen
$seenTmp   = @{}          # relative path -> size (last seen)

$nameLogLines = New-Object System.Collections.Generic.List[string]
$nameLogLines.Add("# Every file name observed under tmp_file, in order of first appearance")
$nameLogLines.Add("# Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$nameLogLines.Add("#")
$nameLogLines.Add("# <seconds>  <size>  <relative path>")

$hostLines = New-Object System.Collections.Generic.List[string]
$hostLines.Add("# Remote endpoints of the launcher process")
$hostLines.Add("# Resolve them with: Resolve-DnsName <ip>")

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$deadline  = (Get-Date).AddMinutes($Minutes)

Write-Host ""
Write-Host "  Start the launcher now and let it update. Press Ctrl+C to stop early." -ForegroundColor Yellow
Write-Host ("  {0}" -f $LauncherPath) -ForegroundColor White
Write-Host ""

$lastReport = 0

while ((Get-Date) -lt $deadline) {
    $elapsed = [int]$stopwatch.Elapsed.TotalSeconds

    # --- file names -------------------------------------------------------
    if (Test-Path -LiteralPath $tmpDir) {
        try {
            Get-ChildItem -LiteralPath $tmpDir -Recurse -File -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $rel = $_.FullName.Substring($tmpDir.Length).TrimStart('\', '/')
                    $size = $_.Length

                    if (-not $seenTmp.ContainsKey($rel) -or $seenTmp[$rel] -ne $size) {
                        $seenTmp[$rel] = $size
                    }

                    if (-not $seenNames.ContainsKey($rel)) {
                        $seenNames[$rel] = $elapsed
                        $nameLogLines.Add(("{0,6}  {1,13}  {2}" -f $elapsed, $size, $rel))
                        $nameLogLines | Set-Content -LiteralPath $nameLog -Encoding UTF8
                        Write-Host ("  [{0,4}s] {1}" -f $elapsed, $rel) -ForegroundColor Green
                    }
                }
        }
        catch {
            # A directory being written to can fail transiently.
        }
    }

    # --- endpoints --------------------------------------------------------
    try {
        $procs = Get-Process -Name PCLauncher, ywzh, ywzh_ -ErrorAction SilentlyContinue
        foreach ($proc in $procs) {
            Get-NetTCPConnection -OwningProcess $proc.Id -ErrorAction SilentlyContinue |
                Where-Object { $_.State -eq 'Established' -and $_.RemotePort -ne 0 } |
                ForEach-Object {
                    $key = "{0}:{1}" -f $_.RemoteAddress, $_.RemotePort
                    if (-not $seenHosts.ContainsKey($key)) {
                        $seenHosts[$key] = $elapsed
                        $hostLines.Add(("{0,6}s  {1}  (pid {2} {3})" -f $elapsed, $key, $proc.Id, $proc.ProcessName))
                        $hostLines | Set-Content -LiteralPath $hostLog -Encoding UTF8
                        Write-Host ("  [{0,4}s] endpoint {1}" -f $elapsed, $key) -ForegroundColor Cyan
                    }
                }
        }
    }
    catch {
        # The process may exit between the two calls.
    }

    # --- periodic progress ------------------------------------------------
    if ($elapsed - $lastReport -ge 30) {
        $lastReport = $elapsed
        Write-Host ("  [{0,4}s] names={1} endpoints={2} live={3}" -f `
            $elapsed, $seenNames.Count, $seenHosts.Count, $seenTmp.Count) -ForegroundColor DarkGray
        $dump = New-Object System.Collections.Generic.List[string]
        $dump.Add("# Files currently present under tmp_file (snapshot at ${elapsed}s)")
        $dump.Add("# <size>  <relative path>")
        foreach ($k in ($seenTmp.Keys | Sort-Object)) {
            $dump.Add(("{0,13}  {1}" -f $seenTmp[$k], $k))
        }
        $dump | Set-Content -LiteralPath $tmpLog -Encoding UTF8
    }

    Start-Sleep -Milliseconds 250
}

# ---------------------------------------------------------------- final report

Write-Step "Writing reports"

$nameLogLines | Set-Content -LiteralPath $nameLog -Encoding UTF8
$hostLines | Set-Content -LiteralPath $hostLog -Encoding UTF8

$dump = New-Object System.Collections.Generic.List[string]
$dump.Add("# Files present under tmp_file when the capture ended")
$dump.Add("# <size>  <relative path>")
foreach ($k in ($seenTmp.Keys | Sort-Object)) {
    $dump.Add(("{0,13}  {1}" -f $seenTmp[$k], $k))
}
$dump | Set-Content -LiteralPath $tmpLog -Encoding UTF8

$summaryPath = Join-Path $OutputDir 'capture-summary.txt'
$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("Launcher download capture")
$summary.Add(("=" * 60))
$summary.Add("")
$summary.Add("Game directory : $GameDir")
$summary.Add("Duration       : $([int]$stopwatch.Elapsed.TotalSeconds)s")
$summary.Add("Distinct names : $($seenNames.Count)")
$summary.Add("Endpoints      : $($seenHosts.Count)")
$summary.Add("")
if ($seenNames.Count -eq 0) {
    $summary.Add("NOTHING was captured.")
    $summary.Add("  The launcher did not download anything during the window - it was probably")
    $summary.Add("  already up to date. Force an update (change branch, or verify files) and retry.")
}
else {
    $summary.Add("Captured names (these carry the section and object name):")
    foreach ($k in ($seenNames.Keys | Sort-Object { $seenNames[$_] })) {
        $summary.Add("  $k")
    }
}
$summary | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host ""
$summary | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "Reports:" -ForegroundColor Cyan
Write-Host "  $nameLog"
Write-Host "  $hostLog"
Write-Host "  $tmpLog"
Write-Host "  $summaryPath"
Write-Host ""
