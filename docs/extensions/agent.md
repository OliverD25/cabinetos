# The Agent extension

The Agent works the file manager for you. You tell it in words what you
want ("put the photos of last summer in a folder of their own"). It asks a
model you choose, and turns the answer into the same command lines a person
can type. Every change first shows as a preview. It is an extension, not part
of CabinetOS (Constitution Article 10, The Zero-Bloat Foundation): you
install it from the marketplace, and without it CabinetOS has no agent.

It has two parts, one for each layer of Article 11 (Bifurcated Extension
Architecture):

| Part | Layer | Where | Marketplace item |
|---|---|---|---|
| The agent | Core Plugin (no UI) | [sdk/extensions/agent/plugin](../../sdk/extensions/agent/plugin) | `agent` |
| The chat | Tool Extension (a page in the window) | [sdk/tools/agent-chat](../../sdk/tools/agent-chat) | `agent-chat` |

The plugin works alone: the command palette has "Agent: Ask" (Ctrl+K
Ctrl+A). The chat page is the comfortable way: it keeps the conversation,
shows what the agent did, and has the buttons for the tier and for undo. The
two marketplace items name each other, so you find both.

## At a glance

- **Nothing changes without a preview** at the default tier. The agent
  proposes; you apply. Every change it makes is a job in the undo journal, so
  it can be undone.
- **The agent never holds your key.** The key lives in the Windows Credential
  Manager, and the core adds it to the request ([ipc.md](ipc.md), "Secrets").
- **The agent runs no shell and no program.** The model writes command lines
  in the syntax of `cab`, the command-line client. The plugin parses them
  with the client's own definitions and runs only a short list of them.
- **The agent cannot write to the disk itself.** It may only read folder
  listings (`fs:read`). Every change goes to the core as a preview, like a
  change a person asks for.
- **Everything is logged** in an audit log that you can read.
- **It can watch folders.** A watch rule names a folder and what to do with
  each new file that arrives; the tier decides what becomes of the model's
  answer, as for any request.

## Install and set up

1. Install `agent` (and `agent-chat`) from the marketplace, and allow what the
   agent asks for in the review. It asks for these capabilities
   ([plugins.md](plugins.md)):

   | Capability | Why the agent asks |
   |---|---|
   | `cmd:register` | Its commands in the palette. |
   | `config:read` | Its own settings (`plugins.agent.settings`), and nothing else of `plugins`. |
   | `events:emit` | To tell the chat page and the window what it answered. |
   | `fs:read`, root `%USERPROFILE%` | To list the folders you ask about: names, sizes and dates. It reads no file contents. |
   | `fs:watch`, root `%USERPROFILE%` | To hear about new files in the folders you give a watch rule (names only; it can read them where `fs:read` allows). |
   | `net`, hosts `api.anthropic.com` and `localhost:11434`, secrets `anthropic` and `openai` | To send your request and the folder listings to the model. |
   | `core:request`, requests `get_window_state`, `preview_listing`, `preview_apply`, `preview_cancel`, `undo_job`, `search` | To read what the window shows, propose changes as a preview, apply one (Autonomous tier only), undo, and search by name. It cannot read secrets or change settings. |

   The roots of `fs:read` limit where the agent may look: it is told the
   same folders in its instructions, and refuses a path outside them. Edit
   the roots in `plugin.json` to give it fewer folders or more.

2. Choose a model. Set `plugins.agent.settings` in `cabinetos.json`
   ([config.md](config.md)) and store the key:

   - **Claude** (the default): store your key once, and give a model if you
     want another than the default.

     ```text
     cabinetos-cli secret set anthropic
     ```

     ```json
     "plugins": { "agent": { "settings": { "provider": "anthropic" } } }
     ```

   - **A model on your own computer**, such as Ollama or LM Studio, or any
     service that speaks the OpenAI chat form:

     ```json
     "plugins": { "agent": { "settings": { "provider": "openai", "base": "http://localhost:11434", "model": "llama3.1" } } }
     ```

     A server on this computer needs no key. For a remote service, name its
     host in `plugin.json` (`net` hosts) and store the key as `openai`.

## Settings

Under `plugins.agent.settings` in `cabinetos.json`. A change applies to the
next request; the agent does not restart. A wrong value is an error that
names the key.

