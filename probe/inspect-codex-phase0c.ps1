[CmdletBinding()]
param(
    [string]$StatePath = (Join-Path $env:TEMP "CodexTimeZonePhase0C\latest.json"),

    [ValidateRange(100, 100000)]
    [int]$TailLines = 20000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-LatestTurnContext {
    param(
        [Parameter(Mandatory = $true)][string]$RolloutPath,
        [Parameter(Mandatory = $true)][DateTime]$LaunchUtc,
        [Parameter(Mandatory = $true)][int]$MaximumTailLines
    )

    $contexts = @()
    foreach ($line in (Get-Content -LiteralPath $RolloutPath -Tail $MaximumTailLines -ErrorAction Stop)) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line -notmatch '"type"\s*:\s*"turn_context"') {
            continue
        }
        try {
            $entry = $line | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            continue
        }
        if ($entry.type -ne "turn_context" -or $null -eq $entry.payload) {
            continue
        }
        $entryUtc = $null
        try {
            $entryUtc = [DateTimeOffset]::Parse([string]$entry.timestamp).UtcDateTime
        }
        catch {
        }
        $contexts += [pscustomobject]@{
            Entry = $entry
            Utc = $entryUtc
        }
    }

    $afterLaunch = @($contexts | Where-Object {
        $null -ne $_.Utc -and $_.Utc -ge $LaunchUtc.AddSeconds(-5)
    })
    if ($afterLaunch.Count -gt 0) {
        return ($afterLaunch | Sort-Object Utc | Select-Object -Last 1).Entry
    }
    if ($contexts.Count -gt 0) {
        return ($contexts | Select-Object -Last 1).Entry
    }
    return $null
}

try {
    Write-Host "Codex Time Zone Probe - Phase 0C Result" -ForegroundColor Cyan
    Write-Host "========================================"

    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        throw "Phase 0C state file was not found: $StatePath. Run launch-codex-phase0c.ps1 first."
    }
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    $launchUtc = [DateTimeOffset]::Parse([string]$state.launch_utc).UtcDateTime
    $expectedIana = [string]$state.expected_iana
    $windowsId = [string]$state.windows_timezone_id
    $systemBefore = [string]$state.system_timezone_before
    $logPath = [string]$state.shim_log

    $sessionsRoot = Join-Path $HOME ".codex\sessions"
    if (-not (Test-Path -LiteralPath $sessionsRoot -PathType Container)) {
        throw "Codex sessions directory was not found: $sessionsRoot"
    }

    $candidates = @(Get-ChildItem -LiteralPath $sessionsRoot -Recurse -File -Filter "rollout-*.jsonl" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $launchUtc.AddMinutes(-1) } |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($candidates.Count -eq 0) {
        $candidates = @(Get-ChildItem -LiteralPath $sessionsRoot -Recurse -File -Filter "rollout-*.jsonl" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending)
        if ($candidates.Count -eq 0) {
            throw "No rollout JSONL files were found under $sessionsRoot"
        }
        Write-Warning "No rollout modified after this launch was found; inspecting the newest rollout as a fallback."
    }

    $selectedRollout = $null
    $context = $null
    foreach ($candidate in $candidates) {
        $candidateContext = Get-LatestTurnContext `
            -RolloutPath $candidate.FullName `
            -LaunchUtc $launchUtc `
            -MaximumTailLines $TailLines
        if ($null -ne $candidateContext) {
            $selectedRollout = $candidate
            $context = $candidateContext
            break
        }
    }

    if ($null -eq $context) {
        Write-Warning "No turn_context record was found after the Phase 0C launch."
        Write-Warning "Create a NEW Codex conversation, send one message, then run this inspector again."
        exit 4
    }

    $contextUtc = $launchUtc
    try {
        $contextUtc = [DateTimeOffset]::Parse([string]$context.timestamp).UtcDateTime
    }
    catch {
        Write-Warning "Could not parse the turn timestamp; expected-date validation will use the current UTC time."
        $contextUtc = [DateTime]::UtcNow
    }
    $targetZone = [System.TimeZoneInfo]::FindSystemTimeZoneById($windowsId)
    $expectedDate = [System.TimeZoneInfo]::ConvertTimeFromUtc($contextUtc, $targetZone).ToString("yyyy-MM-dd")

    $actualIana = [string]$context.payload.timezone
    $actualDate = [string]$context.payload.current_date
    $systemAfter = [System.TimeZoneInfo]::Local.Id

    $brokerSuccess = $false
    $nativeLoaded = $false
    $injectionFailure = $null
    if (Test-Path -LiteralPath $logPath -PathType Leaf) {
        $logLines = @(Get-Content -LiteralPath $logPath -ErrorAction SilentlyContinue)
        $brokerSuccess = $null -ne ($logLines |
            Where-Object { $_ -match "role=broker injection=success" } |
            Select-Object -Last 1)
        $nativeLoaded = $null -ne ($logLines |
            Where-Object { $_ -match 'exe="[^"]*\\codex\.exe" target=loaded windows-id=' } |
            Select-Object -Last 1)
        $injectionFailure = $logLines |
            Where-Object { $_ -match "role=broker injection=failed" } |
            Select-Object -Last 1
    }

    $timezoneMatches = [string]::Equals(
        $actualIana,
        $expectedIana,
        [System.StringComparison]::OrdinalIgnoreCase
    )
    $dateMatches = $actualDate -eq $expectedDate
    $systemUnchanged = $systemAfter -eq $systemBefore

    Write-Host "Rollout           : $($selectedRollout.FullName)"
    Write-Host "Turn timestamp    : $($context.timestamp)"
    Write-Host "Expected IANA     : $expectedIana"
    Write-Host "Actual IANA       : $actualIana"
    Write-Host "Expected date     : $expectedDate"
    Write-Host "Actual date       : $actualDate"
    Write-Host "System zone before: $systemBefore"
    Write-Host "System zone after : $systemAfter"
    Write-Host "Broker injection  : $brokerSuccess"
    Write-Host "codex.exe shim    : $nativeLoaded"
    Write-Host "Shim log          : $logPath"

    if ($null -ne $injectionFailure) {
        Write-Warning "Native injection failure recorded: $injectionFailure"
        Write-Warning "PHASE0C_INCONCLUSIVE: the app-server did not receive the shim."
        exit 4
    }

    if (-not $brokerSuccess -or -not $nativeLoaded) {
        Write-Warning "PHASE0C_INCONCLUSIVE: rollout data exists, but the log does not prove that the shim reached codex.exe app-server."
        exit 4
    }

    if ($timezoneMatches -and $dateMatches -and $systemUnchanged) {
        Write-Host ""
        Write-Host "PHASE0C_PASS: native Codex turn_context uses the requested timezone and date while Windows remains unchanged." -ForegroundColor Green
        exit 0
    }

    Write-Host ""
    Write-Warning "PHASE0C_FAIL: the shim reached codex.exe, but one or more native context checks did not match."
    if (-not $timezoneMatches) {
        Write-Warning "iana-time-zone/Windows.Globalization.Calendar did not report '$expectedIana'."
    }
    if (-not $dateMatches) {
        Write-Warning "chrono::Local date did not match the expected date '$expectedDate'."
    }
    if (-not $systemUnchanged) {
        Write-Warning "Safety failure: the Windows timezone changed."
    }
    exit 5
}
catch {
    Write-Error $_
    exit 1
}
