# sdk/tools — Tool Extensions

Tool Extensions are web pages the window shows beside the files, each in a
WebView2 of its own ([../../docs/tool-extensions.md](../../docs/tool-extensions.md)).
None is part of CabinetOS itself (Constitution Article 10).

| What | Contents |
|---|---|
| [tool.schema.json](tool.schema.json) | The JSON Schema of `tool.json`, for editors. The window reads the file strictly and says what is wrong in its log. |
| [quickview-messages.schema.json](quickview-messages.schema.json) | The web messages between the window and a viewer page in the Quick View panel ([ADR 0023](../../docs/decisions/0023-quick-view-viewer-contract.md)). The test viewer is [../fixtures/tools/quickview-fixture](../fixtures/tools/quickview-fixture/README.md). |
| [markdown-preview/](markdown-preview/README.md) | The first tool: a Markdown file as formatted text, in a pane. Also the example to start a new tool from. |
| [image-viewer/](image-viewer/README.md) | A Quick View viewer ([ADR 0023](../../docs/decisions/0023-quick-view-viewer-contract.md)): Space on a picture (JPEG, PNG, GIF, WebP, AVIF, BMP, ICO, SVG; HEIC, TIFF, JPEG XR and camera RAW drawn by Windows) shows it fitted to the panel, with zoom and pan. Opens nothing in a pane. |
| [media-viewer/](media-viewer/README.md) | A Quick View viewer: Space on a video (MP4, M4V, MOV, WebM, MKV) or a sound file (MP3, M4A, AAC, FLAC, WAV, OGG, Opus, WebA) plays it at once, with keys for play, seek, volume and mute. Opens nothing in a pane. |
| [agent-chat/](agent-chat/README.md) | The chat with the Agent plugin ([../../docs/extensions/agent.md](../../docs/extensions/agent.md)): it opens no file, has a page in the sidebar, and follows a plugin with `subscribe`. |

The two viewers are the viewer pack of Quick View: `build-index.ps1 -Viewers` ([../marketplace/build-index.ps1](../marketplace/build-index.ps1)) offers them in the marketplace index.

To use a tool from here while working on it, start CabinetOS with
`--tools-dir <this folder>`. To install one, copy its folder to
`%LOCALAPPDATA%\CabinetOS\tools\`.
