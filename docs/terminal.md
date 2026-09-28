# The integrated terminal

The core runs shells for the terminal pane: pwsh, cmd, WSL, or any program
in a profile. Each shell runs in a pseudo-console (ConPTY, the Windows API
that gives a program a console whose screen goes to a pipe instead of a
window). The pane shows what the shell prints and sends what the user types.
Nothing runs until a client asks: the terminal is hidden by default
(Constitution Article 4; [ADR 0005](decisions/0005-terminal-in-core-hidden.md)).
It follows the active pane's folder when the client asks (Article 9).

The code is in `core/crates/cabinetos-terminal`. The core wires it to the
pipe; the messages are in [ipc.md](ipc.md), "Terminal sessions". The
terminal pane itself comes with the UI (Phase 5); until then,
`cabinetos-cli term` is the client.

## At a glance

Measured on 2026-09-28 on the development PC (Windows 11, debug build):

| What | Result |
|---|---|
| `terminal_open`, until the shell's process runs | 17 to 24 ms for cmd and pwsh 7 in the core's log (up to 90 ms seen for one pwsh start) |
| Closing a shell at its prompt | 3 ms (cmd) to 15 ms (pwsh) |
| 2.8 MB of output while no client reads | nothing lost; the shell waits, then all of it arrives |
| A client that leaves and a new one that attaches | the shell keeps running; output made meanwhile is delivered |

## Profiles

A session starts a profile from `terminal.profiles` in `cabinetos.json`
([config.md](config.md)); without a name it starts `terminal.defaultProfile`.
The defaults:

```json
"terminal": {
  "defaultProfile": "pwsh",
  "profiles": [
    { "name": "pwsh", "command": "pwsh.exe", "args": ["-NoLogo"] },
    { "name": "cmd", "command": "cmd.exe", "args": [] },
    { "name": "wsl", "command": "wsl.exe", "args": [] }
  ]
}
```

- The core reads the profiles at each `terminal_open`, so an edited profile
  applies to the next shell at once. A running shell keeps what it started
  with.
- `command` is a full path, or a name looked up in the folders of `PATH`
  (with `.exe` added when it has no extension). The current folder is not
  searched, so a stray `pwsh.exe` there never runs. A program that is not
  found fails with `spawn_failed`.
- `args` are quoted the way the Microsoft C runtime splits a command line,
  so each one reaches the program as one argument.
- The shell starts in the folder the client names (an absolute path to a
  folder), or in the user's profile folder.
- It gets the core's environment plus two variables: `TERM=xterm-256color`
  (the pseudo-console speaks the VT sequences of xterm) and
  `CABINETOS_SESSION=<session id>`, so a script can tell it runs in a
  CabinetOS terminal.

## The byte pipe

Terminal output is frequent and binary, so it does not travel on the JSON
control pipe. Each session has a pipe of its own, named
`\\.\pipe\cabinetos-term-<16 random hex digits>`, which `terminal_opened`
and `terminal_list` report.

- **Raw bytes both ways**, no framing. Reading gives the shell's output:
  UTF-8 text with VT sequences (colors, cursor moves), as a terminal
  emulator such as xterm.js expects. Writing sends keys: text, `\r` for
  Enter, VT sequences for the other keys.
- **The control pipe's rules:** only the user who runs the core can open
  it (one access-list entry, for that user), and network clients are
  refused.
- **One client at a time.** While a client is attached, opening the pipe
  fails with `ERROR_PIPE_BUSY`. When it leaves, the next client can open
  the pipe at once: the core creates the pipe's next instance before it
  lets go of the last, so the name never disappears in between.
- **The session belongs to the core**, not to the client. A client may
  leave, crash or restart; the shell goes on. A restarted UI connects,
  calls `terminal_list`, and opens the same pipe again.
- **Output waits for a client**, up to 1 MiB. When the buffer is full, the
  core stops reading the pseudo-console, so the shell waits too, until a
  client reads (backpressure). The core logs `terminal output
  backpressure` once per session. No output is dropped while the shell
  runs.
