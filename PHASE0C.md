# Phase 0C: native Codex app-server timezone probe

## Why a second mechanism is necessary

The Chromium renderer probe changes `Date` and `Intl` inside Electron, but Codex's native turn context is built independently. Current Codex calls:

- `iana_time_zone::get_timezone()` for the IANA identifier; on Windows that crate calls `Windows.Globalization.Calendar.GetTimeZone()`.
- `chrono::Local::now()` for the local date; Chrono's Windows backend obtains DST and offset rules through `GetTimeZoneInformationForYear(..., nullptr, ...)`.

A child-only `TZ` variable does not change either Windows-native path.

## Proposed solution

The Phase 0C probe uses two small Microsoft Detours DLLs with separate roles:

1. `CodexTzBroker64.dll` is loaded into `ChatGPT.exe` as a **broker only**. It does not change ChatGPT/Electron's Win32 timezone. It intercepts process creation only long enough to recognize a `codex.exe ... app-server` child.
2. The broker inserts `CodexTzShim64.dll` into that app-server before its entry point. Inside `codex.exe`, the timezone shim intercepts the Windows APIs used by Chrono and by `Windows.Globalization.Calendar` and returns the selected zone's rules.

Other Chromium children are not injected by the broker. The working Phase 0B Electron/CDP mechanism remains responsible for the renderer in the final combined launcher.

Everything is process-local and in memory. The probe does not call `SetTimeZoneInformation`, does not change the registry, does not persist environment variables, and does not modify the signed Codex package.

## Run the Desktop integration test

This package is experimental. Close Codex normally first, then run from the extracted Phase 0C artifact folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\launch-codex-phase0c.ps1 Pacific/Honolulu
```

For an IANA zone not included in the small probe mapping, also provide its Windows ID:

```powershell
powershell -ExecutionPolicy Bypass -File .\launch-codex-phase0c.ps1 `
  America/Denver `
  -WindowsTimeZoneId "Mountain Standard Time"
```

The script dynamically locates the current `OpenAI.Codex` AppX package and `app\ChatGPT.exe`, checks that Codex is not already running, validates the native files and Windows timezone ID, and then launches Codex. It never terminates Codex.

When the script says that the native app-server injection was observed, create a **new** Codex conversation and send one simple turn, for example:

```text
Reply only with OK.
```

Then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\inspect-codex-phase0c.ps1
```

The inspector reads only the latest `turn_context` metadata from the rollout JSONL. It compares:

- `payload.timezone` with the requested IANA ID;
- `payload.current_date` with the date calculated from the turn timestamp and selected Windows zone;
- the Windows system timezone before and after;
- the shim log proving that the broker reached `codex.exe app-server`.

A complete success ends with:

```text
PHASE0C_PASS: native Codex turn_context uses the requested timezone and date while Windows remains unchanged.
```

## Interpretation

- `PHASE0C_PASS`: the native app-server mechanism is viable.
- `PHASE0C_FAIL`: the DLL reached `codex.exe`, but the native timezone or date remained wrong. The shim log and rollout values identify which Windows path still needs interception.
- `PHASE0C_INCONCLUSIVE`: the app-server was not observed/injected, or no new turn context was available. This usually means process-spawn discovery or test timing needs adjustment rather than that the API override is impossible.

## Remaining production work after a pass

A production launcher should combine the native broker with the already-proven Chromium override, replace the Python bootstrap with C# CDP code, map the full IANA catalog using modern .NET, inspect process mitigation policies, embed/extract the native helpers internally, and add signed release artifacts when appropriate.
