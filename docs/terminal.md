# The integrated terminal

The core runs shells for the terminal pane: pwsh, cmd, WSL, or any program
in a profile. Each shell runs in a pseudo-console (ConPTY, the Windows API
that gives a program a console whose screen goes to a pipe instead of a
window). The pane shows what the shell prints and sends what the user types.
Nothing runs until a client asks: the terminal is hidden by default
(Constitution Article 4; [ADR 0005](decisions/0005-terminal-in-core-hidden.md)).
Each session belongs to one file pane and is locked or linked to it
(Article 9; "Panes and modes" below). A linked shell follows its pane
through its own prompt hook, never by typing into it ("The prompt hook"
below).

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
    { "name": "pwsh", "command": "pwsh.exe", "args": ["-NoLogo"], "linkable": true },
    { "name": "cmd", "command": "cmd.exe", "args": [], "linkable": false },
    { "name": "wsl", "command": "wsl.exe", "args": [], "linkable": true },
    {
      "name": "claude",
      "command": "claude.exe",
      "args": ["--append-system-prompt", "…"],
      "linkable": false
    }
  ]
}
```

(The core writes `linkable` for every default profile. The `claude`
profile's note is quoted below.)

- The core reads the profiles at each `terminal_open`, so an edited profile
  applies to the next shell at once. A running shell keeps what it started
  with.
- `linkable` says whether a session of this profile may be linked to its
  pane ("Panes and modes" below). Left out, it is `true` for PowerShell
  (`pwsh`, `powershell`) and WSL while the profile's `hook` is on, and
  `false` for any other program. It is `false` for cmd and Claude Code: a
  linked shell follows its pane through a prompt hook (a few lines the
  shell runs each time it shows its prompt, "The prompt hook" below), and
  no such hook can be added to them. Linking such a session fails with
  `not_linkable`. Typing paths at its prompt (Ctrl+Alt+P) still works:
  that is the user's own request. A profile that says `"linkable": true`
  for a program without a hook (cmd) may be linked, but nothing follows.
- `hook` is the prompt hook, for PowerShell and WSL (bash): `true` when
  left out (CabinetOS's own), `false` (none: the session neither follows
  its pane nor reports its folder, and it is not linkable unless
  `linkable` says so), or a string, the user's own code in the shell's
  language, run at each prompt in place of the follow step (the folder
  report stays). Any other program ignores it.
- `followsPane`, the key of the old folder sync, is ignored since
  2026-10-01: a file that has it still loads, and the core no longer
  writes it.
- The `claude` profile starts Claude Code (`claude.exe`, the
  subscription-based command-line program; it needs no API key). It is
  not a shell, so it cannot be linked: it starts in the folder the
  terminal was opened in and stays there. `--append-system-prompt`
  adds one line to Claude Code's system prompt, so it knows where it runs
  and how to reach `cabinetos-cli`:
  > You run inside the CabinetOS file manager's integrated terminal. Its
  > command line is on the PATH as cabinetos-cli (also as cab).
  > `cabinetos-cli state --json` prints both panes: their tabs, cursor and
  > marked files. `cabinetos-cli --help` lists the rest. For the marked
  > files, prefer its copy, move and delete: they run as jobs, and
  > `cabinetos-cli undo --last` reverses the last one.
- **A `cabinetos.json` that exists already keeps its own profile list.** The
  core writes the defaults only when the file is missing; a file that lists
  three profiles gets no `claude`. Add it by hand to `terminal.profiles`
  (the full note is in the default file of a new install, and in
  `sdk/config/cabinetos.schema.json`):
  ```json
  { "name": "claude", "command": "claude.exe", "linkable": false,
    "args": ["--append-system-prompt", "You run inside the CabinetOS file manager's integrated terminal. Its command line is on the PATH as cabinetos-cli (also as cab). `cabinetos-cli state --json` prints both panes: their tabs, cursor and marked files. `cabinetos-cli --help` lists the rest. For the marked files, prefer its copy, move and delete: they run as jobs, and `cabinetos-cli undo --last` reverses the last one."] }
  ```
- `command` is a full path, or a name looked up in the folders of `PATH`
  (with `.exe` added when it has no extension). The current folder is not
  searched, so a stray `pwsh.exe` there never runs. A program that is not
  found fails with `spawn_failed`.
- `args` are quoted the way the Microsoft C runtime splits a command line,
  so each one reaches the program as one argument.
- The shell starts in the folder the client names (an absolute path to a
  folder), or in the user's profile folder.
- It gets the core's environment plus three variables: `TERM=xterm-256color`
  (the pseudo-console speaks the VT sequences of xterm),
  `CABINETOS_SESSION=<session id>`, so a script can tell it runs in a
  CabinetOS terminal, and `CABINETOS_PIPE=<the core's pipe token>`, so
  `cab` run in it reaches the core of the window it sits in (the window
  starts its core on a random pipe; `cab` without `--pipe` reads this
  variable before it falls back to `dev`). Neither names a folder: a
  folder is asked of the core when it is needed, so it is never stale. A
  PowerShell or WSL shell also gets its prompt hook ("The prompt hook"
  below).
