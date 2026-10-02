# CabinetOS — Modern System Commander

## The Constitution comes first

[CONSTITUTION.md](CONSTITUTION.md) is the highest authority in this project. It is
loaded into every session below. Every design, architecture and feature decision
must be consistent with it.

- **Never edit CONSTITUTION.md without explicit approval from the creator in chat.**
  This covers wording, formatting, order and content. Approval is per change.
  Propose the change as an exact before/after text, then wait for a clear yes.
- **If a request conflicts with a principle, stop and say so.** Name the article
  by number and title (for example "Article 10, Zero-Bloat Foundation"), explain
  the conflict in one or two sentences, and ask how to proceed. Do not quietly
  work around a principle.
- **Cite articles when they drive a decision.** When a design choice follows from a
  principle, say which one. This keeps the link between code and principles visible.
- **When in doubt about scope, default to Article 10.** A feature that is not core
  file navigation belongs in an extension, not in the core application.

## The other governing documents

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) is the implementation brief. Its
  §1 **Prime Directives** bind every session: no file I/O or data processing in
  the UI, never block the UI thread, no features hardcoded into the core, clean
  UI by default. It is a living document: edit it when a decision changes it,
  and add a row to its change log saying why.
- [docs/PLAN.md](docs/PLAN.md) is the development plan: decisions, phases with
  done-when criteria, open questions, risks. A phase starts only when the
  creator says so.
- [docs/decisions/](docs/decisions/README.md) holds one ADR per decision. A new
  decision gets a new file and a row in the index; an old record is superseded,
  never rewritten.
- [docs/design/](docs/design/ABOUT.md) is the committed mirror of the handout
  folder of the creator's Claude Design session. Before any design-driven
  work, the sync rule of the `design-handouts` skill runs: fetch, compare the
  changelog headers, update the folder, commit, report before app code.

## Repository layout

| Folder | Contents |
|---|---|
| `core/` | Rust Cargo workspace (all crates under `core/crates/`) |
| `ui/` | C# WinUI 3 solution (Phase 5 onward) |
| `sdk/` | WIT interface, protocol schema, plugin templates, themes |
| `docs/` | Governing documents |

## Build and test

Rust core (from `core/`):

```bash
cargo build --workspace && cargo test --workspace && cargo clippy --workspace --all-targets -- -D warnings && cargo fmt --all -- --check && cargo deny check
```

CI runs the same five commands on `windows-latest`. A change is not done until
they pass locally. Toolchains and setup: [docs/dev-setup.md](docs/dev-setup.md).

## Working rules

- Commit after each finished unit of work; the message says why. Push after
  each commit.
- Before any script takes the real keyboard and mouse (the live check, the
  Claude Code probe, a coder's real-key driver), it shows the countdown
  window of `ui/livecheck/countdown.ps1` for 5 seconds, with a sound, so
  the creator knows when to let go of both; Esc cancels the run. The
  creator's rule of 2026-10-01. A `DONE.md` in Notepad says when they are
  free again. Scripts that press no key (the speed runner, the snapshot
  steps) need no countdown.
- **Nothing opens a CabinetOS window on this PC without the creator's
  consent.** Their rule of 2026-10-02: twice, test windows popped up over
  their work. Before the first window run of a stretch (the end-to-end
  suite, the live check, the speed runner, a manual window start), the
  session asks in the chat. A yes, or no answer for one minute, is consent,
  and the PC stays free until the creator takes it back ("give me the PC",
  "stop", "no"). A deliberate no means the PC is not used, with no second
  ask until they offer it. The answer is recorded with
  `ui/livecheck/wait-for-pc.ps1 -Set allowed` or `-Set held` (`-Until
  "yyyy-MM-dd HH:mm"` for a time the creator named), and every window run
  on this PC, the session's or a coder's, calls `wait-for-pc.ps1` first: it
  waits until the file says allowed and no CabinetOS.exe runs. Coders'
  handouts carry this; the fast test run (no end-to-end tests) opens no
  window and is always fine. The Omen laptop (`ui/livecheck/remote-livecheck.ps1`,
  `remote-tests.ps1`, `remote-script.ps1`) and the VM (`vm-livecheck.ps1`)
  need no consent.
- **The laptop's numbers are the market's numbers.** The creator's rule of
  2026-10-02: this PC is a strong machine, and most users' machines are
  closer to the Omen laptop. So every merge's live check runs on the
  laptop too (`ui/livecheck/remote-livecheck.ps1`), and the laptop's frame
  numbers (the scroll goal, the frames over 20 and 33 ms) decide whether a
  speed goal is met. The end-to-end suite runs there as well
  (`remote-tests.ps1 -EndToEnd`) when this PC is held or busy. A run on
  this PC alone proves the logic, not the speed. The laptop's dead
  `CabinetOS.exe` processes, which lock its clones after a crashed run,
  end with `taskkill /F /IM CabinetOS.exe` over SSH (seen 2026-10-02); no
  restart is needed.
- Unsafe Rust only in the crates that talk to Windows (`ipc`, `fs`, `jobs`,
  `index`), every `unsafe` block with a `// SAFETY:` comment.
- Project skills live in `.claude/skills/`. `heavy-logging` says when and
  how to use the heavy logging mode to find a fault (a key that did
  nothing, a job that did not do what was asked, a stall, a crash) and how
  to read the chain of one action; read it before chasing such a fault.
  `design-handouts` says how the design session's documents come into
  `docs/design/` (the creator's sync rule of 2026-10-01) and onto the
  desk, read-only towards the session. `release-notes-page` says how the
  public "What's new" page of a release is made for the Luminart site: the
  changelog becomes `notes.md`, `ui/livecheck/release-media.ps1` takes clean
  screenshots and recordings on the laptop over a demo folder, and nothing
  is deployed without the creator's word in the chat. `settings-three-ways`
  holds the creator's rule of 2026-10-03: every preference is reachable
  from the window, from the command palette and from the settings file,
  all three in sync; a unit that adds a setting names all three, and the
  skill carries the audit of the settings that exist.
- Implementation goes to the `coder` agent once the plan is concrete; small
  fixes are done directly.

## The desk

This project's task board (the desk) is bound in `.claude/notion-desk.json`. Its
card "Development Plan (mirror of the repo plan)" is a copy of
[docs/PLAN.md](docs/PLAN.md). The repo file is the source of truth. When
`docs/PLAN.md` changes, refresh that card from the file in the same session.

After a unit of work lands, the creator's global Stop hook (desk-watch, in
the global Claude config) asks the session for one short look at the desk:
a new card in an actionable column that can be worked on with nothing
blocking it is announced in a line and started (the creator's rule of
2026-09-30). At most once in 30 minutes.

@CONSTITUTION.md
