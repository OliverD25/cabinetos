# ui/ — the WinUI 3 frontend (C# on .NET)

Not started. This folder receives the C# solution in Phase 5 of
[../docs/PLAN.md](../docs/PLAN.md).

What will live here: `CabinetOS.exe`, the visual layer only. It draws pixels
and captures keystrokes (brief §1, the Dumb UI Rule), launches
`cabinetos-core.exe`, talks to it over the named pipe, and reads directory
listings from shared memory without copying.

Language decision: [ADR 0001](../docs/decisions/0001-frontend-language-csharp.md).
Setup: [../docs/dev-setup.md](../docs/dev-setup.md), section "Phase 5".
