# Codex Time Zone Launcher

Windows 11 launcher project for starting the Codex/ChatGPT desktop application with a time zone that applies only to the launched Codex process tree.

This repository is currently in **Phase 0: feasibility probes**. The first probe established that a child-only `TZ` environment variable reaches the launched process but is **not sufficient for Chromium on Windows**. The second probe tests a Chromium-native override installed before normal Electron application startup.

## Confirmed Phase 0A result

Tested against Codex desktop package `OpenAI.Codex_26.825.6671.0_x64__2p2nqsd0c76g0`:

- AppX discovery: PASS
- Dynamic `app\ChatGPT.exe` path resolution: PASS
- Full-path running-process guard: PASS
- Parent/global environment isolation: PASS
- Child `TZ=Pacific/Honolulu`: inherited by the child process
- Chromium renderer: **FAIL** — renderer still reported `Asia/Shanghai` / UTC+08

Therefore the production design will not rely on `TZ` alone.

## Phase 0B: early Electron renderer override

Electron exposes a main-process Node inspector through `--inspect-brk`. The Phase 0B probe pauses the Electron main JavaScript before normal app code runs, attaches only through a loopback inspector port, and installs an in-memory `web-contents-created` listener. For every WebContents it uses Electron's debugger API to send Chromium's native:

`Emulation.setTimezoneOverride`

The listener remains inside the launched Electron main process and re-applies the override for newly created/reloaded WebContents. No Codex files are modified and no Windows timezone setting is changed.

### Run Phase 0B

Close Codex normally first. From the repository root:

```powershell
python -m pip install -r .\requirements-probe.txt
powershell -ExecutionPolicy Bypass -File .\probe\launch-codex-early.ps1 Pacific/Honolulu
```

If your Python executable is different:

```powershell
powershell -ExecutionPolicy Bypass -File .\probe\launch-codex-early.ps1 Pacific/Honolulu -Python "C:\path\to\python.exe"
```

Expected success marker:

```text
CHROMIUM_OVERRIDE_PASS: renderer reports 'Pacific/Honolulu' while the override is maintained internally by Electron.
```

Possible outcomes:

- **CHROMIUM_OVERRIDE_PASS** — the Chromium/UI half has a viable isolated-timezone mechanism.
- **CHROMIUM_OVERRIDE_FAIL** — Electron debugging was available, but the requested renderer timezone could not be established.
- **EARLY_BOOTSTRAP_UNAVAILABLE** — the packaged Electron build did not expose the main-process inspector (for example because a production Electron fuse disables Node CLI inspector arguments). This is inconclusive for other mechanisms.

The helper always attempts to resume Codex before detaching if it managed to attach to an `--inspect-brk` session, even when the injection fails. The launcher never terminates Codex automatically.

## Native Codex app-server finding

The Chromium renderer is only one part of the requirement. Current open-source Codex core builds a per-turn environment context using a native Rust `local_time_context()` function. It obtains the IANA zone with `iana_time_zone::get_timezone()` and the local date with `chrono::Local::now()`.

On Windows those libraries use Windows timezone facilities rather than the `TZ` environment variable. Consequently, even a successful Phase 0B result proves only the Chromium/UI half. Phase 0C will test a process-local Windows timezone API shim for the native `codex.exe app-server` side.

The intended production architecture remains:

1. Dynamically locate the current user's `OpenAI.Codex` AppX package and `app\ChatGPT.exe`.
2. Refuse to launch if that exact Codex executable is already running; never terminate it.
3. Read the saved timezone once at launcher startup.
4. Launch Codex only when the user clicks **Launch Codex**.
5. Apply the selected timezone only inside the Codex process tree.
6. Keep Windows and unrelated software on the normal system timezone.
7. Ship as a Windows-only C#/.NET self-contained single-file launcher under the MIT license.

## Original Phase 0A probe

The original test is retained for diagnostics:

```powershell
powershell -ExecutionPolicy Bypass -File .\probe\launch-codex.ps1 Pacific/Honolulu
```

It dynamically discovers `OpenAI.Codex`, resolves `app\ChatGPT.exe`, applies child-only `TZ`, and reads Chromium timezone values using a temporary localhost DevTools port. It deliberately performs no timezone emulation. Its failure on the tested Windows build is now an expected diagnostic result.

## Safety behavior

All probes intentionally avoid:

- `Stop-Process`, `taskkill`, `TerminateProcess`, or equivalent;
- changing the Windows system timezone;
- `setx` or persistent user/system environment changes;
- hard-coded Codex versions, architectures, WindowsApps roots, or installation drives;
- modifying files inside the signed Store package.

If the exact discovered Codex executable is already running, a probe exits and asks you to close it manually.

## Planned production work

After the renderer and native-process mechanisms are proven, the production application will be Windows-only C#/.NET (WinForms) with:

- manifest/AppX-based executable discovery;
- full-path process protection;
- searchable IANA timezone selection and supported fixed UTC-offset input;
- saved last-selected timezone as the next default;
- one **Launch Codex** button;
- a process-tree-only timezone implementation;
- GitHub Actions build/test/release pipelines;
- a self-contained single-file `win-x64` executable (with optional ARM64 later).

## CI limitations

GitHub Actions validates probe syntax, but hosted runners do not have an authenticated Windows Store Codex installation. End-to-end AppX/Electron behavior must therefore be tested on a Windows 11 machine with Codex installed.

## License

MIT
