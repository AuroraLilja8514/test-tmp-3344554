# Codex Time Zone Launcher

Windows 11 launcher project for starting the Codex/ChatGPT desktop application with a time zone that applies only to the launched Codex process tree.

This repository is currently in **Phase 0: feasibility probe**. The purpose of this phase is to prove that the current Windows Codex build actually honors a child-only `TZ` environment variable in its Chromium renderer before the full .NET launcher is built.

## Current test plan

The probe checks four things:

1. Dynamically locate the current user's `OpenAI.Codex` AppX package and resolve `app\ChatGPT.exe` without hard-coding a package version, architecture, WindowsApps directory, or drive.
2. Refuse to launch if that exact Codex executable is already running. The probe never kills Codex.
3. Launch Codex with a copied child environment in which only `TZ` is overridden. The parent PowerShell process and Windows user/system environment are not modified.
4. When possible, launch Chromium remote debugging on a temporary localhost port and read the renderer's actual values for:
   - `Intl.DateTimeFormat().resolvedOptions().timeZone`
   - `new Date().getTimezoneOffset()`
   - `new Date().toString()`

The renderer inspection uses `Runtime.evaluate` only. It does **not** call `Emulation.setTimezoneOverride`, because that would invalidate the test.

## Requirements for Phase 0

- Windows 11
- Codex desktop installed from the Windows package (`OpenAI.Codex`)
- Windows PowerShell 5.1 or PowerShell 7+
- Python 3 available from the command line
- `websocket-client`

Install the small Python dependency with:

```powershell
python -m pip install -r .\requirements-probe.txt
```

If your Conda environment is not active, activate it first or pass the desired Python executable using `-Python`.

## Run the first test

Close Codex normally before running the probe.

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\probe\launch-codex.ps1 Pacific/Honolulu
```

`Pacific/Honolulu` is deliberately recommended for the first test because its UTC-10 offset is easy to distinguish from common Windows time zones and it does not observe daylight-saving time.

If your Python command is different:

```powershell
powershell -ExecutionPolicy Bypass -File .\probe\launch-codex.ps1 Pacific/Honolulu -Python "C:\path\to\python.exe"
```

To test only package discovery and private-environment launching without renderer inspection:

```powershell
powershell -ExecutionPolicy Bypass -File .\probe\launch-codex.ps1 Pacific/Honolulu -NoInspect
```

## Expected results

A successful renderer test ends with output similar to:

```text
Renderer timezone observations
==============================
Target 1: ChatGPT
  Intl timezone : Pacific/Honolulu
  Offset minutes: 600
  Local date    : ... GMT-1000 ...

PASS: at least one renderer reports the requested timezone 'Pacific/Honolulu'.
```

Possible outcomes:

- **PASS** — renderer inspection worked and at least one Codex page/webview reports the requested IANA time zone.
- **FAIL** — renderer inspection worked, but Codex reports another time zone. This indicates that inherited `TZ` is not sufficient in the current desktop build (or an IANA alias needs further evaluation).
- **INCONCLUSIVE** — Codex launched, but the DevTools endpoint or renderer target could not be inspected. This is not treated as proof that `TZ` failed.

## Safety behavior

The probe intentionally does not:

- call `Stop-Process`, `taskkill`, `TerminateProcess`, or equivalent;
- modify the Windows system time zone;
- use `setx` or persist a `TZ` variable in user/system environment settings;
- use Chrome DevTools timezone emulation;
- assume a specific package version or WindowsApps root path.

If the exact discovered Codex executable is already running, the script exits and asks you to close it manually.

## Planned implementation after Phase 0

If the renderer test passes, the production application will be a Windows-only C#/.NET launcher, likely WinForms, with:

1. AppX-based Codex executable discovery.
2. Full-path process guard to protect running work.
3. A searchable IANA time-zone selector plus supported UTC fixed-offset input.
4. Saved last-selected time zone as the next default.
5. A **Launch Codex** button that starts the package executable with a private child environment.
6. Self-contained, single-file Windows release builds through GitHub Actions.
7. MIT licensing.

## CI limitations

GitHub Actions validates syntax and the Python helper, but hosted runners do not have an authenticated Windows Store Codex installation. The actual AppX launch and renderer time-zone check therefore must be run on a Windows 11 machine with Codex installed.

## License

MIT
