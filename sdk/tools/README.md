# sdk/tools — Tool Extensions

Tool Extensions are web pages the window shows beside the files, each in a
WebView2 of its own ([../../docs/tool-extensions.md](../../docs/tool-extensions.md)).
None is part of CabinetOS itself (Constitution Article 10).

| What | Contents |
|---|---|
| [tool.schema.json](tool.schema.json) | The JSON Schema of `tool.json`, for editors. The window reads the file strictly and says what is wrong in its log. |
| [markdown-preview/](markdown-preview/README.md) | The first tool: a Markdown file as formatted text, in a pane. Also the example to start a new tool from. |
| [agent-chat/](agent-chat/README.md) | The chat with the Agent plugin ([../../docs/extensions/agent.md](../../docs/extensions/agent.md)): it opens no file, has a page in the sidebar, and follows a plugin with `subscribe`. |

To use a tool from here while working on it, start CabinetOS with
`--tools-dir <this folder>`. To install one, copy its folder to
`%LOCALAPPDATA%\CabinetOS\tools\`.
