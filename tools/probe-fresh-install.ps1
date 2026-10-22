#Requires -Version 5.1
<#
.SYNOPSIS
    Observes what the official NetEase launcher downloads for a fresh installation.

.DESCRIPTION
    The plugin cannot complete a fresh installation because the base packages
    (Patch.mpk, Patch1..123.mpk, ResourceExA..) are not served by the content host:
    every section answers 404, no manifest describes them, and the engine payload is
    only 1.3 GB while they are 59 GB.

    This script records two things while you let the official launcher attempt a
    fresh install:

      1. every file that appears in a probe directory (name, size, order)
      2. every outbound connection (host + port) made by the launcher

    It never downloads the whole game: run it for 1-3 minutes, then stop.

.PARAMETER ProbeDir
    An EMPTY directory the launcher should install into. Must not be the existing
    installation (D:\ywzh\ywzh_extreme).

.PARAMETER OutputDir
    Where the reports are written. Defaults to .\probe-output next to this script.

.PARAMETER WatchExisting
    Also snapshot the existing installation, to detect if the launcher writes there
    instead of the probe directory. Off by default.

.EXAMPLE
    .\probe-fresh-install.ps1 -ProbeDir "E:\sor-probe"

.NOTES
    Requires an elevated PowerShell for pktmon. Read-only with respect to the
    existing installation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ProbeDir,

    [string] $OutputDir,

    [string] $ExistingInstall = "D:\ywzh\ywzh_extreme",

    [switch] $WatchExisting,

    [string] $LauncherPath = "D:\ywzh\ywzh_launcher\Win64Release\PCLauncher\PCLauncher.exe",

    # Stop automatically after this many seconds. Also the limit used when no console is
    # available, so the script can run unattended.
    [int] $Duration = 180
)

# 'Continue', not 'Stop': the external tools this script drives (pktmon, etl2txt) write
# diagnostics to stderr and return non-zero for harmless cases. Under PowerShell 5.1 a 'Stop'
# preference turns the first such line into a NativeCommandError that kills the whole run -
# which is exactly what happened on the first attempt ("数据包监视器没有运行" from pktmon stop).
# Failures that actually matter are checked explicitly instead.
$ErrorActionPreference = 'Continue'

# ---------------------------------------------------------------- helpers

