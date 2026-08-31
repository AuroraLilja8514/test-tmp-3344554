#!/usr/bin/env python3
"""Legacy Phase-0B bootstrap helper.

This file is retained only to document the failed paused-global-evaluation
experiment. `launch-codex-early.ps1` no longer calls it.

Why it was superseded:
- Codex/Electron successfully reached the `--inspect-brk` Debugger.paused state.
- This helper then used `Runtime.evaluate`, which targets the global execution
  context rather than a paused call frame.
- On the tested Codex build that request remained unanswered until the helper
  failed and cleanup resumed Electron, which is why the Codex GUI appeared only
  after the script exited.

Use `bootstrap-electron-timezone-callframe.py`, which injects through
`Debugger.evaluateOnCallFrame` while paused and defers asynchronous renderer
work until after `Debugger.resume`.
"""

import sys

print(
    "This helper is superseded. Use probe/bootstrap-electron-timezone-callframe.py "
    "via probe/launch-codex-early.ps1.",
    file=sys.stderr,
)
raise SystemExit(4)
