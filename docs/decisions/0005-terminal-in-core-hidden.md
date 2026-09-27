# ADR 0005: The integrated terminal is core infrastructure, hidden by default

- Status: accepted
- Date: 2026-09-28
- Decided by: the creator, asked in chat (a Constitution interpretation)

## Context

Two articles pull in different directions. Article 9 says the system
"integrates embedded developer tools, allowing users to spawn integrated
terminals". Article 10 says the core ships "zero supplementary tools". The
design handout puts the terminal in the core UI, toggled with Ctrl+`.

ConPTY is Windows's pseudo-console API: it lets an application run a shell
such as pwsh or WSL and show its output in a pane.

Options considered:

- **Core, hidden by default.** The core ships the ConPTY host and the terminal
  pane, switched off until Ctrl+` (Article 4, Progressive Disclosure).
- **First-party Tool Extension, preinstalled.** The core ships only the Tool
  Dock and the ConPTY hooks; the terminal pane is an extension made in this
  repo, installed by default and removable.
- **Optional extension, not preinstalled.** Purest zero-bloat; weakest reading
  of Article 9.

## Decision

Core, hidden by default. Reading: Article 9 names the terminal as a system
feature, so it is infrastructure, not a "supplementary tool". Article 4
handles the disclosure.

## Consequences

- The ConPTY host lives in the Rust core (Phase 8); shell profiles (pwsh, cmd,
  WSL) are configuration in `cabinetos.json`.
- The terminal pane is a native, first-party WinUI control, the one exception
  to the "third-party Tool Extensions run in WebView2" default in PLAN.md
  section 2.
- The same reasoning applies to the marketplace view (PLAN.md consistency
  check row G): it is the way everything else is added, so it is core.
- The line for future requests: a feature is core only if an article names it
  as part of the system. Everything else is an extension (Article 10).
