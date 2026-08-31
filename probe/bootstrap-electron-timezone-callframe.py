#!/usr/bin/env python3
"""Install and verify an early Chromium timezone override in Codex Electron.

This version deliberately uses Debugger.evaluateOnCallFrame while --inspect-brk
has the Electron main VM paused. Runtime.evaluate targets the global execution
context and can remain unanswered while V8 is stopped at a debugger pause.

The injected bootstrap only installs listeners and schedules work. It does not
call webContents.debugger.sendCommand until after the main process is resumed.
No application files or Windows timezone settings are modified.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field

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
    events: list[dict] = field(default_factory=list)

    @classmethod
    def connect(cls, ws_url: str, timeout: float = 5.0) -> "InspectorClient":
        ws = websocket.create_connection(ws_url, timeout=timeout, suppress_origin=True)
        return cls(ws=ws)

    def close(self) -> None:
        try:
            self.ws.close()
        except Exception:
            pass

    def _remember_event(self, message: dict) -> None:
        self.events.append(message)
        if len(self.events) > 250:
            del self.events[: len(self.events) - 250]

    def _recv_message(self, deadline: float) -> dict | None:
        while time.monotonic() < deadline:
            remaining = deadline - time.monotonic()
            try:
                self.ws.settimeout(min(1.0, max(0.1, remaining)))
                raw = self.ws.recv()
            except websocket.WebSocketTimeoutException:
                continue
            if raw is None or raw == "":
                raise RuntimeError("Electron inspector connection closed")
            try:
                message = json.loads(raw)
            except json.JSONDecodeError:
                continue
            if isinstance(message, dict):
                return message
        return None

    def command(self, method: str, params: dict | None = None, timeout: float = 8.0) -> dict:
        request_id = self.next_id
        self.next_id += 1
        payload: dict = {"id": request_id, "method": method}
        if params is not None:
            payload["params"] = params
        self.ws.send(json.dumps(payload))

        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            message = self._recv_message(deadline)
            if message is None:
                break
            if message.get("id") != request_id:
                self._remember_event(message)
                continue
            if "error" in message:
                raise RuntimeError(f"{method} failed: {message['error']}")
            return message.get("result", {})
        raise RuntimeError(f"Timed out waiting for {method}")

    def wait_for_event(self, method: str, timeout: float = 10.0) -> dict:
        for index, message in enumerate(self.events):
            if message.get("method") == method:
                return self.events.pop(index)
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            message = self._recv_message(deadline)
            if message is None:
                break
            if message.get("method") == method:
                return message
            self._remember_event(message)
        raise RuntimeError(f"Timed out waiting for inspector event {method}")

    @staticmethod
    def _remote_value(result: dict, method: str):
        remote = result.get("result", {})
        if result.get("exceptionDetails"):
            raise RuntimeError(f"{method} exception: {result['exceptionDetails']}")
        if remote.get("subtype") == "error":
            raise RuntimeError(f"{method} returned an error object: {remote}")
        return remote.get("value")

    def evaluate_global(self, expression: str, timeout: float = 10.0):
        result = self.command(
            "Runtime.evaluate",
            {
                "expression": expression,
                "returnByValue": True,
                "awaitPromise": True,
            },
            timeout=timeout,
        )
        return self._remote_value(result, "Runtime.evaluate")

    def evaluate_on_frame(self, call_frame_id: str, expression: str, timeout: float = 10.0):
        result = self.command(
            "Debugger.evaluateOnCallFrame",
            {
                "callFrameId": call_frame_id,
                "expression": expression,
                "returnByValue": True,
                "silent": False,
            },
            timeout=timeout,
        )
        return self._remote_value(result, "Debugger.evaluateOnCallFrame")


def make_bootstrap_expression(requested: str) -> str:
    tz_literal = json.dumps(requested)
    return f"""