- **The end.** When the shell exits, the client reads the shell's last
  output, then the end of the pipe. Every connection that said `hello`
  gets `terminal_exited` with the exit code. The session stays listed, as
  exited, until a client closes it; a client that opens its pipe after the
  end gets the end at once.

The pseudo-console paints the shell's last output a few milliseconds after
the shell wrote it. So after the shell exits, the core waits until the
output has been quiet for 100 ms (at most 1 s) before it closes the
pseudo-console. After the exit, output that does not fit the buffer is
dropped instead of waiting, so a session whose client never reads can
still end.

## Following the active pane

`terminal_sync_cwd` types the shell's own change-directory command,
followed by Enter, as if the user typed it. The command depends on the
program's file name:

| Program | Line typed for `D:\it's here` |
|---|---|
| `pwsh`, `powershell` | `Set-Location -LiteralPath 'D:\it''s here'` |
| `cmd` | `cd /d "D:\it's here"` |
| `wsl` | `cd "$(wslpath -a 'D:\it'\''s here')"` |
| anything else | `cd "D:\it's here"` |

Each shell must read the path literally:

- **PowerShell:** inside single quotes, each single-quote character is
  doubled, including the typographic quotes ‘ ’ ‚ ‛, which PowerShell also
  counts as quotes. `-LiteralPath` keeps `[` and `]` from being wildcards.
- **cmd:** a name cannot contain `"`, but it can contain `%`, and cmd
  expands `%name%` even inside quotes when `name` is a variable. So each `%`
  is moved out of the quotes with a caret after it: `100%x` becomes
  `"100"%^x""`. The variable name cmd would look up then starts with `^`,
  which no variable does, and the caret only keeps the next character
  literal. `cd` ignores the quotes. Tested with a folder named
  `to 100%CABINETOS_SESSION% & ^x (y)`.
- **WSL:** `wslpath` turns the Windows path into the Linux one
  (`/mnt/d/it's here`); a `'` is written `'\''` inside single quotes. This
  assumes a POSIX shell such as bash or zsh in the Linux distribution.

Every other character goes into the line as it is: the line reaches the
pseudo-console as UTF-8, which it turns into the shell's own input, so
Cyrillic, Chinese, an emoji or a decomposed accent needs no quoting of its
own and the console's code page plays no part. For
`E:\Звіт 'проєкт' $HOME 100%PATH%` the lines are:

| Program | Line typed |
|---|---|
| `pwsh`, `powershell` | `Set-Location -LiteralPath 'E:\Звіт ''проєкт'' $HOME 100%PATH%'` |
| `cmd` | `cd /d "E:\Звіт 'проєкт' $HOME 100"%^P"ATH"%^""` |
| `wsl` | `cd "$(wslpath -a 'E:\Звіт '\''проєкт'\'' $HOME 100%PATH%')"` |

`$HOME` stays literal in PowerShell and bash because single quotes do not
expand it, and in cmd because cmd has no `$` variables. The tests type a
folder named `Звіт 'проєкт' $HOME ’q’ 100%PATH% Ґанок` into real cmd,
PowerShell 7, Windows PowerShell 5.1 and WSL, and read the folder back from
each shell (`cd`, `(Get-Location).Path`, `pwd`); WSL's `wslpath` turns it
into `/mnt/c/…/Звіт 'проєкт' $HOME ’q’ 100%PATH% Ґанок`.

The path must be an absolute path to a folder (`invalid_path`, `not_found`
otherwise). Limits of typing a command: text already on the prompt line
stays in front of it, and a program that runs in the shell (an editor, a
long build) receives the line instead of the shell. The client decides
when to sync; the core does not guess.

## Size

`terminal_open` and `terminal_resize` take the size in character cells,
from 1 to 32,767 each. A resize reaches the shell at once (`mode con` in
cmd shows the new width); the pseudo-console then paints the screen again
at the new size.

