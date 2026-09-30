# Phase 14 — The AI agent extension

The history of this phase, moved from [PLAN.md](../PLAN.md) on 2026-09-30; the plan keeps the decisions and the done-when list.

**14a, the core foundations, built 2026-09-30 (protocol 13; 46c644c,
b6bd317, 942b82e, 94b0e33, 85d6cd5, c6489a3, 44f2c66, 4feb72f).** Every
addition is general and works without AI. `window_state` keeps one state
per connection (a client is "<hello name>#N") and `get_window_state`
returns the newest or a named one; `cabinetos-cli state` prints it.
Previews: `preview_listing` answers `preview_opened` with the listing (a
normal listing with the header's preview flag and 12-byte preview rows
at the old reserved offset, so a folder listing is byte-identical to
before), `open_preview` reopens one a plugin proposed, `preview_apply`
runs the rows as chained `steps` jobs (neighbouring rows of one kind
share a job; a failed job cancels the rest) and answers `jobs_started`,
`preview_cancel` drops it; previews expire after 10 minutes, 20 per
client. Secrets live in the Windows Credential Manager through the new
crate `cabinetos-secrets` (`secret_set`, `secret_get`, `secret_delete`,
`secret_list`, `cabinetos-cli secret`); a value never prints, and a test
scans a trace-level log for it. A plugin reaches the network only
through the core's `http-request` (ureq on rustls with the Windows
certificate store): `https:` only except localhost, only the hosts and
the secrets its manifest names, no redirects, 8 MiB, 120 s, the secret
put into the named header by the core and never seen by the plugin, one
log line per request; the WIT package is 0.2.0, and a 0.1 plugin is
refused. `fs:watch` gives a plugin `watch-folder` and `unwatch-folder`
under its roots and the export `on-event` with `folder-changed` (200 ms
batches, an `overflow` flag, at most 16 watches). A plugin command may
declare `input` in its manifest, and `list_commands` says so. Every job
leaves one line in the undo journal (`%LOCALAPPDATA%\CabinetOS\undo`,
200 jobs, 256 MiB of saved copies, a replaced file moved aside rather
than copied); `undo_job` reverses a job or the newest one not yet
undone, names what cannot come back (a file in the Recycle Bin, a delete
for good), and `cabinetos-cli undo <job>|--last` follows it. The release
carries `cab.exe`, the command line under a short name. New fixture
plugins `fetcher` and `watcher`. 700 core tests. Left for the extension
and decided the same night: a plugin can make core requests only through
a new host function under an allow-listed capability, and a plugin may
read its own settings under `plugins.<id>.settings`; both go in with 14b.
Guides: [ipc.md](../ipc.md), [plugins.md](../plugins.md), [jobs.md](../jobs.md)
("Chains and steps", "Undo").

**The window's parts of Phase 14, built 2026-09-30 (401d439, cc46f5f,
2dad54c, 74510e7, 80d5ed8, 409a1b8, d6e2de7).** Nothing in the window
knows an agent (Article 10); every piece is a general rule. A plugin
command with an `input` asks for its text in the prompt box first, titled
by the input's title, and Esc runs nothing. A preview a plugin proposes
opens with `open_preview` whenever a plugin command's result or any
plugin event carries a string field `preview`, and shows in the other
pane in place of its list (in single-pane mode the window switches to
dual while the preview waits, and back after): a create, a rename, a
move, a copy and a delete each with its target, a delete tinted red;
Enter sends `preview_apply` and Esc `preview_cancel` before the keymap,
and `preview_applied` or `preview_cancelled` from any cause, the
10-minute expiry included, closes it. Any plugin event carrying a string
field `notice` shows its text in the status bar. A tool page can follow
plugins (`subscribe`, at most 16, and `plugin-event`) and receives rows
dragged from a pane as `paths-dropped`, one path per line, at most 1,000,
through a catcher layer that covers the page only while a drag runs,
since a WebView2 takes drops for itself. The permissions review and the
plugin list show a `net` capability's hosts and the names of the stored
secrets it may use, never a value (Article 8). The UI test harness sets
`CABINETOS_UNDO_DIR`, so test jobs never write the real undo journal.
Checked with the real core (a six-row preview rendered, Enter ran the
four jobs it makes and the files matched, Esc changed nothing, a preview
opened by a plugin event, a fake command with input ran with the typed
text) and with a real mouse drag in the live check's new section 14,
whose "ask" part waits for the extension. Not yet seen with real keys:
the preview's Enter and Esc, the review dialog's hosts and secrets lines.
22 new UI tests, 767 in all. Guide: [ui.md](../ui.md), "What plugins ask of
the window".

