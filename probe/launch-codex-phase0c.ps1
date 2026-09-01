[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$TimeZone = "Pacific/Honolulu",

    [string]$WindowsTimeZoneId = "",

    [string]$NativeBinDirectory = $PSScriptRoot,

    [ValidateRange(5, 300)]
    [int]$WaitForAppServerSeconds = 60,

    [string]$StatePath = (Join-Path $env:TEMP "CodexTimeZonePhase0C\latest.json")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-CodexPackage {
    $packages = @(Get-AppxPackage -Name "OpenAI.Codex" -ErrorAction SilentlyContinue)
    if ($packages.Count -eq 0) {
        throw "OpenAI.Codex AppX package was not found for the current Windows user."
    }
    return $packages | Sort-Object Version -Descending | Select-Object -First 1
}

function Get-CodexExecutablePath {
    param([Parameter(Mandatory = $true)]$Package)

    $candidate = Join-Path $Package.InstallLocation "app\ChatGPT.exe"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Codex package was found, but app\ChatGPT.exe does not exist at: $candidate"
    }
    return [System.IO.Path]::GetFullPath($candidate)
}

function Get-RunningCodexProcesses {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    $target = [System.IO.Path]::GetFullPath($ExecutablePath)
    $matches = @()
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'ChatGPT.exe'" -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        if ([string]::IsNullOrWhiteSpace($process.ExecutablePath)) {
            continue
        }
        $candidate = [System.IO.Path]::GetFullPath($process.ExecutablePath)
        if ([string]::Equals($candidate, $target, [System.StringComparison]::OrdinalIgnoreCase)) {
            $matches += $process
        }
    }
    return $matches
}

function Resolve-WindowsTimeZoneId {
    param(
        [Parameter(Mandatory = $true)][string]$IanaId,
        [string]$ExplicitWindowsId
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitWindowsId)) {
        return $ExplicitWindowsId
    }

    # Windows PowerShell 5.1 does not expose TimeZoneInfo.TryConvertIanaIdToWindowsId.
    # Keep this Phase 0C probe intentionally small; the production .NET launcher will
    # use the runtime conversion API for the complete IANA catalog.
    $known = @{
        "Pacific/Honolulu"    = "Hawaiian Standard Time"
        "Pacific/Kiritimati"  = "Line Islands Standard Time"
        "Asia/Shanghai"       = "China Standard Time"
        "Asia/Tokyo"          = "Tokyo Standard Time"
        "Europe/London"       = "GMT Standard Time"
        "Europe/Paris"        = "Romance Standard Time"
        "America/New_York"    = "Eastern Standard Time"
        "America/Los_Angeles" = "Pacific Standard Time"
        "Etc/UTC"             = "UTC"
        "UTC"                 = "UTC"
    }

    if ($known.ContainsKey($IanaId)) {
        return $known[$IanaId]
    }

    throw "No built-in Phase 0C mapping exists for '$IanaId'. Re-run with -WindowsTimeZoneId '<Windows ID>'."
}