- The core's own folder is added at the end of the session's `PATH`
  (unless `PATH` lists it already; a `PATH` that is missing or empty
  becomes that folder alone). That folder holds `cabinetos-cli.exe`, and in
  a release `cab.exe` too, so `cabinetos-cli` (and `cab` in a release) run
  from any CabinetOS terminal. It goes last, so a program with the same
  name that is already on `PATH` still wins. The core's own `PATH` does
  not change.

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

## Panes and modes

Every session belongs to one file pane, `left` or `right`, which
`terminal_open` names. The pane never changes: a session opened for the
left pane stays the left pane's. Each session also has a mode:

- **`locked`** (the default): the shell stays where the user takes it.
  Nothing the panes do reaches it.
- **`linked`**: the session follows its pane. Each time the shell draws
  its prompt, its prompt hook asks the core for the pane's folder and,
  when the pane has moved since the hook last looked (or the session was
  just linked) and the shell is elsewhere, changes to it before the
  prompt is drawn ("The prompt hook" below). Nothing follows until the
  shell reaches its prompt: while a command runs, or while the user types
  a line, the pane may move freely, and the line runs where its prompt
  was drawn; the prompt after it is in the pane's folder. A `cd` of the
  user's own stays until the pane moves again. Linking a session makes
  its next prompt follow (press Enter at an empty prompt to follow at
  once). cmd and Claude Code cannot be linked.

`terminal_open` takes the mode too (`locked` when left out).
`terminal_set_mode` changes it later. Every connection that said `hello`
then gets `terminal_mode_changed` with the session and its new mode, so
two windows, or a window and the CLI, show the same mode. The same mode
again changes nothing and sends no event. An exited session takes the
change too, since it stays listed until it is closed. `linked` for a
session whose profile is not linkable fails with `not_linkable`;
`terminal_open` with `linked` fails the same way and starts no shell.
`terminal_opened` reports the new session's mode and whether it is
linkable, and `terminal_list` reports the pane, the mode and `linkable`
of every session.

Until 2026-10-01 the core had `terminal_sync_cwd`: it typed the shell's
own `cd` command and Enter whenever the window's active pane changed
folder. It is gone, with the window's following of the active pane. A
line typed into a shell the user did not touch can land in a half-typed
command or in a running program, so only the user's own request types
into a shell now ([ui.md](ui.md), "The terminal"), and a linked shell
follows through its prompt hook.

## The prompt hook

A few lines a shell runs each time it draws its prompt, added by the core
when it starts the shell, without touching the user's own profile files
(unit 2 of the terminal sprint, 2026-10-02). At each prompt it does two
things, in this order:

1. **Follow** (a linked session only). It runs `cabinetos-cli term cwd`
   (`cab` below), by the full path of the one next to the core, so a
   program of the same name on the `PATH` cannot stand in. It asks the core
   for the session's mode and its pane's folder (`terminal_pane_folder`,
   [ipc.md](ipc.md), "Terminal sessions") and prints the folder only for
   a linked session, and only when a window has said what its panes show.
   Nothing printed: the hook forgets the folder it followed last. A folder
   it has not followed yet (the pane moved, or the session was just
   linked): it remembers it and, when the shell is elsewhere, changes to
   it (`Set-Location -LiteralPath` in PowerShell, `builtin cd` in bash).
   The same folder as last time changes nothing, so a `cd` of the user's
   own stays. A run of `cab` that failed changes nothing either.
