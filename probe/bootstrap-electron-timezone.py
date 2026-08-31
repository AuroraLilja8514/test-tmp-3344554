#!/usr/bin/env python3
"""Install an early, in-memory timezone override in the Electron main process.

The target application is launched with Electron/Node's --inspect-brk switch so
its main JavaScript is paused before normal application code runs. This helper
attaches to that localhost inspector, installs an Electron `web-contents-created`
listener, and uses each WebContents' documented debugger API to send Chromium's
`Emulation.setTimezoneOverride` command.

No application files are modified. No Windows timezone setting is changed. The
listener and debugger attachments live only inside the launched Electron
process and disappear when that process exits.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass

try:
    import websocket  # type: ignore
except ImportError:
    print(
        "INCONCLUSIVE: Python package 'websocket-client' is not installed.\n"
        "Install it with: python -m pip install websocket-client",
        file=sys.stderr,
    )
    raise SystemExit(4)


def fetch_json(url: str, timeout: float = 1.5):
    with urllib.request.urlopen(url, timeout=timeout) as response:
        return json.load(response)


def wait_for_inspector_target(port: int, timeout: float) -> dict:
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        try:
            targets = fetch_json(f"http://127.0.0.1:{port}/json/list")
            if isinstance(targets, list):
                for target in targets:
                    if target.get("webSocketDebuggerUrl"):
                        return target
        except (OSError, urllib.error.URLError, json.JSONDecodeError) as exc:
            last_error = exc
        time.sleep(0.25)

    detail = f" ({last_error})" if last_error else ""
    raise RuntimeError(f"Electron main-process inspector did not become ready{detail}")


@dataclass
class InspectorClient:
    ws: object
    next_id: int = 1

    @classmethod
    def connect(cls, ws_url: str, timeout: float = 5.0) -> "InspectorClient":
        ws = websocket.create_connection(
            ws_url,
            timeout=timeout,
            suppress_origin=True,
        )
        return cls(ws=ws)

    def close(self) -> None:
        try:
            self.ws.close()
        except Exception:
            pass

    def command(self, method: str, params: dict | None = None, timeout: float = 8.0) -> dict:
        request_id = self.next_id
        self.next_id += 1
        payload: dict = {"id": request_id, "method": method}
        if params is not None:
            payload["params"] = params
        self.ws.send(json.dumps(payload))

        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            raw = self.ws.recv()
            message = json.loads(raw)
            if message.get("id") != request_id:
                continue
            if "error" in message:
                raise RuntimeError(f"{method} failed: {message['error']}")
            return message.get("result", {})
        raise RuntimeError(f"Timed out waiting for {method}")

    def evaluate(self, expression: str, *, await_promise: bool = True, timeout: float = 10.0):
        result = self.command(
            "Runtime.evaluate",
            {
                "expression": expression,
                "returnByValue": True,
                "awaitPromise": await_promise,
                "replMode": True,
            },
            timeout=timeout,
        )
        remote = result.get("result", {})
        if remote.get("subtype") == "error":
            raise RuntimeError(f"Runtime.evaluate returned an error object: {remote}")
        if result.get("exceptionDetails"):
            raise RuntimeError(f"Runtime.evaluate exception: {result['exceptionDetails']}")
        return remote.get("value")


def make_bootstrap_expression(requested: str) -> str:
    tz_literal = json.dumps(requested)
    return f"""
