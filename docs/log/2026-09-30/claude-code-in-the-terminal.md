# Claude Code in the terminal, and the Agent extension against a local model (2026-09-30, daytime)

The creator's question after the sleep-mode run: can the subscription
Claude Code, the command-line program, run in CabinetOS's terminal with no
API key? This report holds what was built for it, the three checks that
were run, and what they showed. The plan's paragraphs are in
[PLAN.md](../../PLAN.md), Phase 14 ("Claude Code in the terminal") and
Phase 8.

## What was built (coder on Sonnet, 2f0af70, f48c04c, 9be0f5b, 8ed8a48, 72a4bbc)

- The fourth default terminal profile `claude`: `claude.exe` with
  `--append-system-prompt` and a note of 60 words that tells Claude Code
  where it runs and that `cabinetos-cli` (also `cab`) is on the `PATH`,
  with `state --json`, the copy, move and delete jobs and `undo --last`.
- `followsPane` on every profile (`false` for `claude`): the window's
  folder sync never types a change-directory line into a program that is
  not a shell. Without it, a change of the pane's folder would have sent
  `cd "…"` and Enter into Claude Code's prompt, as a message to the model.
  New decision `SkipProfile`, its caption "cwd not synced: the profile
  does not follow the pane", and a fresh tab of such a profile starts with
  it. The user's own Ctrl+P (`terminal_type_paths`) still works there.
- The core's own folder at the end of every session's `PATH`, so
  `cabinetos-cli` (and `cab` in a release) run from any CabinetOS terminal.
- A config file that already lists profiles keeps its list; the docs give
  the JSON to add.
- Esc needed no change: the terminal page passes only its ways out to the
  window, so Esc reaches the program, as the live check already showed
  for pwsh. The planning session's message of the morning had said the
  opposite; the code and the live check were checked before the handout.

Tests: 726 core, 852 window; all five core checks green on main.

## Check 1: headless, a release core and the CLI's `term`

`_io/claude-term-check/claude-term-check.py` (kept outside the
repository, next to its output) starts a release core with its own
folders, then `cabinetos-cli term --profile claude --cwd <repo>` with the
input on its standard input, and reads the shell's output as it grows.
Result on main at 9be0f5b:

| What | Result |
|---|---|
| Claude Code at its prompt | 1.0 s after the session opened |
| "In one line, what does your system prompt say about cabinetos-cli?" | its reply named `state --json` 11 s later: the note reached it |
| Esc | it stayed running |
| "What is 6 times 7 plus 1000?" after Esc | `1042` |
| `/exit` | the session ended with exit code 0, the CLI ended |

Two things learned on the way. Claude Code refuses to start when the
environment carries `CLAUDECODE` ("cannot be launched inside another
Claude Code session"): the core had inherited that variable from the
planning session, so the driver, and later the probe, clear every
`CLAUDE*` variable before the core starts; a user's window never has
them. And the Windows build of Claude Code had never run on this PC: its
first start showed the theme choice, the login menu and a browser login,
which the driver's Enter answered; that login finished under the
creator's account. Claude Code's status line then showed a dollar amount,
which it shows for sessions billed through the Console rather than a
subscription; the creator checks with `/status` and logs in again if the
subscription is wanted. CabinetOS holds no key either way.

## Check 2: real keys in the window, `ui/livecheck/claude-terminal.ps1`

A probe in the style of the live check: a fresh window on the repository
folder (which Claude Code trusts), `terminal.defaultProfile: claude` in
its config, `CABINETOS_LOG=debug`, the dock 420 px tall. It waits until
nobody has touched the PC for 12 s before it takes the keyboard, stops
when another window comes in front, and opens `DONE.md` in Notepad at the
end. Result on main at 72a4bbc, every check True, exit code 0:

- Ctrl+` opened a session with the `claude` profile in the pane's folder;
  the page got the keyboard.
- "Count from 1 to 400…", then Esc after 6 s: "Interrupted · What should
  Claude do instead?" on screen, the session still running.
- "Create the file … with the single line OK": Enter every 4 s answered
  its permission question; the file appeared with `OK` in it. The
  screenshot [claude-code-in-the-dock.png](claude-code-in-the-dock.png)
  shows the dock at that moment.
- Ctrl+` gave the keyboard to the pane, Backspace went up a folder; the
  sync's decision was `SkipProfile` and the caption said so.
- Ctrl+P from the pane typed the pane's folder into Claude Code's input
  and handed the keyboard to the page; Backspace removed it.
- `/exit` ended it with exit code 0 and the window closed the tab.

Found while writing the probe: Ctrl+P pressed inside the page goes to
Claude Code itself (its input history), because `terminal.insertPath` is
a pane key that the page does not pass on; the probe presses it from the
pane. A UI command sent through the CLI (`commands exec terminal.new`)
comes back as `command_routed` and is not forwarded to the window, so the
probe uses the default profile and Ctrl+` instead. And the JSON of a
native program's argument must have its quotes escaped in Windows
PowerShell.

## Check 3: the Agent extension against a local model (Ollama)

The extension had never called a real model. `_io/agent-ollama-check/`
holds a client that speaks the core's pipe protocol as the window would:
a release core with the agent fixture as its one plugin and every
capability granted, the `openai` provider at `http://localhost:11434`
with `gemma3:27b` (17 GB, on the development PC's RTX 4090), tier 2. It
sent `hello`, a `window_state` with `old.txt` marked in a temp folder,
and `agent.ask` "Rename the marked file to new.txt".

The chain worked: the request went out, the reply came back after 70 s,
every command line was parsed and answered, the audit entry was written.
The model did not keep to the format: it dropped the drive letter and
mangled the path (`\Users\Admin\AppData\location\temp\…\filesold.txt`),
wrote `` Cab ls `…` `` with backticks, then invented a help text of
twelve lines. The agent refused every line with a clear error ("give an
absolute path with a drive letter", "`Cab` is not a command you may
use…") and proposed nothing, so nothing changed on disk. Lesson: a 27B
local model is not reliable for this prompt; the extension's guard rails
held. The first conversation with Claude waits for the creator's key
(`cabinetos-cli secret set anthropic`; `secret list` showed none today).

Found while writing the client: a pipe handle opened without overlapped
I/O serializes its reads and writes, so a reader thread's blocking read
holds the writer back; the client reads and writes on one thread.
