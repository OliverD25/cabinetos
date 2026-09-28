# ui/ — the WinUI 3 frontend (C# on .NET)

`CabinetOS.exe`, the visual layer only. It draws pixels and captures
keystrokes (brief §1, the Dumb UI Rule), starts `cabinetos-core.exe`, talks
to it over the named pipe, and reads directory listings from shared memory
without copying.

| Folder | What it is |
|---|---|
| `CabinetOS/` | The WinUI 3 app: window, panes, sidebar, command palette, context menu, transfer flyout |
| `CabinetOS.Core/` | Everything testable without a window: pipe client, protocol, `ListingView`, selection, keys and chords, `CommandRouter`, the job client, the clipboard, diagnostics |
| `CabinetOS.Tests/` | xunit v3 tests, including three against the real core |

Build, run, test and debug: [../docs/ui.md](../docs/ui.md). Tools and
package versions: [../docs/dev-setup.md](../docs/dev-setup.md), "Phase 5".
Language decision: [ADR 0001](../docs/decisions/0001-frontend-language-csharp.md).
