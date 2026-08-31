[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$TimeZone = "Pacific/Honolulu",

    [ValidateRange(1, 65535)]
    [int]$InspectorPort = 0,

    [string]$Python = "python",

    [ValidateRange(5, 300)]
    [int]$TimeoutSeconds = 60
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

function Resolve-PythonExecutable {
    param([Parameter(Mandatory = $true)][string]$PythonCommand)

    if (Test-Path -LiteralPath $PythonCommand -PathType Leaf) {
        return [System.IO.Path]::GetFullPath($PythonCommand)
    }

    $resolved = Get-Command -Name $PythonCommand -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $resolved) {
        throw "Python executable was not found: $PythonCommand"
    }
    return $resolved.Source
}

function Test-BootstrapPrerequisites {
    param(
        [Parameter(Mandatory = $true)][string]$PythonExecutable,
        [Parameter(Mandatory = $true)][string]$BootstrapPath
    )

    if (-not (Test-Path -LiteralPath $BootstrapPath -PathType Leaf)) {
        throw "Bootstrap script is missing: $BootstrapPath"
    }

    $checkScript = @'
import pathlib
import sys

if sys.version_info < (3, 10):
    raise SystemExit(
        f"Python 3.10 or newer is required; found {sys.version.split()[0]} at {sys.executable}"
    )

try:
    import websocket
except Exception as exc:
    raise SystemExit(
        "Python package 'websocket-client' is required. "
        f"Install it with: {sys.executable} -m pip install websocket-client\n"
        f"Import error: {exc}"
    )

if not callable(getattr(websocket, "create_connection", None)):
    raise SystemExit(
        "The imported 'websocket' module is not websocket-client (create_connection is missing). "
        f"Install the correct package with: {sys.executable} -m pip install websocket-client"
    )

bootstrap = pathlib.Path(sys.argv[1])
source = bootstrap.read_text(encoding="utf-8")
compile(source, str(bootstrap), "exec")

version = getattr(websocket, "__version__", "unknown")
print(f"Python       : {sys.executable}")
print(f"Python ver.  : {sys.version.split()[0]}")
print(f"websocket-client: {version}")
print(f"Bootstrap    : {bootstrap}")
'@

    $preflightOutput = @(& $PythonExecutable -c $checkScript $BootstrapPath 2>&1)
    $preflightExitCode = $LASTEXITCODE
    foreach ($line in $preflightOutput) {
        Write-Host $line
    }
    if ($preflightExitCode -ne 0) {
        throw "Python/bootstrap prerequisite check failed (exit code $preflightExitCode). Codex was not started."
    }
}

function Start-CodexPausedForBootstrap {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$RequestedTimeZone,
        [Parameter(Mandatory = $true)][int]$Port
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $ExecutablePath
    $psi.WorkingDirectory = Split-Path -Parent $ExecutablePath
    $psi.UseShellExecute = $false

    # Keep TZ as a child-only hint for Node/CLI subprocesses. Chromium on
    # Windows ignores it, so the renderer override is installed separately.
    $psi.EnvironmentVariables["TZ"] = $RequestedTimeZone
    $psi.EnvironmentVariables["CODEX_TZ_LAUNCHER_REQUESTED"] = $RequestedTimeZone

    # Electron documents --inspect-brk for its main process. Binding explicitly
    # to loopback prevents the inspector from being exposed on the network.
    $psi.Arguments = "--inspect-brk=127.0.0.1:$Port"

    return [System.Diagnostics.Process]::Start($psi)
}

try {
    Write-Host "Codex Time Zone Probe - Early Electron Bootstrap" -ForegroundColor Cyan
    Write-Host "================================================="

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

    # All prerequisites that could otherwise strand Codex at --inspect-brk are
    # checked BEFORE creating the Codex process. A wrong Python path, missing
    # websocket-client package, unsupported Python version, missing helper, or
    # helper syntax error now fails safely without launching Codex at all.
    $bootstrap = Join-Path $PSScriptRoot "bootstrap-electron-timezone-callframe.py"
    Write-Host ""
    Write-Host "Checking bootstrap prerequisites before Codex launch..." -ForegroundColor Cyan
    $pythonExecutable = Resolve-PythonExecutable -PythonCommand $Python
    Test-BootstrapPrerequisites -PythonExecutable $pythonExecutable -BootstrapPath $bootstrap
    Write-Host "Prerequisite check passed. Codex has not been started yet." -ForegroundColor Green

    $parentTzBefore = [System.Environment]::GetEnvironmentVariable("TZ", "Process")
    $parentMarkerBefore = [System.Environment]::GetEnvironmentVariable("CODEX_TZ_LAUNCHER_REQUESTED", "Process")

    if ($InspectorPort -eq 0) {
        $InspectorPort = Get-FreeTcpPort
    }

    $process = Start-CodexPausedForBootstrap `
        -ExecutablePath $codexExe `
        -RequestedTimeZone $TimeZone `
        -Port $InspectorPort

    if ($null -eq $process) {
        throw "Windows did not return a process object after launching Codex."
    }

    $parentTzAfter = [System.Environment]::GetEnvironmentVariable("TZ", "Process")
    $parentMarkerAfter = [System.Environment]::GetEnvironmentVariable("CODEX_TZ_LAUNCHER_REQUESTED", "Process")
    if ($parentTzBefore -ne $parentTzAfter -or $parentMarkerBefore -ne $parentMarkerAfter) {
        throw "Safety check failed: the probe process environment changed unexpectedly."
    }

    Write-Host ""
    Write-Host "Started Codex PID $($process.Id)." -ForegroundColor Green
    Write-Host "Parent PowerShell environment remained unchanged."
    Write-Host "Electron main inspector (loopback only): $InspectorPort"
    Write-Host "Bootstrap/renderer wait budget: $TimeoutSeconds seconds"
    Write-Host "Note: --inspect-brk intentionally prevents the Codex window from appearing until the helper installs the hook and resumes startup."

    Write-Host ""
    Write-Host "Installing early renderer timezone override..."
    & $pythonExecutable $bootstrap --port $InspectorPort --requested $TimeZone --timeout $TimeoutSeconds
    $bootstrapExitCode = $LASTEXITCODE

    switch ($bootstrapExitCode) {
        0 {
            Write-Host ""
            Write-Host "PASS: Chromium renderer override was installed and verified." -ForegroundColor Green
            Write-Host "The Electron main process keeps the per-WebContents debugger attachments alive."
            Write-Warning "This proves the Chromium/UI half only. The native Codex app-server timezone still requires a separate solution."
            exit 0
        }
        4 {
            Write-Warning "Electron main-process inspector was unavailable. Result: INCONCLUSIVE for early bootstrap."
            Write-Warning "If Codex opened normally, the packaged Electron build may have disabled Node CLI inspector arguments."
            exit 4
        }
        default {
            Write-Warning "Early Electron bootstrap did not verify the requested timezone."
            exit $bootstrapExitCode
        }
    }
}
catch {
    Write-Error $_
    exit 1
}