**14b to 14e, the extension, built 2026-09-30 (3887a4c, 99b83c0,
21d414a, 85a2870, bf5c0b1, aaa1fce, 9e8d8e5).** First the two core
additions decided the same night: the capability `core:request` (high),
whose manifest lists the request types a plugin may send through the
host function `core-request`, with a fixed list of requests no manifest
can allow (`hello`, `shutdown`, `set_value`, the secrets, the bundle,
`window_state`), and `plugins.<id>.settings`, an open object a plugin may
read through `config-get`, nothing else of the `plugins` section. Then
the extension. `sdk/extensions/agent/plugin` is a Core Plugin (Article
11, the engine layer): it reaches the core only through `core-request`
and a model only through `http-request`, holds no key, runs no shell and
writes no file itself except its audit log in its own data folder. The
model writes `cab` command lines, which the plugin parses with the
command line's own definitions, now the library crate `cabinetos-cli-args`
shared with `cabinetos-cli`; `ls` and `describe` are answered from the
plugin's own reads under its roots, `search` and `state` through the
core, and the write commands become a preview (tier 2, the default) or
run at once (tier 3). Providers: Anthropic, an OpenAI-compatible endpoint
(a local Ollama by default) and a fake for tests; settings under
`plugins.agent.settings`. `agent.ask` (Ctrl+K Ctrl+A) asks through the
window's prompt box and answers with a preview in the other pane;
`agent.undo` reverses the last applied preview's jobs in reverse order.
The chat page `sdk/tools/agent-chat` is a Tool Extension with tier
buttons, Undo, the audit Log and a Rules panel; it follows the plugin's
events and takes dropped rows. Watch folders: a rule per folder from the
settings or `agent.rule.add` (kept in `rules.json` in the plugin's data
folder, since a plugin cannot change the configuration), one model call
and one round per new file, temporary files skipped, the tier deciding
what follows, a rule paused after 3 failures in a row, at most 10 files a
minute, and the agent's own changes ignored so a renaming rule cannot
loop. Both parts install from the marketplace as `agent` and
`agent-chat` (`build-index.ps1 -Extensions`; the default index leaves
them out on purpose, and publishing them is the creator's step). The
built plugin is committed as the fixture `sdk/fixtures/plugins/agent`
(about 1 MB, a new copy in the history at each rebuild), so the host
tests load the real component without a WASM toolchain. Tests: 722 core
(two host tests run the built component with the fake provider through
`agent.ask` to a preview, `agent.undo` after an apply, and a rule on a
real watched folder) and the plugin's own suite of 90, which the five
checks do not run. No real model was called; the first real conversation
waits for the creator to store a key (`cabinetos-cli secret set
anthropic`). Left for the shell: a tool page may run the commands of a
plugin it follows (a general rule, so the chat page can send `agent.*`),
and the live check's "ask" step with the real settings key and a
canned-reply file. Guide: [extensions/agent.md](../extensions/agent.md).

**Claude Code in the terminal, 2026-09-30 (2f0af70, f48c04c, 9be0f5b,
8ed8a48, 72a4bbc; coder on Sonnet).** The creator asked whether the
subscription Claude Code, the command-line program, can run in CabinetOS's
terminal with no API key. It can: it is a console program, and a profile
starts it. Built the same day: the fourth default profile `claude`
(`claude.exe` with `--append-system-prompt` and a 60-word note that names
`cabinetos-cli state --json`, its copy, move and delete as jobs, and `undo
--last`); `followsPane` on every profile (`false` for `claude`), so the
window's folder sync never types a change-directory line into a program
that is not a shell, which would have reached Claude Code's prompt as a
message (a new decision `SkipProfile` with its caption, from the tab's
first moment; the user's own Ctrl+P still types paths there); and the
core's own folder at the end of every session's `PATH`, so `cabinetos-cli`
(and `cab` in a release) run from any CabinetOS terminal. A file that
already lists profiles keeps its list; the docs give the JSON to add. Esc
needed no change: the page passes only its ways out to the window, so Esc
reaches the program, as the live check had shown for pwsh. Checked three
ways the same day, all recorded in
[log/2026-09-30/claude-code-in-the-terminal.md](../log/2026-09-30/claude-code-in-the-terminal.md):
headless through the CLI's `term` (at its prompt in 1 s; its reply named
`state --json`, so the note reached it; alive after Esc; `/exit` ended
the session with code 0), with real keys in the window
(`ui/livecheck/claude-terminal.ps1`: Ctrl+` with `terminal.defaultProfile:
claude`, Esc cutting an answer short, a file written after Enter answered
its permission question, the sync skipping the tab on a folder change,
Ctrl+P from the pane, `/exit`; every check True), and the Agent
extension's first real model call, with a local model (Ollama, gemma3:27b
on the development PC): the chain worked end to end, the model did not
keep to the command format, and the agent refused every bad line and
changed nothing, so the first Claude conversation still waits for the
creator's key. Learned: Claude Code refuses to start with `CLAUDECODE` in
its environment (a run started from inside a Claude Code session clears
those variables; a user's window never has them), and the Windows build's
first start ran its login, which landed on the creator's Console
organization by the look of its status line, for the creator to check
with `/status`. Tests: 726 core, 852 window. Guides:
[terminal.md](../terminal.md), "Profiles"; [extensions/agent.md](../extensions/agent.md),
"Claude Code in the terminal".