2. **Report** (every session). It prints the shell's folder as
   `ESC ] 9 ; 9 ; <folder> ESC \` (OSC 9;9, the sequence Windows Terminal
   reads). The session's output thread reads it from the bytes it already
   sees and, when the folder changed, every connection that said `hello`
   gets `terminal_folder_changed`; `terminal_list` reports it as
   `folder`. The bytes stay in the stream; xterm.js ignores the sequence.
   The window's header shows it ([ui.md](ui.md), "The terminal").

The hook prints nothing else, swallows every error, and hands the user's
prompt the last command's status (`$?` and `$LASTEXITCODE` in
PowerShell, `$?` in bash). It runs only when the prompt is drawn: a
running command or a half-typed line is never touched, and nothing is
ever typed into the shell. Keys typed while it runs wait and arrive whole.

Per shell:

| Shell | How the hook is added |
|---|---|
| PowerShell (`pwsh`, `powershell`) | `-NoExit -Command <script>` after the profile's own arguments. The user's profile loads first; the script keeps the `prompt` function the profile left and wraps it, so the user's own prompt (oh-my-posh, starship) still draws. A command line, not a script file: Windows PowerShell's default execution policy refuses script files, and nothing is written to disk. `cab` runs through .NET's process API, which reads the folder as UTF-8 whatever the console's code page. A profile whose arguments already give PowerShell a command or a script (`-Command`, `-File`, `-EncodedCommand`, a script name) gets no hook, and the core logs a warning. |
| WSL (bash) | `PROMPT_COMMAND`, passed into Linux by `WSLENV` together with `CABINETOS_SESSION` and `CABINETOS_PIPE`. It defines the function `__cabinetos_prompt` at the first prompt and calls it at each. `cab` (a Windows program, found by `wslpath`) runs with its input from `/dev/null`: WSL hands a Windows program the terminal's input, and keys typed while it ran were lost (seen 2026-10-02). The pane's folder becomes a Linux one with `wslpath -u`, the shell's folder a Windows one with `wslpath -w` (only when it changed). A `~/.bashrc` that sets `PROMPT_COMMAND` replaces the hook (one that adds to it keeps it); zsh and fish do not read it. |
| cmd, Claude Code, any other program | No hook: they cannot be linked, and they report no folder. |

**The cost.** One process start per prompt, locked or linked: the hook
must ask the core whether the session is linked now. Measured on
2026-10-02 on the development PC (release build, a core with one
session, 30 runs each): `cab term cwd` takes 19 ms in the middle
(median), 65 ms at the 90th percentile, run as `& cab` or through .NET's
process API alike; `cmd /c rem` takes 21 ms on the same PC, so the start
of a Windows process is most of it. `cab term cwd` waits at most 1 s for
the pipe and 1 s for the reply, so a stuck core cannot hold a prompt
longer. `"hook": false` in a profile turns it off.

## Typing paths

`terminal_type_paths` types paths at the prompt for the user to go on
typing around them (the window's Ctrl+Alt+P, the active pane's folder, and
Ctrl+Shift+Enter, the selected paths). Total Commander's command line has
Ctrl+P and Ctrl+Shift+Enter; the window's Ctrl+P went to Quick Open in
Phase 16. Each path is quoted so the shell reads it literally (the
program's file name says which shell it is); the paths are separated by
one space, and no Enter follows. For `D:\it's here` and `D:\100%x`:

| Program | Text typed |
|---|---|
| `pwsh`, `powershell` | `'D:\it''s here' 'D:\100%x'` |
| `cmd` | `"D:\it's here" "D:\100"%^x""` |
| `wsl` | `"$(wslpath -a 'D:\it'\''s here')" "$(wslpath -a 'D:\100%x')"` |
| anything else | `"D:\it's here" "D:\100%x"` |

Each shell must read the path literally:

- **PowerShell:** inside single quotes, each single-quote character is
  doubled, including the typographic quotes ‘ ’ ‚ ‛, which PowerShell also
  counts as quotes. A command that takes wildcards still reads `[` and
  `]` as wildcards; `Set-Location -LiteralPath` does not.
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

Every other character goes in as it is: the text reaches the
pseudo-console as UTF-8, which it turns into the shell's own input, so
Cyrillic, Chinese, an emoji or a decomposed accent needs no quoting of its
own and the console's code page plays no part. For
`E:\Звіт 'проєкт' $HOME 100%PATH%` the text is:

| Program | Text typed |
|---|---|
| `pwsh`, `powershell` | `'E:\Звіт ''проєкт'' $HOME 100%PATH%'` |
| `cmd` | `"E:\Звіт 'проєкт' $HOME 100"%^P"ATH"%^""` |
| `wsl` | `"$(wslpath -a 'E:\Звіт '\''проєкт'\'' $HOME 100%PATH%')"` |

`$HOME` stays literal in PowerShell and bash because single quotes do not
expand it, and in cmd because cmd has no `$` variables. The tests type a
folder named `Звіт 'проєкт' $HOME ’q’ 100%PATH% Ґанок` into real cmd,
PowerShell 7, Windows PowerShell 5.1 and WSL at an empty prompt, put the
shell's own `cd` in front of it (`cd /d`, `Set-Location -LiteralPath`,
`cd`), press Enter and read the folder back from each shell (`cd`,
`(Get-Location).Path`, `pwd`); WSL's `wslpath` turns it into
`/mnt/c/…/Звіт 'проєкт' $HOME ’q’ 100%PATH% Ґанок`. Typed behind the
command instead, the folder lost its typographic quotes in Windows
PowerShell 5.1 on 2026-10-01, while a suggestion from its history was on
the line; typed at an empty prompt, it arrived whole.