function Write-Step {
    param([string] $Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Note {
    param([string] $Message)
    Write-Host "    $Message" -ForegroundColor DarkGray
}

function Get-DirectorySnapshot {
    param([string] $Path)

    $result = @{}
    if (-not (Test-Path -LiteralPath $Path)) {
        return $result
    }

    try {
        Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue |
            ForEach-Object {
                $relative = $_.FullName.Substring($Path.Length).TrimStart('\', '/')
                $result[$relative] = $_.Length
            }
    }
    catch {
        # A directory being written to may transiently fail; the next tick retries.
    }

    return $result
}

function Format-Size {
    param([long] $Bytes)

    if ($Bytes -ge 1GB) { return "{0:N2} GB" -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return "{0:N1} MB" -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return "{0:N1} KB" -f ($Bytes / 1KB) }
    return "$Bytes B"
}

# ---------------------------------------------------------------- preflight

Write-Host ""
Write-Host "NetEase launcher - fresh install probe" -ForegroundColor White
Write-Host ("=" * 60)

if (-not $OutputDir) {
    $OutputDir = Join-Path $PSScriptRoot 'probe-output'
}

# Refuse to probe the real installation: the launcher would overwrite it.
$probeFull = [System.IO.Path]::GetFullPath($ProbeDir)
$existingFull = [System.IO.Path]::GetFullPath($ExistingInstall)

if ($probeFull.TrimEnd('\') -ieq $existingFull.TrimEnd('\')) {
    throw "ProbeDir must not be the existing installation ($existingFull)."
}
if ($probeFull.StartsWith($existingFull.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "ProbeDir must not be inside the existing installation ($existingFull)."
}
if ($existingFull.StartsWith($probeFull.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "ProbeDir must not contain the existing installation ($existingFull)."
}

$isElevated = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isElevated) {
    Write-Host ""
    Write-Host "WARNING: not running as administrator - packet capture will be skipped." -ForegroundColor Yellow
    Write-Note "File observation still works. Re-run elevated to capture hosts."
}

if (-not (Test-Path -LiteralPath $LauncherPath)) {
    Write-Host "WARNING: launcher not found at $LauncherPath" -ForegroundColor Yellow
    Write-Note "Start it manually when the script asks."
}

New-Item -ItemType Directory -Force -Path $probeFull | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# The probe directory has to be empty, otherwise the timeline is meaningless.
$existingItems = @(Get-ChildItem -LiteralPath $probeFull -Force -ErrorAction SilentlyContinue)
if ($existingItems.Count -gt 0) {
    throw "ProbeDir must be empty; it contains $($existingItems.Count) item(s): $probeFull"
}

$drive = Split-Path -Qualifier $probeFull
try {
    $free = (Get-PSDrive -Name $drive.TrimEnd(':')).Free
    Write-Note ("Probe directory : {0}" -f $probeFull)
    Write-Note ("Free space      : {0} on {1}" -f (Format-Size $free), $drive)
}
catch {
    Write-Note ("Probe directory : {0}" -f $probeFull)
}

Write-Note ("Existing install: {0}  (read-only, {1})" -f $existingFull,
    $(if ($WatchExisting) { 'also watched' } else { 'not touched' }))
Write-Note ("Reports         : {0}" -f $OutputDir)

# ---------------------------------------------------------------- capture

$pktmonEtl = Join-Path $OutputDir 'probe.etl'
$pktmonTxt = Join-Path $OutputDir 'probe.txt'
$captureActive = $false

# pktmon writes diagnostics to stderr and returns non-zero for harmless cases (no session
# running, driver not loaded). Under PowerShell 5.1 with $ErrorActionPreference = 'Stop' the
# first line of that stderr becomes a NativeCommandError and terminates the script, so the
# preference is relaxed for the duration of the call and the exit code is inspected instead.
function Invoke-PktMon {
    param([string[]] $Arguments)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & pktmon @Arguments 2>&1
        $exit = $LASTEXITCODE
        return @{ Ok = ($exit -eq 0); Output = (($output | Out-String).Trim()) }
    }
    catch {
        return @{ Ok = $false; Output = $_.Exception.Message }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

if ($isElevated) {
    Write-Step "Starting packet capture (TLS connections only, no payload)"

    # A leftover session from a previous run would make start fail.
    $null = Invoke-PktMon -Arguments @('stop')
    Start-Sleep -Milliseconds 400

    # Only the TCP 443 filter keeps the ETL small enough to decode.
    $null = Invoke-PktMon -Arguments @('filter', 'remove')
    $filterResult = Invoke-PktMon -Arguments @('filter', 'add', '-p', '443')
    if (-not $filterResult.Ok) {
        Write-Host "    filter add failed: $($filterResult.Output)" -ForegroundColor Yellow
    }

    $startResult = Invoke-PktMon -Arguments @(
        'start', '--capture', '--pkt-size', '64', '--file-name', $pktmonEtl, '--comp', 'nics'
    )

    if ($startResult.Ok) {
        $captureActive = $true
        Write-Note "Capturing to $pktmonEtl"
    }
    else {
        # Packet capture is a bonus, not a requirement: the file timeline alone answers the
        # question (does Patch.mpk appear, and where). Common causes are a driver that is not
        # loaded, a security product blocking it, or Windows running pktmon without the
        # Network Capture component.
        Write-Host "    pktmon could not start: $($startResult.Output)" -ForegroundColor Yellow
        Write-Note "Continuing with file observation only - that is enough for the main question."
    }
}
else {
    Write-Host ""
    Write-Host "  Not elevated: packet capture skipped (file observation still runs)." -ForegroundColor Yellow
    Write-Note "The file timeline alone answers whether the base packages are downloadable."
}

# ---------------------------------------------------------------- watch loop

Write-Step "Recording the probe directory every 2 seconds"
Write-Note "Press Enter to stop."

$baseline = Get-DirectorySnapshot -Path $probeFull
$existingBaseline = if ($WatchExisting) { Get-DirectorySnapshot -Path $existingFull } else { @{} }

$seen = @{}
foreach ($key in $baseline.Keys) {
    $seen[$key] = $true
}

$timeline = New-Object System.Collections.Generic.List[string]
$timeline.Add("# File timeline for $probeFull")
$timeline.Add("# Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$timeline.Add("#")
$timeline.Add("# <seconds>  <size>  <path>")

$existingChanges = New-Object System.Collections.Generic.List[string]
$existingChanges.Add("# Changes detected in the EXISTING installation $existingFull")
$existingChanges.Add("# (empty means the launcher did not touch it - good)")
$existingChanges.Add("#")

$totalBytes = 0L
$newFiles = 0
$startTime = Get-Date

Write-Host ""
Write-Host "  Now start the official launcher and click Install:" -ForegroundColor Yellow
Write-Host "    $LauncherPath" -ForegroundColor White
Write-Host ""
Write-Host "  Point it at: $probeFull" -ForegroundColor White
Write-Host "  Watch for 1-3 minutes, then press Enter here." -ForegroundColor White
Write-Host ""
Write-Host "  If it starts writing to $existingFull instead, close it IMMEDIATELY." -ForegroundColor Red
Write-Host ""

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$enterPressed = $false
$consoleUsable = $true

try {
    # Throws when there is no console (redirected input, scheduled task, some hosts).
    $null = [Console]::KeyAvailable
}
catch {
    $consoleUsable = $false
    Write-Host "  (no interactive console - the probe stops automatically after the limit)" -ForegroundColor Yellow
}

$autoStopSeconds = $Duration
Write-Note "Automatic stop after ${autoStopSeconds}s if no Enter is available."

# Poll for Enter without blocking the snapshot loop.
while (-not $enterPressed) {
    if ($consoleUsable) {
        try {
            if ([Console]::KeyAvailable) {
                $key = [Console]::ReadKey($true)
                if ($key.Key -eq 'Enter') {
                    $enterPressed = $true
                    break
                }
            }
        }
        catch {
            # The console went away; fall back to the time limit.
            $consoleUsable = $false
        }
    }

    $elapsed = [int]$stopwatch.Elapsed.TotalSeconds
    if ($elapsed -ge $autoStopSeconds) {
        $enterPressed = $true
        Write-Host ""
        Write-Host "  Reached the ${autoStopSeconds}s limit, stopping." -ForegroundColor Yellow
        break
    }

    $snapshot = Get-DirectorySnapshot -Path $probeFull

    foreach ($key in $snapshot.Keys) {
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = $true
            $size = $snapshot[$key]
            $newFiles++
            $totalBytes += $size
            $timeline.Add(("{0,6}  {1,12}  {2}" -f $elapsed, $size, $key))
            Write-Host ("  [{0,4}s] + {1}  ({2})" -f $elapsed, $key, (Format-Size $size)) -ForegroundColor Green
        }
    }

    if ($WatchExisting) {
        $nowExisting = Get-DirectorySnapshot -Path $existingFull
        foreach ($key in $nowExisting.Keys) {
            if (-not $existingBaseline.ContainsKey($key) -or
                $existingBaseline[$key] -ne $nowExisting[$key]) {
                $line = ("{0,6}  {1,12}  {2}" -f $elapsed, $nowExisting[$key], $key)
                if (-not $existingChanges.Contains($line)) {
                    $existingChanges.Add($line)
                    Write-Host ("  [{0,4}s] ! EXISTING INSTALL CHANGED: {1}" -f $elapsed, $key) -ForegroundColor Red
                }
            }
        }
    }

    Start-Sleep -Seconds 2
}

$stopwatch.Stop()
$duration = [int]$stopwatch.Elapsed.TotalSeconds

# ---------------------------------------------------------------- stop capture

if ($captureActive) {
    Write-Step "Stopping packet capture"

    $stopResult = Invoke-PktMon -Arguments @('stop')
    if (-not $stopResult.Ok) {
        Write-Host "    pktmon stop: $($stopResult.Output)" -ForegroundColor Yellow
    }

    Start-Sleep -Milliseconds 600
    $null = Invoke-PktMon -Arguments @('filter', 'remove')

    if (Test-Path -LiteralPath $pktmonEtl) {
        $decodeResult = Invoke-PktMon -Arguments @('etl2txt', $pktmonEtl, '-o', $pktmonTxt)
        if ($decodeResult.Ok -and (Test-Path -LiteralPath $pktmonTxt)) {
            Write-Note "Decoded to $pktmonTxt"
        }
        else {
            Write-Host "    Could not decode the capture: $($decodeResult.Output)" -ForegroundColor Yellow
            Write-Note "The ETL is kept at $pktmonEtl (decode it with: pktmon etl2txt <etl> -o <txt>)"
        }
    }
    else {
        Write-Host "    No capture file was produced." -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------- reports

Write-Step "Writing reports"

$timelinePath = Join-Path $OutputDir 'file-timeline.txt'
$timeline | Set-Content -LiteralPath $timelinePath -Encoding UTF8

$existingPath = Join-Path $OutputDir 'existing-install-changes.txt'
$existingChanges | Set-Content -LiteralPath $existingPath -Encoding UTF8

# Pull the interesting hosts and paths out of the capture.
$networkPath = Join-Path $OutputDir 'network.txt'
$hosts = @{}
$paths = @{}

if (Test-Path -LiteralPath $pktmonTxt) {
    try {
        $lines = Get-Content -LiteralPath $pktmonTxt -ErrorAction SilentlyContinue
        foreach ($line in $lines) {
            if ($line -match '(\d{1,3}(?:\.\d{1,3}){3})\.(\d+)\s*$') {
                $endpoint = "$($Matches[1]):$($Matches[2])"
                $hosts[$endpoint] = $true
            }
        }
    }
    catch {
        # A malformed capture is not fatal.
    }
}

$network = New-Object System.Collections.Generic.List[string]
$network.Add("# Network endpoints seen during the probe")
$network.Add("# Duration: ${duration}s")
$network.Add("#")
$network.Add("# TLS is encrypted, so this lists destinations only. Resolve them to names")
$network.Add("# with: Resolve-DnsName <ip>  (or compare against the known content host")
$network.Add("# h74.gph.netease.com / h74.update.netease.com)")
$network.Add("#")
if ($hosts.Count -eq 0) {
    $network.Add("# (no capture data - run the script as administrator to collect this)")
}
else {
    foreach ($endpoint in ($hosts.Keys | Sort-Object)) {
        $network.Add($endpoint)
    }
}
$network | Set-Content -LiteralPath $networkPath -Encoding UTF8

# ---------------------------------------------------------------- summary

$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("Fresh install probe summary")
$summary.Add(("=" * 60))
$summary.Add("")
$summary.Add("Probe directory : $probeFull")
$summary.Add("Duration        : ${duration}s")
$summary.Add("New files       : $newFiles")
$summary.Add("Total size seen : $(Format-Size $totalBytes)")
$summary.Add("")

$interesting = @('Patch.mpk', 'ResourceExA.mpk', 'Patch1.mpk')
$found = @()
foreach ($name in $interesting) {
    if ($seen.ContainsKey($name)) { $found += $name }
}
if ($seen.Keys | Where-Object { $_ -like '*Patch*.mpk' }) {
    $patchCount = @($seen.Keys | Where-Object { $_ -like '*.mpk' }).Count
    $summary.Add("Base packages   : APPEARED ($patchCount .mpk files)")
    $summary.Add("  => The base packages ARE downloadable.")
    if ($hosts.Count -gt 0) {
        $summary.Add("     Destinations are in network.txt; resolve them to get the URL shape.")
    }
    else {
        $summary.Add("     No capture was taken - run elevated with pktmon working to get the hosts.")
    }
}
elseif ($newFiles -eq 0) {
    $summary.Add("Base packages   : NOTHING was written to the probe directory")
    $summary.Add("  => The launcher ignored the probe directory, or it did not start an install.")
    $summary.Add("     Re-run with -WatchExisting to see whether it wrote to the real install.")
}
else {
    $summary.Add("Base packages   : did not appear in ${duration}s")
    $summary.Add("  => Either the install did not get that far, or they come from elsewhere.")
    $summary.Add("     Check file-timeline.txt for what DID appear.")
}
$summary.Add("")

if ($existingChanges.Count -gt 3) {
    $summary.Add("EXISTING INSTALL: CHANGED - see existing-install-changes.txt")
}
elseif ($WatchExisting) {
    $summary.Add("EXISTING INSTALL: untouched (good)")
}
$summary.Add("")
$summary.Add("Reports:")
$summary.Add("  file-timeline.txt             what appeared, in order")
$summary.Add("  network.txt                   destinations contacted")
$summary.Add("  existing-install-changes.txt  whether the real install was touched")
$summary.Add("")

$summaryPath = Join-Path $OutputDir 'summary.txt'
$summary | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host ""
$summary | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "Done. Send these three files back:" -ForegroundColor Cyan
Write-Host "  $timelinePath"
Write-Host "  $networkPath"
Write-Host "  $summaryPath"
Write-Host ""
Write-Host "You can delete the probe directory now:" -ForegroundColor DarkGray
Write-Host "  Remove-Item -LiteralPath '$probeFull' -Recurse -Force" -ForegroundColor DarkGray
Write-Host ""