## Closing

`terminal_close` closes the pseudo-console. The shell sees this as a
hang-up (the console it runs in went away) and ends; at its prompt this
takes 3 to 15 ms. A shell still running 2 s later is ended by force. That
was seen with a shell closed in its first milliseconds, before it finished
starting: it never ends on its own. The reply comes once the shell has
ended, and the session is forgotten.

When the core stops, it closes every session the same way, all within the
same 2 s.

## Limits

| Limit | Value |
|---|---|
| Sessions at once, running or exited | 32 (`spawn_failed` for the 33rd) |
| Output waiting for a client | 1 MiB per session |
| Size | 1 to 32,767 cells each way |
| Clients attached to one session | 1 |

## Notes for a client

- At start, the pseudo-console asks the terminal in front of it for
  win32-input-mode (`ESC [ ? 9001 h`, keys sent as detailed sequences) and
  for focus reports (`ESC [ ? 1004 h`). A terminal emulator that does not
  know them ignores them. A client that passes the output to a real
  console, as `cabinetos-cli term` does, must drop them: the console would
  otherwise send every key as a win32-input-mode sequence.
- A client that attaches to a running session gets what waited in the
  buffer, not a picture of the screen. A resize to another size makes the
  pseudo-console paint the whole screen again (seen in the tests), which
  a reattaching UI can use to draw the screen from scratch.

## The CLI

`cabinetos-cli term` opens a session sized to its console window and
attaches to it:

```text
cabinetos-cli --pipe demo term --profile pwsh --cwd E:\
```

- The console switches to raw mode: keys go to the shell one by one, as VT
  sequences, with no echo of their own; Ctrl+C goes to the shell as a key.
- The CLI compares the console's size with the session's every 250 ms and
  sends `terminal_resize` when it changed.
- `Ctrl+]` detaches: the CLI exits and the session keeps running.
- When the shell exits, the CLI prints its exit code on standard error:
  `cabinetos-cli: session 3 ended; exit code 0`.
- With standard input from a pipe or a file, the CLI forwards the bytes
  unchanged and waits for the shell to exit, so the input should end with
  `exit`: `printf 'Get-Location\r\nexit\r\n' | cabinetos-cli term`.
- Without `--profile`, the default profile; without `--cwd`, the CLI's own
  folder.

The other commands act on any session, from any CLI:

```text
cabinetos-cli term list
3 pwsh pid 4242 120x30 running attached E:\
4 cmd pid 5120 80x25 exited(0) detached C:\Users\me
cabinetos-cli term cd 3 D:\docs
session 3: cd D:\docs
cabinetos-cli term close 3
session 3 closed
```

## Threads and logs

Each session has three threads in the core: `term-<id>-out` reads the
pseudo-console's output into the buffer, `term-<id>-in` writes keys into
it, and `term-<id>-exit` waits for the shell and reports its exit. One
async task serves the byte pipe. Every log line about a session names it
in `session_id` (under `fields`, [diagnostics.md](diagnostics.md)):
`terminal session opened` (with the profile, program, process ID, folder
and size), `terminal shell exited` (with the exit code), `terminal session
closed`, and the warning `terminal output backpressure`.

## Tests

`cargo test -p cabinetos-terminal` runs real shells: cmd always, pwsh and
Windows PowerShell and WSL when installed (skipped with a message
otherwise; CI has no WSL). The core's `tests/terminal.rs` and the CLI's
`tests/term.rs` add the protocol, a session that outlives its connection,
the shells ending with the core, and `term` itself. To test `term` in a
real console, a CLI test runs `cabinetos-cli term` as the program of a
session of its own and types into that pseudo-console, `Ctrl+]` included.

The shells run only `echo`, `cd`, `mode con`, `Get-Location`, `pwd` and
`exit`, in folders under `%TEMP%\cabinetos-term-test\`, which the tests
remove.