try {
    Write-Host "Codex Time Zone Probe - Phase 0C Native App-Server" -ForegroundColor Cyan
    Write-Host "====================================================="
    Write-Warning "Experimental: this run injects an in-memory API shim into ChatGPT.exe only as a child-process broker, then into codex.exe app-server for native timezone APIs. No file or Windows timezone is modified."

    if (-not [Environment]::Is64BitOperatingSystem) {
        throw "The current Phase 0C probe is win-x64 only."
    }

    $package = Get-CodexPackage
    $codexExe = Get-CodexExecutablePath -Package $package
    $windowsId = Resolve-WindowsTimeZoneId -IanaId $TimeZone -ExplicitWindowsId $WindowsTimeZoneId

    try {
        $targetZone = [System.TimeZoneInfo]::FindSystemTimeZoneById($windowsId)
    }
    catch {
        throw "Windows timezone ID '$windowsId' is not available on this computer."
    }

    $launcher = Join-Path $NativeBinDirectory "tzshim-launcher.exe"
    $shim = Join-Path $NativeBinDirectory "CodexTzShim64.dll"
    $broker = Join-Path $NativeBinDirectory "CodexTzBroker64.dll"
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        throw "Native launcher is missing: $launcher"
    }
    if (-not (Test-Path -LiteralPath $shim -PathType Leaf)) {
        throw "Native timezone shim is missing: $shim"
    }
    if (-not (Test-Path -LiteralPath $broker -PathType Leaf)) {
        throw "Native broker is missing: $broker"
    }

    $running = @(Get-RunningCodexProcesses -ExecutablePath $codexExe)
    if ($running.Count -gt 0) {
        Write-Warning "Codex is already running from the discovered package."
        foreach ($process in $running) {
            Write-Warning "PID $($process.ProcessId): $($process.ExecutablePath)"
        }
        Write-Warning "Close Codex normally and run the probe again. No process was stopped or modified."
        exit 2
    }

    $stateDirectory = Split-Path -Parent $StatePath
    if ([string]::IsNullOrWhiteSpace($stateDirectory)) {
        $stateDirectory = (Get-Location).Path
        $StatePath = Join-Path $stateDirectory $StatePath
    }
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    $stamp = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss")
    $logPath = Join-Path $stateDirectory "tzshim-$stamp.log"
    if (Test-Path -LiteralPath $logPath) {
        Remove-Item -LiteralPath $logPath -Force
    }

    $systemZoneBefore = [System.TimeZoneInfo]::Local.Id
    $launchUtc = [DateTime]::UtcNow

    Write-Host "Package          : $($package.PackageFullName)"
    Write-Host "Version          : $($package.Version)"
    Write-Host "ChatGPT.exe      : $codexExe"
    Write-Host "Requested IANA   : $TimeZone"
    Write-Host "Windows zone ID  : $windowsId"
    Write-Host "System zone      : $systemZoneBefore"
    Write-Host "Shim log         : $logPath"
    Write-Host ""
    Write-Host "Launching Codex with the selective native broker..."

    $launchOutput = @(& $launcher `
        --timezone-windows-id $windowsId `
        --timezone-iana $TimeZone `
        --dll CodexTzBroker64.dll `
        --log $logPath `
        -- $codexExe 2>&1)
    $launchExitCode = $LASTEXITCODE
    foreach ($line in $launchOutput) {
        Write-Host $line
    }
    if ($launchExitCode -ne 0) {
        throw "Native launcher failed with exit code $launchExitCode. Codex was not successfully started."
    }

    $rootPid = 0
    foreach ($line in $launchOutput) {
        if ([string]$line -match "Started PID\s+(\d+)") {
            $rootPid = [int]$Matches[1]
            break
        }
    }
    if ($rootPid -eq 0) {
        Write-Warning "Could not parse the ChatGPT.exe PID from launcher output. The log can still be inspected."
    }

    $state = [ordered]@{
        schema_version = 1
        launch_utc = $launchUtc.ToString("o")
        root_pid = $rootPid
        package_full_name = [string]$package.PackageFullName
        chatgpt_exe = $codexExe
        expected_iana = $TimeZone
        windows_timezone_id = $windowsId
        system_timezone_before = $systemZoneBefore
        shim_log = $logPath
    }
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $StatePath -Encoding UTF8

    $systemZoneAfter = [System.TimeZoneInfo]::Local.Id
    if ($systemZoneAfter -ne $systemZoneBefore) {
        throw "Safety check failed: Windows timezone changed from '$systemZoneBefore' to '$systemZoneAfter'."
    }
    Write-Host "Windows timezone remains unchanged." -ForegroundColor Green

    Write-Host "Waiting for the Desktop app to spawn codex.exe app-server..."
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitForAppServerSeconds)
    $nativeLine = $null
    $failureLine = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $logPath) {
            $lines = @(Get-Content -LiteralPath $logPath -ErrorAction SilentlyContinue)
            $failureLine = $lines |
                Where-Object { $_ -match "role=broker injection=failed" } |
                Select-Object -Last 1
            $nativeLine = $lines |
                Where-Object { $_ -match 'exe="[^"]*\\codex\.exe" target=loaded windows-id=' } |
                Select-Object -Last 1
            if ($null -ne $failureLine -or $null -ne $nativeLine) {
                break
            }
        }
        Start-Sleep -Milliseconds 250
    }

    if ($null -ne $failureLine) {
        Write-Warning "The broker saw codex.exe app-server but native DLL injection failed:"
        Write-Warning $failureLine
        Write-Warning "Result: INCONCLUSIVE. Codex was not terminated. Close it normally after inspecting any startup error."
        exit 4
    }

    if ($null -eq $nativeLine) {
        Write-Warning "No injected codex.exe app-server was observed within $WaitForAppServerSeconds seconds."
        Write-Warning "The broker remains loaded in ChatGPT.exe, so it can still catch a delayed app-server launch when you create the first turn."
    }
    else {
        Write-Host "Native app-server injection observed:" -ForegroundColor Green
        Write-Host $nativeLine
    }
    Write-Host ""
    Write-Host "Next:" -ForegroundColor Cyan
    Write-Host "  1. In Codex, start a NEW conversation and send: Reply only with OK."
    Write-Host "  2. Then run this script from the same extracted folder:"
    Write-Host "     powershell -ExecutionPolicy Bypass -File .\inspect-codex-phase0c.ps1"
    Write-Host ""
    Write-Host "State file: $StatePath"
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