The text goes in as one chunk, so a key the user presses meanwhile cannot
land inside it; text already on the line stays in front of it. The paths
need not exist: they are only text. A path with a control character
(a line break, Escape) is refused with `invalid_path`, because typed it
would act as a key. The test types two paths into real cmd, one of them
`100%CABINETOS_SESSION% & x`, and finds both on the one prompt line.

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
cabinetos-cli --pipe demo term --profile pwsh --cwd E:\ --pane right
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
  folder; without `--pane`, the left pane. The session starts locked.

`cabinetos-cli term cwd` is what a prompt hook runs: it prints the folder
a linked session follows (its pane's folder), one line, or nothing for a
locked session or when no window says what its panes show. The session
comes from `--session <id>`, else `CABINETOS_SESSION`; the pipe from
`--pipe`, else `CABINETOS_PIPE` (both are set in every CabinetOS
terminal). Exit code 0 when the core answered, 1 for no session, no
core, or an unknown session.

The other commands act on any session, from any CLI:

```text
cabinetos-cli term list
3 pwsh right locked pid 4242 120x30 running attached E:\
4 cmd left locked pid 5120 80x25 exited(0) detached C:\Users\me
cabinetos-cli term mode 3 linked
session 3: linked
cabinetos-cli term type 3 "D:\docs\a b.txt" D:\docs\c.md
session 3: typed 2 paths
cabinetos-cli term close 3
session 3 closed
```

## Threads and logs

Each session has three threads in the core: `term-<id>-out` reads the
pseudo-console's output into the buffer, `term-<id>-in` writes keys into
it, and `term-<id>-exit` waits for the shell and reports its exit. One
async task serves the byte pipe. Every log line about a session names it
in `session_id` (under `fields`, [diagnostics.md](diagnostics.md)):
`terminal session opened` (with the profile, program, process ID, folder,
size, pane, mode, `linkable`, and `hooked`: whether the shell got its
prompt hook), `terminal mode changed` (with the new mode), `terminal
shell exited` (with the exit code), `terminal session closed`, the
warnings `terminal output backpressure` and `the prompt hook was not
added` (with the reason), and at debug level `terminal folder reported`
(with the folder). `term-<id>-out` also reads the prompt hook's folder
reports. The hook's `terminal_pane_folder` requests are logged at debug
level, one per prompt.

## Tests

`cargo test -p cabinetos-terminal` runs real shells: cmd always, pwsh and
Windows PowerShell and WSL when installed (skipped with a message
otherwise; CI has no WSL). The core's `tests/terminal.rs` and the CLI's
`tests/term.rs` add the protocol, a session that outlives its connection,
the shells ending with the core, and `term` itself. The prompt hook is
tested at three levels: its text per shell and the folder report's reader
(unit tests); real shells running a hook of the test's own (the terminal
crate: the hook runs at each prompt, `$LASTEXITCODE` and the pipe token
reach the shell, a profile with its own `-Command` gets none); and a real
core, `cab` and shell with the test as the window (the CLI's tests: a
linked pwsh, Windows PowerShell and WSL bash follow their pane at the next
prompt, a half-typed line runs as typed where its prompt was drawn, a
`cd` of the user's own stays, a locked session stays, keys typed while
the hook runs arrive whole, and each change of folder is reported once). To test `term` in a
real console, a CLI test runs `cabinetos-cli term` as the program of a
session of its own and types into that pseudo-console, `Ctrl+]` included.

The shells run only `echo`, `cd`, `mode con`, `Get-Location`, `pwd` and
`exit`, in folders under `%TEMP%\cabinetos-term-test\`, which the tests
remove. Paths typed with `terminal_type_paths` run only behind the tests'
own `cd` ("Typing paths" above).

The `claude` profile was checked with the real program on 2026-09-30, two
ways: headless, through `cabinetos-cli term --profile claude` driven by a
script that reads the output as it grows (at its prompt in 1 s, the note
of the profile named in its reply, alive after Esc, ended by `/exit` with
exit code 0), and with real keys in the window (`ui/livecheck/claude-terminal.ps1`,
[ui.md](ui.md), "The live check"). Both clear every `CLAUDE*` variable
before the core starts: Claude Code refuses to start inside another
Claude Code session, which it tells by `CLAUDECODE`; a user's window
never has it. Report:
[log/2026-09-30/claude-code-in-the-terminal.md](log/2026-09-30/claude-code-in-the-terminal.md).
