# Codex Time Zone Launcher — Phase 1

This is the first production-shaped Windows 11 launcher built from the proven Phase 0B + Phase 0C mechanisms.

## Included in this milestone

- C# / .NET 10 WinForms launcher (`win-x64`).
- Dynamic discovery of the current-user `OpenAI.Codex` AppX package.
- Exact executable-path guard for an already-running Codex instance. The launcher never terminates Codex.
- IANA timezone selector with a saved last selection under `%LOCALAPPDATA%\CodexTimeZoneLauncher\settings.json`.
- Native `codex.exe app-server` isolation through `CodexTzBroker64.dll` + `CodexTzShim64.dll`.
- Python-free early Electron bootstrap implemented over .NET `ClientWebSocket` and the Chrome DevTools Protocol.
- Renderer verification before reporting success.
- Loopback-only Node inspector.
- No system timezone changes, registry timezone changes, `setx`, package modification, or persistent environment-variable changes.

## Current limitations

- Non-zero fixed `UTC+/-HH:mm` inputs are parsed but intentionally rejected at launch until the native shim can synthesize fixed-offset Windows timezone rules.
- The native helper DLLs are still shipped next to the single-file managed EXE. A later packaging milestone will embed/extract them so the user-facing distribution can become one EXE.
- Process-scoped HTTP/HTTPS/SOCKS5 proxy routing is tracked separately in issue #4. The intended design is a loopback HTTP CONNECT adapter shared by Electron and the native app-server.
- The `--inspect-brk` bootstrap has a best-effort resume path if CDP setup fails; a production watchdog is still desirable for crash/forced-exit resilience during the very short startup pause.

## Build

```powershell
dotnet publish .\src\CodexTimeZoneLauncher\CodexTimeZoneLauncher.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The GitHub Actions production workflow also builds the Microsoft Detours native helpers and packages a ready-to-test ZIP.