| Key | Values | Default | What |
|---|---|---|---|
| `provider` | `anthropic`, `openai`, `fake` | `anthropic` | Who answers. `fake` is for tests: canned replies, no network. |
| `model` | text | `claude-sonnet-5-5`, or `llama3.1` for `openai` | The model asked. |
| `base` | URL | `http://localhost:11434` | The server of an `openai` provider (`<base>/v1/chat/completions`). |
| `maxTokens` | 1 to 64000 | `4096` | The longest answer asked for. |
| `tier` | `1`, `2`, `3` | `2` | How much the agent may do (see below). The tier you set with "Agent: Set Tier" or the chat page wins until you change `tier` here. |
| `rules` | a list of `{ "folder", "rule" }` | none | Watch rules (see "Watch folders"). `folder` is an absolute Windows path, `rule` says what to do with each new file, and each folder has one rule. |
| `fakeReplies` | a file name | `fake-replies.json` | For `fake`: a file in the plugin's own data folder with a JSON list of replies, given one after the other. |

## Tiers

The tier says how much the agent may do. You choose it; the agent cannot
raise it.

| Tier | Name | What happens |
|---|---|---|
| 1 | Advisor | It looks (lists, describes, searches) and answers in words. It proposes nothing and changes nothing. |
| 2 | Diff and approve | **The default.** It proposes the changes as a preview in the other pane. You apply or cancel them. |
| 3 | Autonomous | It proposes the changes and applies them at once. Undo brings them back. It is never the default, and the chat page asks for a second click before it sets it. |

## Commands

| Command | Keys | Takes | What |
|---|---|---|---|
| `agent.ask` | Ctrl+K Ctrl+A | `input` (asked in a box), `paths` (the selection) | Runs one request. The preview it proposes opens in the other pane. |
| `agent.chat` | none | `message`, `paths` | The same, for the chat page. |
| `agent.tier` | none | `input` or `tier` (1, 2 or 3) | Sets the tier, or, with no number, says which is set. |
| `agent.undo` | none | `job` (optional) | Undoes the changes of the last applied preview, the last job first; or one job. |
| `agent.audit` | none | `n` (optional, 20) | Tells the chat page the last entries of the audit log. |
| `agent.rule.add` | none | `folder` and `rule`, or `input` (asked in a box): the folder, then the rule | Adds a watch rule and starts watching. |
| `agent.rule.remove` | none | `folder`, or `input` | Removes a rule that `agent.rule.add` made. |
| `agent.rule.resume` | none | `folder`, or `input` | Starts a paused or blocked rule again. |
| `agent.rule.list` | none | `quiet` (optional) | Tells the chat page the rules; and, unless `quiet`, puts a line in the status bar. |

## How a request runs

1. The agent asks the core what the window shows (`get_window_state`): the
   folder of each pane, the cursor row, the marked rows. With your request
   and the selection, that is the first message to the model, together with
   instructions that name the folders it may use, the tier and every command
   it may write ([prompt.md](../../sdk/extensions/agent/plugin/prompt.md)).
2. The model answers with a sentence and, in a fenced block, command lines.
3. **Commands that look** (`ls`, `describe`, `search`, `state`) run at once:
   `ls` and `describe` are answered by the plugin from the folders it may
   read, `search` and `state` by the core. Their output goes back to the
   model, which may look again. There are at most **3 rounds**.
4. **Commands that change files** (`mkdir`, `mkfile`, `rename`, `copy`,
   `move`, `delete`, and `undo`, at tier 3 only) become the rows of a
   **preview** (`preview_listing`): `create`, `rename`, `move`, `copy` or
   `delete` per file, at most 500 rows. A change sent in the same reply as a
   look is held back, and the model is told, so it decides after it has
   seen. A row outside the folders the agent may use, or with a name Windows
   does not allow, is refused with a reason the model can read and correct.
5. At tier 2 you see the preview in the other pane and apply it or cancel
   it. At tier 3 the agent applies it at once (`preview_apply`), and a line
   in the status bar says what it did. A new proposal cancels the one before
   it, which was not applied.

A `delete` goes to the Recycle Bin. The agent has no way to delete for good
and no way to overwrite: it is told to choose another name when one is taken,
and the core's conflict rules apply as they do for you.

## Undo

