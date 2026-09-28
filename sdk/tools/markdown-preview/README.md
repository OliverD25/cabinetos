# Markdown Preview

The first Tool Extension ([docs/tool-extensions.md](../../../docs/tool-extensions.md)):
Enter on a `.md` or `.markdown` file shows it as formatted text, in the
other pane when two are shown, else in the same pane. Links to other
Markdown files open them in the preview; web links do not open (tools have
no network).

It is not part of CabinetOS itself (Constitution Article 10): install it
by copying this folder to `%LOCALAPPDATA%\CabinetOS\tools\markdown-preview\`,
or start CabinetOS with `--tools-dir <this repository>\sdk\tools` while
working on it.

| File | What | Source |
|---|---|---|
| `tool.json` | The manifest | This repository |
| `index.html`, `preview.css`, `preview.js` | The page: reads the file the window names, renders it, follows links | This repository |
| `marked/marked.umd.js`, `marked/LICENSE` | marked 18.0.14, MIT | `lib/marked.umd.js` and `LICENSE` of https://registry.npmjs.org/marked/-/marked-18.0.14.tgz, unchanged |

SHA-256 of the copied file:

```text
21568877a938d2c4e7d74e27f18e60da96bb73a68809610ca39216e1efebae62  marked/marked.umd.js
```

marked does not sanitize HTML inside Markdown. The page's
Content-Security-Policy stops it from running: scripts come only from the
tool's own host, inline scripts and event handlers are refused, and the
window blocks every request outside its virtual hosts.
