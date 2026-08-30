[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$TimeZone = "Pacific/Honolulu",

    [ValidateRange(1, 65535)]
    [int]$DebugPort = 0,

    [string]$Python = "python",

    [switch]$NoInspect
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Get-CodexPackage {
    $packages = @(Get-AppxPackage -Name "OpenAI.Codex" -ErrorAction SilentlyContinue)
    if ($packages.Count -eq 0) {
        throw "OpenAI.Codex AppX package was not found for the current Windows user."
    }

    # Prefer the highest installed version if more than one package record is visible.
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
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath
    )

    $target = [System.IO.Path]::GetFullPath($ExecutablePath)
    $matches = @()

    # Win32_Process gives us the full image path. Access may fail for unrelated elevated
    # processes; those are ignored because they cannot be positively identified as Codex.
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

function Start-CodexWithPrivateTimeZone {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$RequestedTimeZone,
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][bool]$EnableInspection
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $ExecutablePath
    $psi.WorkingDirectory = Split-Path -Parent $ExecutablePath
    $psi.UseShellExecute = $false

    # ProcessStartInfo inherits the current process environment. We change only the child
    # environment block; the PowerShell process and Windows user/system environment remain untouched.
    $psi.EnvironmentVariables["TZ"] = $RequestedTimeZone

    if ($EnableInspection) {
        $psi.Arguments = "--remote-debugging-port=$Port"
    }

    return [System.Diagnostics.Process]::Start($psi)
}

try {
    Write-Host "Codex Time Zone Probe" -ForegroundColor Cyan
    Write-Host "====================="

    $package = Get-CodexPackage
    $codexExe = Get-CodexExecutablePath -Package $package

    Write-Host "Package : $($package.PackageFullName)"
    Write-Host "Version : $($package.Version)"
    Write-Host "Path    : $codexExe"
    Write-Host "Requested TZ: $TimeZone"

    $running = @(Get-RunningCodexProcesses -ExecutablePath $codexExe)
    if ($running.Count -gt 0) {
        Write-Warning "Codex is already running from the discovered package."
        foreach ($p in $running) {
            Write-Warning "PID $($p.ProcessId): $($p.ExecutablePath)"
        }
        Write-Warning "Close Codex normally, then run this probe again. No process was stopped or modified."
        exit 2
    }

    $parentTzBefore = [System.Environment]::GetEnvironmentVariable("TZ", "Process")

    if (-not $NoInspect -and $DebugPort -eq 0) {
        $DebugPort = Get-FreeTcpPort
    }

    $process = Start-CodexWithPrivateTimeZone `
        -ExecutablePath $codexExe `
        -RequestedTimeZone $TimeZone `
        -Port $DebugPort `
        -EnableInspection (-not $NoInspect)

    if ($null -eq $process) {
        throw "Windows did not return a process object after launching Codex."
    }

    $parentTzAfter = [System.Environment]::GetEnvironmentVariable("TZ", "Process")
    if ($parentTzBefore -ne $parentTzAfter) {
        throw "Safety check failed: the probe process TZ changed unexpectedly."
    }

    Write-Host ""
    Write-Host "Started Codex PID $($process.Id)." -ForegroundColor Green
    Write-Host "Parent PowerShell TZ remained unchanged."

    if ($NoInspect) {
        Write-Host "Renderer inspection disabled (-NoInspect)."
        Write-Host "Launch test completed; timezone behavior is not verified."
        exit 0
    }

    Write-Host "DevTools port: $DebugPort"
    $inspector = Join-Path $PSScriptRoot "inspect-timezone.py"
    if (-not (Test-Path -LiteralPath $inspector -PathType Leaf)) {
        Write-Warning "Inspector script is missing: $inspector"
        Write-Warning "Codex was launched, but renderer timezone verification is INCONCLUSIVE."
        exit 3
    }

    Write-Host ""
    Write-Host "Inspecting Chromium renderer..."
    & $Python $inspector --port $DebugPort --requested $TimeZone
    $inspectionExitCode = $LASTEXITCODE

    if ($inspectionExitCode -eq 0) {
        exit 0
    }

    if ($inspectionExitCode -eq 4) {
        Write-Warning "Codex launched, but renderer inspection was unavailable. Result: INCONCLUSIVE."
        exit 3
    }

    Write-Warning "Renderer inspection completed but did not confirm the requested timezone."
    exit $inspectionExitCode
}
catch {
    Write-Error $_
    exit 1
}