`agent.undo` (the chat page's Undo button) reverses what the agent applied,
as new jobs from the undo journal ([jobs.md](jobs.md), "Undo"): renames and
moves go back, copies go to the Recycle Bin, replaced files come back. What
cannot be brought back (a permanent delete) is named in the answer. The
agent learns which jobs a preview started from the core (`preview-applied`,
[plugins.md](plugins.md)), also when you applied it yourself in the window;
it remembers the last one over a restart.

## Watch folders

A watch rule names a folder and says what to do with each new file that
arrives in it: "put each file in a folder of its year", "move the receipts to
`D:\Papers\Receipts`". Rules come from two places:

- **`plugins.agent.settings.rules`** in `cabinetos.json`, a list of
  `{ "folder": "C:\\Users\\me\\Inbox", "rule": "..." }`. The agent starts
  watching them when it starts and again whenever the settings change.
- **`agent.rule.add`**, from the palette (one text: the folder, in quotes when
  it has a space, then the rule) or from the chat page. A plugin may not
  change the configuration, so these rules are kept in `rules.json` in the
  plugin's own folder. A folder that has a rule in the settings cannot get
  another; `agent.rule.remove` removes only the ones `agent.rule.add` made.

What happens for a new file:

1. The core watches the folder (`fs:watch`) and tells the agent about
   `created` files. A file that gets its real name from a temporary one (a
   finished browser download, `.crdownload`) counts as new too. Files that are
   still being written (`.crdownload`, `.part`, `.tmp`), Office's `~$` files,
   `desktop.ini` and `Thumbs.db` do not. Files that were there before the
   agent started are not new. Subfolders are not watched.
2. The agent asks the model **once** for each new file. The model is given the
   rule, the file's path, what `describe` shows for it, and the names in the
   folder. It has **one round**: nothing it looks at comes back, so it must use
   what it was given and answer with the commands that change files, or with
   words when the rule does not apply.
3. The tier decides what becomes of the answer, as for any request. **Tier 2:**
   a preview is offered (the agent tells the window with `agent.preview`, which
   opens it in the other pane) and only you apply it. **Tier 3:** it is applied
   at once and a status bar line says so; `agent.undo` reverses it. **Tier 1:**
   the rules stay idle, and you are told once.
4. Every request is in the audit log, with the source `rule`.

Some limits keep a folder from costing money or looping:

- A rule is **paused after 3 failures in a row** (the model cannot be reached,
  or answers with an error), with a notice. `agent.rule.resume` (or the
  Resume button) starts it again. A rule whose words you change starts afresh.
- A rule hands the model **at most 10 new files a minute**. The other files of
  that minute are skipped, and you are told once.
- The changes the agent made itself (a renamed file shows up as a new file)
  are not new files: the agent remembers the paths its changes made, and
  ignores the first event about each. Otherwise a rule that renames files
  would rename them for ever.
- A rule proposes for its own file. It never cancels the preview you are
  looking at, which a new request of yours would.
- A folder the core cannot watch (outside the roots of `fs:watch`, not there,
  or more than 16 folders) is a **blocked** rule with the reason. A watch that
  ends by itself (the folder was deleted) blocks it the same way.

The chat page's Rules button lists the rules with their state (watching,
paused, blocked), and has a form to add one.

## The audit log

One JSON line for each request, in the plugin's data folder,
`%LOCALAPPDATA%\CabinetOS\plugins-data\agent\audit.<date>.jsonl`, one file a
day. It has the time, the tier, where the request came from (`ask`, `chat`),
what you asked, what the window showed, the provider and model, the model's
replies with each command line and what became of it (`ok`, `proposed`,
`applied`, `error: ...`, `blocked`), the preview and the jobs, and the error
if it failed. It never holds a key: the agent has none. The chat page's Log
button shows the last entries.

## The chat page

`agent-chat` is a Tool Extension ([tool-extensions.md](tool-extensions.md)):
`placement` is `dock`, it opens no file (`accepts` is empty) and it has a
page in the sidebar. It shows what you asked, the agent's words, the changes
it proposes and the command lines it ran (open, when one failed), the three
tiers as buttons, Undo, the watch rules and the Log. Drag files from a pane onto the page to
point the agent at them; otherwise the selected files go along.

The page speaks the window's page protocol only. It asks the window to run
commands, and the plugin answers with events:

| Page sends | Why |
|---|---|
| `ready`, `subscribe { plugin: "agent" }` | Start, and follow the agent's events (again after every `ready`). |
| `command agent.chat { message, paths }` | A message. |
| `command agent.tier { tier, input }` | Set the tier; with an empty `input`, read it. |
| `command agent.undo`, `command agent.audit { n }` | Undo; the log. |
| `command agent.rule.list { quiet }`, `agent.rule.add { folder, rule, input }`, `agent.rule.remove { folder, input }`, `agent.rule.resume { folder, input }` | The watch rules. The `input` keeps the window from asking for the text in a box. |