(() => {{
  const timezone = {tz_literal};
  const getRequire = () => {{
    if (typeof require === 'function') return require;
    if (typeof process !== 'undefined' && typeof process.getBuiltinModule === 'function') {{
      const mod = process.getBuiltinModule('module');
      if (mod && typeof mod.createRequire === 'function') return mod.createRequire(process.execPath);
    }}
    throw new Error('Node require() is unavailable in the selected paused call frame');
  }};

  const electron = getRequire()('electron');
  const app = electron.app;
  const webContents = electron.webContents;
  const state = globalThis.__codexTimezoneLauncher || {{ applied: {{}}, errors: [], retries: {{}} }};
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

  const applyWithRetry = (wc, remaining = 120) => {{
    if (!wc || wc.isDestroyed() || remaining <= 0) return;
    Promise.resolve(apply(wc)).then((ok) => {{
      if (!ok && !wc.isDestroyed()) {{
        state.retries[wc.id] = (state.retries[wc.id] || 0) + 1;
        setTimeout(() => applyWithRetry(wc, remaining - 1), 100);
      }}
    }}).catch((error) => recordError(wc, 'retry', error));
  }};

  const kick = (wc) => setImmediate(() => applyWithRetry(wc));

  const installFor = (wc) => {{
    if (!wc || wc.isDestroyed() || wc.__codexTimezoneLauncherInstalled) return;
    wc.__codexTimezoneLauncherInstalled = true;
    kick(wc);
    wc.on('did-start-navigation', () => kick(wc));
    wc.on('did-finish-load', () => kick(wc));
    wc.on('render-process-gone', () => {{ delete state.applied[wc.id]; }});
  }};

  if (!state.listenerInstalled) {{
    state.listenerInstalled = true;
    app.on('web-contents-created', (_event, wc) => installFor(wc));
  }}

  // Important: this bootstrap is installed while the Electron main VM is
  // debugger-paused. Do not perform asynchronous debugger commands until the
  // VM resumes. setImmediate defers the initial sweep until normal execution.
  setImmediate(() => {{
    try {{
      for (const wc of webContents.getAllWebContents()) installFor(wc);
    }} catch (error) {{
      recordError(null, 'initial-sweep', error);
    }}
  }});

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
    return {{ timezone: state.timezone, applied: state.applied, errors: state.errors.slice(-20), output }};
  }};

  return {{ ok: true, timezone, listenerInstalled: state.listenerInstalled }};
}})()
"""


def normalize_zone(value: str) -> str:
    return value.strip().replace("_", "-").lower()


def resume_application(client: InspectorClient) -> None:
    try:
        client.command("Runtime.runIfWaitingForDebugger", timeout=3.0)
    except Exception:
        pass
    try:
        client.command("Debugger.resume", timeout=3.0)
    except Exception:
        pass


def enter_inspect_brk_pause(client: InspectorClient, timeout: float) -> dict:
    client.command("Runtime.enable", timeout=min(5.0, timeout))
    client.command("Debugger.enable", timeout=min(5.0, timeout))
    client.command("Runtime.runIfWaitingForDebugger", timeout=min(10.0, timeout))
    return client.wait_for_event("Debugger.paused", timeout=min(20.0, timeout))


def choose_injection_frame(client: InspectorClient, paused: dict) -> tuple[str, dict]:
    frames = (paused.get("params") or {}).get("callFrames") or []
    if not frames:
        raise RuntimeError("Debugger.paused did not include a call frame")

    capability_expression = """
(() => ({
  hasProcess: typeof process !== 'undefined',
  hasRequire: typeof require === 'function',
  hasBuiltinModule: typeof process !== 'undefined' && typeof process.getBuiltinModule === 'function'
}))()
"""
    diagnostics: list[str] = []
    for frame in frames:
        frame_id = frame.get("callFrameId")
        if not frame_id:
            continue
        try:
            caps = client.evaluate_on_frame(frame_id, capability_expression, timeout=5.0)
        except Exception as exc:
            diagnostics.append(f"{frame.get('functionName') or '<anonymous>'}: {exc}")
            continue
        if isinstance(caps, dict) and caps.get("hasProcess") and (
            caps.get("hasRequire") or caps.get("hasBuiltinModule")
        ):
            return frame_id, frame
        diagnostics.append(
            f"{frame.get('functionName') or '<anonymous>'}: capabilities={caps!r}"
        )
    raise RuntimeError(
        "No paused call frame exposed a usable Node context. " + " | ".join(diagnostics[-5:])
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--requested", required=True)
    parser.add_argument("--timeout", type=float, default=60.0)
    args = parser.parse_args()

    print(f"Waiting for Electron main inspector on 127.0.0.1:{args.port} ...")
    try:
        target = wait_for_inspector_target(args.port, args.timeout)
    except Exception as exc:
        print(f"EARLY_BOOTSTRAP_UNAVAILABLE: {exc}", file=sys.stderr)
        return 4

    print("Electron main inspector is reachable.")
    client: InspectorClient | None = None
    resumed = False

    try:
        client = InspectorClient.connect(target["webSocketDebuggerUrl"])
        print("Debugger connected. Advancing to the --inspect-brk startup pause ...")
        paused = enter_inspect_brk_pause(client, args.timeout)
        reason = (paused.get("params") or {}).get("reason")
        print(f"Electron main JavaScript is paused before normal startup (reason: {reason or 'unknown'}).")

        frame_id, frame = choose_injection_frame(client, paused)
        location = frame.get("location") or {}
        print(
            "Selected paused Node call frame: "
            f"{frame.get('functionName') or '<anonymous>'} "
            f"(scriptId={location.get('scriptId', '?')}, line={location.get('lineNumber', '?')})"
        )
        print("Installing renderer timezone listener through Debugger.evaluateOnCallFrame ...")

        installed = client.evaluate_on_frame(
            frame_id,
            make_bootstrap_expression(args.requested),
            timeout=15.0,
        )
        if not isinstance(installed, dict) or not installed.get("ok"):
            raise RuntimeError(f"unexpected bootstrap result: {installed!r}")

        print("Early Electron bootstrap installed while startup is paused.")
        print(f"Requested timezone: {args.requested}")
        print("Resuming Codex startup; renderer debugger work begins only after this point ...")
        client.command("Debugger.resume", timeout=8.0)
        resumed = True

        requested_norm = normalize_zone(args.requested)
        deadline = time.monotonic() + args.timeout
        last_snapshot = None
        while time.monotonic() < deadline:
            time.sleep(0.5)
            try:
                snapshot = client.evaluate_global(
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
                            value = observed["value"]
                            print(f"  Intl timezone : {value.get('timeZone')}")
                            print(f"  Offset minutes: {value.get('offsetMinutes')}")
                            print(f"  Local date    : {value.get('dateString')}")
                        else:
                            print(f"  Error         : {observed.get('error')}")
                    print(
                        f"\nCHROMIUM_OVERRIDE_PASS: renderer reports '{args.requested}' "
                        "while the override is maintained by the Electron main process."
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
                resume_application(client)
            client.close()


if __name__ == "__main__":
    raise SystemExit(main())
