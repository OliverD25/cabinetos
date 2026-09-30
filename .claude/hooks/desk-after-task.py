"""After a unit of work has landed, ask the session for one short look at the desk.

The creator asked on 2026-09-30: check the Notion desk for new cards after each
task is done; if a new card can be worked on and nothing blocks it, say so and
start it. A skill has to be invoked; a hook fires by itself. This is a Stop
hook: it runs each time the session ends a turn, and it hands the session one
instruction (the `reason` of a "block" decision) when, and only when, all of
these hold:

  * the project has a desk (`.claude/notion-desk.json` in the working folder);
  * the session is not already acting on a Stop hook (`stop_hook_active`),
    which is what keeps this from looping forever;
  * at least one commit landed on the current branch since the last check,
    which is the honest sign of a finished unit of work (the creator's rule is
    to commit per unit), so a turn that only answered a question stays quiet;
  * the last check is older than MIN_MINUTES.

The first Stop of a session only records the time; the check starts from the
next unit of work. Everything else exits 0 with no output, which lets the
session stop as usual. State lives in one small file per session in %TEMP%.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
from datetime import datetime, timedelta, timezone

MIN_MINUTES = 20


def state_path(session_id: str) -> str:
    safe = "".join(c for c in session_id if c.isalnum() or c in "-_")[:64] or "unknown"
    return os.path.join(tempfile.gettempdir(), f"claude-desk-after-task-{safe}.json")


def commits_since(cwd: str, since: datetime) -> int:
    try:
        out = subprocess.run(
            ["git", "log", "--oneline", f"--since={since.isoformat()}", "HEAD"],
            cwd=cwd, capture_output=True, text=True, timeout=8,
        )
    except (OSError, subprocess.SubprocessError):
        return 0
    if out.returncode != 0:
        return 0
    return len([line for line in out.stdout.splitlines() if line.strip()])


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except (ValueError, OSError):
        return 0
    if payload.get("stop_hook_active"):
        return 0
    cwd = payload.get("cwd") or os.getcwd()
    if not os.path.isfile(os.path.join(cwd, ".claude", "notion-desk.json")):
        return 0
    path = state_path(str(payload.get("session_id", "")))
    now = datetime.now(timezone.utc)
    try:
        with open(path, encoding="utf-8") as f:
            last = datetime.fromisoformat(json.load(f)["last_check"])
    except (OSError, ValueError, KeyError, TypeError):
        last = None
    if last is None:
        # The session just started: nothing has landed yet that the desk could follow.
        with open(path, "w", encoding="utf-8") as f:
            json.dump({"last_check": now.isoformat()}, f)
        return 0
    if now - last < timedelta(minutes=MIN_MINUTES):
        return 0
    if commits_since(cwd, last) == 0:
        return 0
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"last_check": now.isoformat()}, f)
    since = last.strftime("%Y-%m-%dT%H:%M:%SZ")
    reason = (
        "Desk check after a finished unit of work (the project's Stop hook; commits landed since "
        f"{since}). Do one short check, nothing more: one query of the desk's data source in rows "
        "mode (the binding in .claude/notion-desk.json), Status is Inbox or TODO, Created time after "
        f"{since}, limit 10. If nothing new: reply with the one line 'Desk: no new cards.' and stop. "
        "If a new card exists, read it once. If it can be worked on now (a clear brief, nothing it "
        "waits for, no hard stop of away mode, no coder already on the same files), tell the user in "
        "one or two lines which card you start and why, then work it by the desk-check skill (first "
        "pass on the card, plan questions only when the user is present, otherwise decide and record). "
        "If it cannot be worked on, say in one line what blocks it and stop."
    )
    print(json.dumps({"decision": "block", "reason": reason}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
