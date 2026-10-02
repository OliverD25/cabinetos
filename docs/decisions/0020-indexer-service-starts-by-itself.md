# ADR 0020: The indexer service starts by itself after Windows restarts (automatic start, delayed)

- Status: accepted
- Date: 2026-10-02
- Decided by: the planning session, as Phase 22, unit 3 ([PLAN.md](../PLAN.md);
  the creator's word of 2026-10-02, 20:56: "do all except the Linux core").
  It amends [ADR 0009](0009-packaging.md), which said the service has a
  manual start.

## Context

`cabinetos-indexer --install` registered the service with the start type
manual (ADR 0009, open question 4 of the plan). That kept the install to one
UAC prompt and kept the service from running when nobody asked, but after
every restart of Windows the service was off until someone ran `sc start`.
Search then fell back to the slower walk of one folder tree
([indexer.md](../indexer.md), "Without the indexer") without saying why, and
[release.md](../release.md) listed it as a known gap.

## Decision

`--install` registers the service as **automatic with a delayed start**
(`SERVICE_AUTO_START` and `SERVICE_CONFIG_DELAYED_AUTO_START_INFO`) and
**starts it once** after registering it, so it runs at once and after every
restart of Windows by itself. `--uninstall` stays the reverse: it stops the
service and deletes it. The install stays behind the one UAC prompt (an
elevated terminal), and `install.ps1 -AllUsers -Indexer` keeps its
conditions: Program Files only, because the service runs as LocalSystem.

Delayed means Windows starts the service a little after its other automatic
services (about two minutes after the boot), so building the index, which
reads the whole MFT, does not slow down the start of Windows itself
(Article 1).

It is made with the `windows-service` crate the indexer already uses:
`Service::set_delayed_auto_start` calls `ChangeServiceConfig2W` with
`SERVICE_CONFIG_DELAYED_AUTO_START_INFO`, so no `unsafe` block and no new
dependency were needed. The handout suggested the `windows` crate with
`unsafe` blocks; the result asked for is the same.

## Consequences

- **The index is there after every restart**, without anyone starting the
  service. A test in the indexer crate reads the registration (start type
  automatic, the delayed flag, `LocalSystem`, the command line) without the
  service manager, and `ui/livecheck/vm-indexer-check.ps1` restarts the VM
  and reads the service. Installing a service needs an elevated process and
  VirtualBox's guest control cannot give one (UAC), so the install in the VM
  is one manual step ([release.md](../release.md), "The indexer service").
- **A resident process.** With the service installed, `cabinetos-indexer`
  runs from the boot, with or without a CabinetOS window: about 87 bytes
  per entry (Phase 6), so about 120 MB for a volume of 1.4 million files.
  It is opt-in, as Article 10 asks: the service exists only after `-Indexer`
  or `--install`, and `--uninstall` removes it.
- **`--install` fails loudly when the service does not run after its first
  start** (not Running within 20 s): the error names the state and the log
  folder, and the service stays registered.
- **An earlier manual-start service is not changed** by `--install`, which
  creates a service and fails when one exists: `--uninstall`, then
  `--install`. No released version had a manual-start service (0.1.0 is not
  published).
- **To undo:** `ServiceStartType::OnDemand` and no `set_delayed_auto_start`
  and no `start` in `service.rs`; the texts in `main.rs`, `install.ps1`,
  `indexer.md` and `release.md`.
