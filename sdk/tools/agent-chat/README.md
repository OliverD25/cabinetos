# Agent Chat

The chat page of the Agent extension ([docs/extensions/agent.md](../../../docs/extensions/agent.md)),
a Tool Extension ([docs/tool-extensions.md](../../../docs/tool-extensions.md)).
It needs the plugin `agent` ([sdk/extensions/agent](../../extensions/agent/plugin)):
without it the page has nothing to talk to.

It opens no file. It has a page in the sidebar (`sidebar: true`), shows what
you asked and what the agent did, has buttons for the three tiers, Undo and
the Log, and takes files dragged from a pane as what to work on.

| File | What |
|---|---|
| `tool.json` | The manifest. |
| `index.html`, `chat.css`, `chat.js` | The page. No library: plain HTML, CSS and JavaScript, and the model's words are set with `textContent`, never as HTML. |

The page sends `ready`, `subscribe { plugin: "agent" }` and the plugin's
commands, and it reads `plugin-event` and `paths-dropped`; the details are in
[docs/extensions/agent.md](../../../docs/extensions/agent.md), "The chat
page". Its Content-Security-Policy allows scripts and styles from the tool
only, and no connection at all.

To use it while working on it, start CabinetOS with
`--tools-dir <this repository>\sdk\tools`.