(() => {{
  const timezone = {tz_literal};
  const getRequire = () => {{
    if (typeof require === 'function') return require;
    if (process && typeof process.getBuiltinModule === 'function') {{
      const mod = process.getBuiltinModule('module');
      if (mod && typeof mod.createRequire === 'function') return mod.createRequire(process.execPath);
    }}
    throw new Error('Node require() is unavailable in the inspector context');
  }};
  const electron = getRequire()('electron');
  const app = electron.app;
  const webContents = electron.webContents;
  const state = globalThis.__codexTimezoneLauncher || {{
    timezone,
    applied: {{}},
    errors: [],
    retries: {{}},
  }};
  state.timezone = timezone;
  globalThis.__codexTimezoneLauncher = state;

  const recordError = (wc, stage, error) => {{
    state.errors.push({{
      id: wc && wc.id,
      stage,
      message: String(error && (error.stack || error.message) || error),
      at: Date.now(),
    }});
    if (state.errors.length > 100) state.errors.splice(0, state.errors.length - 100);
  }};

  const apply = async (wc) => {{
    if (!wc || wc.isDestroyed()) return false;
    try {{
      if (!wc.debugger.isAttached()) wc.debugger.attach('1.3');
      await wc.debugger.sendCommand('Emulation.setTimezoneOverride', {{ timezoneId: timezone }});
      state.applied[wc.id] = {{ url: wc.getURL(), at: Date.now() }};
      return true;
    }} catch (error) {{
      recordError(wc, 'apply', error);
      return false;
    }}
  }};

  const applyWithRetry = (wc, remaining = 30) => {{
    if (!wc || wc.isDestroyed() || remaining <= 0) return;
    Promise.resolve(apply(wc)).then((ok) => {{
      if (!ok && !wc.isDestroyed()) {{
        state.retries[wc.id] = (state.retries[wc.id] || 0) + 1;
        setTimeout(() => applyWithRetry(wc, remaining - 1), 100);
      }}
    }}).catch((error) => recordError(wc, 'retry', error));
  }};

  const installFor = (wc) => {{
    if (!wc || wc.__codexTimezoneLauncherInstalled) return;
    wc.__codexTimezoneLauncherInstalled = true;
    applyWithRetry(wc);
    wc.on('did-start-navigation', () => applyWithRetry(wc));
    wc.on('did-finish-load', () => applyWithRetry(wc));
    wc.on('render-process-gone', () => {{ delete state.applied[wc.id]; }});
  }};

  if (!state.listenerInstalled) {{
    state.listenerInstalled = true;
    app.on('web-contents-created', (_event, wc) => installFor(wc));
  }}
  for (const wc of webContents.getAllWebContents()) installFor(wc);

  state.inspect = async () => {{
    const output = [];
    for (const wc of webContents.getAllWebContents()) {{
      if (!wc || wc.isDestroyed()) continue;
      try {{
        const value = await wc.executeJavaScript(`(() => ({{
          timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
          offsetMinutes: new Date().getTimezoneOffset(),
          dateString: new Date().toString()
        }}))()`, true);
        output.push({{ id: wc.id, url: wc.getURL(), type: wc.getType(), value }});
      }} catch (error) {{
        output.push({{ id: wc.id, url: wc.getURL(), type: wc.getType(), error: String(error) }});
      }}
    }}
    return {{ timezone: state.timezone, applied: state.applied, errors: state.errors.slice(-10), output }};
  }};

  return {{ ok: true, timezone, listenerInstalled: state.listenerInstalled }};
}})()
"""


def normalize_zone(value: str) -> str:
    return value.strip().replace("_", "-").lower()


def resume_application(client: InspectorClient) -> None:
    # Depending on the Electron/Node version, --inspect-brk may be represented as
    # "waiting for debugger" or as an ordinary debugger pause. Try both forms.
    try:
        client.command("Runtime.runIfWaitingForDebugger", timeout=3.0)
    except Exception:
        pass
    try:
        client.command("Debugger.resume", timeout=3.0)
    except Exception:
        pass


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--requested", required=True)
    parser.add_argument("--timeout", type=float, default=25.0)
    args = parser.parse_args()

    try:
        target = wait_for_inspector_target(args.port, min(args.timeout, 12.0))
    except Exception as exc:
        print(f"EARLY_BOOTSTRAP_UNAVAILABLE: {exc}", file=sys.stderr)
        return 4

    client: InspectorClient | None = None
    resumed = False
    try:
        client = InspectorClient.connect(target["webSocketDebuggerUrl"])
        try:
            client.command("Runtime.enable")
        except Exception:
            pass
        try:
            client.command("Debugger.enable")
        except Exception:
            pass

        installed = client.evaluate(make_bootstrap_expression(args.requested), timeout=10.0)
        if not isinstance(installed, dict) or not installed.get("ok"):
            raise RuntimeError(f"unexpected bootstrap result: {installed!r}")

        print("Early Electron bootstrap installed before normal app startup.")
        print(f"Requested timezone: {args.requested}")

        resume_application(client)
        resumed = True

        requested_norm = normalize_zone(args.requested)
        deadline = time.monotonic() + args.timeout
        last_snapshot = None
        while time.monotonic() < deadline:
            time.sleep(0.5)
            try:
                snapshot = client.evaluate(
                    "globalThis.__codexTimezoneLauncher && globalThis.__codexTimezoneLauncher.inspect ? globalThis.__codexTimezoneLauncher.inspect() : null",
                    timeout=8.0,
                )
            except Exception:
                continue
            if not isinstance(snapshot, dict):
                continue
            last_snapshot = snapshot
            observations = snapshot.get("output") or []
            for item in observations:
                value = item.get("value") if isinstance(item, dict) else None
                if not isinstance(value, dict):
                    continue
                zone = value.get("timeZone")
                if isinstance(zone, str) and normalize_zone(zone) == requested_norm:
                    print("\nRenderer timezone observations")
                    print("==============================")
                    for observed in observations:
                        if not isinstance(observed, dict):
                            continue
                        print(f"WebContents {observed.get('id')}: {observed.get('type') or '<unknown>'}")
                        print(f"  URL           : {observed.get('url') or '<unknown>'}")
                        if isinstance(observed.get("value"), dict):
                            ov = observed["value"]
                            print(f"  Intl timezone : {ov.get('timeZone')}")
                            print(f"  Offset minutes: {ov.get('offsetMinutes')}")
                            print(f"  Local date    : {ov.get('dateString')}")
                        else:
                            print(f"  Error         : {observed.get('error')}")
                    print(
                        f"\nCHROMIUM_OVERRIDE_PASS: renderer reports '{args.requested}' "
                        "while the override is maintained internally by Electron."
                    )
                    return 0

        print("\nCHROMIUM_OVERRIDE_FAIL: bootstrap ran, but no renderer confirmed the requested timezone.")
        if isinstance(last_snapshot, dict):
            print(json.dumps(last_snapshot, indent=2, ensure_ascii=False))
        return 5
    except Exception as exc:
        print(f"EARLY_BOOTSTRAP_FAIL: {exc}", file=sys.stderr)
        return 5
    finally:
        if client is not None:
            if not resumed:
                # Never intentionally leave Codex suspended merely because our
                # diagnostic injection failed after attaching to the inspector.
                resume_application(client)
            client.close()


if __name__ == "__main__":
    raise SystemExit(main())
