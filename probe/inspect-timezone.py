#!/usr/bin/env python3
"""Read Codex renderer timezone through the Chrome DevTools Protocol.

This helper is intentionally read-only with respect to timezone state. It uses
Runtime.evaluate and never calls Emulation.setTimezoneOverride.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request

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


def wait_for_targets(port: int, timeout: float) -> list[dict]:
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        try:
            targets = fetch_json(f"http://127.0.0.1:{port}/json/list")
            if isinstance(targets, list) and targets:
                return targets
        except (OSError, urllib.error.URLError, json.JSONDecodeError) as exc:
            last_error = exc
        time.sleep(0.5)

    detail = f" ({last_error})" if last_error else ""
    raise RuntimeError(f"DevTools endpoint did not become ready{detail}")


def evaluate_timezone(ws_url: str, timeout: float = 3.0) -> dict:
    # Chromium may validate the websocket Origin header. suppress_origin avoids
    # injecting a browser-like Origin into this local diagnostic connection.
    ws = websocket.create_connection(ws_url, timeout=timeout, suppress_origin=True)
    try:
        request_id = 1
        expression = """(() => ({
            timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
            offsetMinutes: new Date().getTimezoneOffset(),
            dateString: new Date().toString()
        }))()"""
        ws.send(
            json.dumps(
                {
                    "id": request_id,
                    "method": "Runtime.evaluate",
                    "params": {
                        "expression": expression,
                        "returnByValue": True,
                        "awaitPromise": True,
                    },
                }
            )
        )

        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            message = json.loads(ws.recv())
            if message.get("id") != request_id:
                continue
            if "error" in message:
                raise RuntimeError(f"CDP error: {message['error']}")
            result = (
                message.get("result", {})
                .get("result", {})
                .get("value")
            )
            if not isinstance(result, dict):
                raise RuntimeError(f"Unexpected Runtime.evaluate result: {message}")
            return result
        raise RuntimeError("Timed out waiting for Runtime.evaluate result")
    finally:
        ws.close()


def normalize_zone(value: str) -> str:
    return value.strip().replace("_", "-").lower()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--requested", required=True)
    parser.add_argument("--timeout", type=float, default=20.0)
    args = parser.parse_args()

    try:
        targets = wait_for_targets(args.port, args.timeout)
    except Exception as exc:
        print(f"INCONCLUSIVE: {exc}", file=sys.stderr)
        return 4

    page_targets = [
        target
        for target in targets
        if target.get("type") in {"page", "webview"}
        and target.get("webSocketDebuggerUrl")
    ]
    if not page_targets:
        print("INCONCLUSIVE: DevTools is reachable but no inspectable page/webview target was found.")
        return 4

    observed: list[tuple[dict, dict]] = []
    errors: list[str] = []
    for target in page_targets:
        try:
            value = evaluate_timezone(target["webSocketDebuggerUrl"])
            observed.append((target, value))
        except Exception as exc:
            errors.append(f"{target.get('title', '<untitled>')}: {exc}")

    if not observed:
        print("INCONCLUSIVE: no renderer target could be evaluated.")
        for error in errors:
            print(f"  {error}")
        return 4

    print("\nRenderer timezone observations")
    print("==============================")
    for index, (target, value) in enumerate(observed, start=1):
        print(f"Target {index}: {target.get('title') or '<untitled>'}")
        print(f"  URL           : {target.get('url') or '<unknown>'}")
        print(f"  Intl timezone : {value.get('timeZone')}")
        print(f"  Offset minutes: {value.get('offsetMinutes')}")
        print(f"  Local date    : {value.get('dateString')}")

    requested = normalize_zone(args.requested)
    exact_matches = [
        value
        for _, value in observed
        if isinstance(value.get("timeZone"), str)
        and normalize_zone(value["timeZone"]) == requested
    ]

    if exact_matches:
        print(f"\nPASS: at least one renderer reports the requested timezone '{args.requested}'.")
        return 0

    print(
        f"\nFAIL: renderer inspection worked, but no renderer reported the requested "
        f"timezone '{args.requested}'."
    )
    print(
        "Note: an IANA alias can theoretically represent equivalent rules under a different "
        "canonical name. For the first probe, use Pacific/Honolulu to make the result unambiguous."
    )
    return 5


if __name__ == "__main__":
    raise SystemExit(main())
