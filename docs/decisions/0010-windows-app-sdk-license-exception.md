# ADR 0010: The UI layer depends on Microsoft's Windows App SDK under its own license terms; a recorded exception

- Status: accepted
- Date: 2026-09-28
- Decided by: the architect's recommendation, taken on the creator's
  instruction in chat on 2026-09-28 ("where you can choose your recommended
  option, do this"). The creator can overturn it with a new record.

## Context

Article 2 says the core application and the marketplace infrastructure stay
free and open source; [ADR 0003](0003-license-mit.md) made the project MIT.
[ADR 0001](0001-frontend-language-csharp.md) chose WinUI 3 for the window,
because Article 3 asks for a first-party Windows 11 look (Fluent, Mica), and
WinUI 3 comes only through Microsoft's Windows App SDK.

Facts checked on the development PC on 2026-09-28:

- The NuGet package `Microsoft.WindowsAppSDK` 2.5.1 carries `license.txt`,
  the "Microsoft Software License Terms, Microsoft Windows App SDK". It is a
  proprietary license, not an open-source one. Its section 1 says the
  software may collect information about the user and their use of the
  software and send it to Microsoft, with an opt-out for many cases but not
  all. Its section 3 makes the files the package places next to an
  application distributable, for framework-dependent and self-contained
  apps alike. Its `NOTICE.txt` lists the open-source components inside it.
- The Windows App Runtime, the framework package an unpackaged app loads at
  run time ([ADR 0009](0009-packaging.md)), comes from Microsoft's own
  installer under Microsoft's terms. It is installed per machine and is not
  shipped by CabinetOS.
- The WebView2 SDK package `Microsoft.Web.WebView2` 1.0.3719.77 is under a
  BSD-style license (its `LICENSE.txt`), so it needs no exception. The
  WebView2 Runtime is part of Windows 11 and Edge, under Windows' own terms.
- The source of the Windows App SDK is public under MIT, but the binaries
  CabinetOS builds against are the package's, under the package's terms.

Options considered:

- **Accept the dependence and record it**, bounded to Microsoft's platform
  components.
- **Drop WinUI 3 for a framework under an open-source license.** WPF,
  Windows Forms and Avalonia are MIT, but each meets Article 3 only by
  imitating Fluent and Mica; ADR 0001 chose WinUI 3 for exactly that
  reason.
- **Ship the Windows App SDK self-contained** to spare users the runtime
  installer. The terms are the same, and the zip grows by the runtime.

## Decision

The UI layer (`ui/`) depends on the Windows App SDK package and, at run
time, on the Windows App Runtime, under Microsoft's license terms. This is
a recorded exception to Article 2 with these bounds:

- it covers platform components from the operating system's vendor that are
  free of charge and redistributable, nothing else;
- no CabinetOS code is under those terms; the project stays MIT;
- the core, the indexer, the CLI and the SDK have no such dependency and
  build without the package.

## Consequences

- `THIRD-PARTY-NOTICES.md` in the release folder names the Windows App SDK
  files it carries and their terms (the package's `license.txt` and
  `NOTICE.txt`), as the release script generates it (Phase 10).
- The installer says that the Windows App Runtime comes from Microsoft
  under Microsoft's terms and points at its winget package; CabinetOS does
  not bundle it (framework-dependent, ADR 0009).
- The README's License section says in one sentence that the WinUI 3
  frontend builds on the Windows App SDK under Microsoft's terms.
- A user who objects to the runtime's data collection can use the core and
  the CLI without the window; the window cannot run without the runtime.
- If Microsoft changes the terms, or an open-source UI framework reaches
  Article 3's bar, a new record supersedes this one.