| Event of the agent (`plugin-event`) | Payload | The page shows |
|---|---|---|
| `agent.reply` | `source`, `prompt`, `text`, `commands` (`line`, `kind`, `status`), `changes`, `tier` | The answer. |
| `agent.error` | `text` | An error line. |
| `agent.notice` | `notice` | A line of news. The window also shows it in the status bar. |
| `agent.preview` | `preview` | Nothing but a line: the window opens the preview in the other pane. |
| `agent.tier` | `tier`, `label` | Which tier is set. |
| `agent.audit` | `entries` | The log. |
| `agent.rules` | `rules` (`folder`, `rule`, `status`, `why`, `origin`) | The watch rules. |
| `badge` | `view: "agent-chat"`, `kind: "spinner"` or `null` | Nothing: the window marks the chat's button in the rail while a request or a rule runs ([tool-extensions.md](tool-extensions.md), "The sidebar page"). |

The window's general rules make this work without special code: an event
whose payload has a string `preview` opens that preview in the other pane, and
one with a string `notice` shows it in the status bar
([tool-extensions.md](tool-extensions.md)). So `agent.ask` names its preview
in its result only, and `agent.chat` names it in `agent.preview`: never both,
or the window would open it twice.

## What the window allows

A page may run the commands of the window's fixed list, and the commands
of a plugin it follows: a command whose source is that plugin and whose id
starts with the plugin's id (`agent.`), once the page has sent
`subscribe { plugin: "agent" }` ([tool-extensions.md](tool-extensions.md)).
The chat page subscribes first, so `agent.chat`, `agent.tier`,
`agent.undo`, `agent.audit` and the rule commands run without any name of
this extension in the window.

## Testing without a model

`provider: "fake"` reads canned replies from `fake-replies.json` (or the file
`fakeReplies` names) in the plugin's data folder, one reply for each model
call, and writes every request it got to `fake-requests.jsonl` beside it. No
network is used. The tests of the plugin and the core use it and never call a
real model:

- `cargo test` in [sdk/extensions/agent/plugin](../../sdk/extensions/agent/plugin)
  runs the plugin's logic as ordinary Rust tests, with a fake host.
- `core/crates/cabinetos-plugins/tests/host.rs` loads the built WebAssembly
  (`sdk/fixtures/plugins/agent`) and runs `agent.ask` to a `preview_listing`
  and `agent.undo` after a `preview_apply`, with real reads of a real folder,
  and a new file in a watched folder to a preview.
- `core/crates/cabinetos-market/tests/install.rs` installs the plugin and the
  chat as the marketplace would.

## Building and packing

```text
powershell -ExecutionPolicy Bypass -File <repo>\sdk\extensions\build-extensions.ps1
powershell -ExecutionPolicy Bypass -File <repo>\sdk\marketplace\build-index.ps1 -OutDir <folder> -Extensions
```

The first builds the plugin (`wasm32-wasip2`), puts `plugin.wasm` next to
`plugin.json` and refreshes the committed test copy in
`sdk/fixtures/plugins/agent`. The second packs the plugin (a zip of
`plugin.json` and `plugin.wasm`) and the chat (a zip of its folder) as the
items `agent` and `agent-chat` of a local index ([marketplace.md](marketplace.md)).
Nothing is uploaded: publishing to the public marketplace is the creator's
step.

## Limits, and what it does not do

- One model call at a time: a request waits for the one before it.
- It reads names, sizes and dates. It does not read what is inside files.
- It cannot change settings, read secrets, run a program or reach any
  network host the manifest does not name.
- The model can be wrong. That is the reason for the preview, and for undo.

## Claude Code in the terminal

You can also run Claude Code, the command-line program, from the `claude`
terminal profile: Ctrl+` opens the terminal, and the `terminal.new` command
with `{"profile": "claude"}`, or the dock's profile list, starts it in the
active pane's folder, on your Claude subscription, so CabinetOS holds no key
for it ([terminal.md](../terminal.md), "Profiles"). It finds `cabinetos-cli`
(and `cab` in a release) on the `PATH`, and a note added to its system prompt
tells it so. Keys go to it as to any shell, and the folder sync leaves it
alone. It does not get this extension's preview-before-change flow: it asks
its own permission questions, and CabinetOS shows no preview. A copy, move or
delete it runs through `cabinetos-cli` is a job in the undo journal, and what
it does with its own tools is not.
