# The public-repository preparation (Phase 22, unit 6, 2026-10-02, evening)

Context: the creator decided this evening that the repository
`OliverD25/cabinetos` goes public. This unit prepares everything up to that
step: a scan of the whole git history for secrets and private things, the
repository read as a first visitor reads it, and the publish commands ready
to paste. Done by the Opus coder agent in the worktree branch
`worktree-agent-abef79476272136f0`, on the main PC. Nothing was published,
pushed or changed on GitHub: every call to GitHub was a read. No window, no
laptop, no VM.

## Summary

| Question | Answer |
|---|---|
| A secret in the history? | **None.** gitleaks found 3 matches in the history and 2 in the working tree, all one false positive in the minified `xterm.js`. The second pass found no token, key, password value or private key of any kind, in 778 commits and 4,312 files (binary files too) |
| What must happen before the flip | Nothing must. One decision is the creator's: every commit carries the creator's personal e-mail address as author and committer ("The e-mail in every commit", below) |
| Private but harmless | Private paths and machine names in the build log, the creator's paste-ready commands and a few test files; the Notion board's ids; the Claude Design project id; screenshots that show the drive labels and the home folder |
| What changed in the repository | `README.md` rewritten for a first visitor (ce23e43); the publish sheet in `docs/release.md`, "Publish" (24d9f58, 5a06f06); this report, its row, and the plan's status line |
| Where the publish sheet is | [docs/release.md](../../release.md), section "Publish": the e-mail decision, then (a) the flip, (b) the CHANGELOG cut and the build, (c) the GitHub Release, (d) the marketplace site, (e) winget |

## 1. The scan

### How it was scanned

- **gitleaks 8.30.1**, installed for this user with winget for this scan.
  `gitleaks git <main checkout> --redact` read the full history: 769
  commits with changes (778 commits are reachable from every branch; the
  other 9 are merges with no diff of their own). `gitleaks dir --redact`
  read the working tree of `main`'s head, 2a71b7d, in this worktree.
- **The second pass**, a Python script over `git log -p -U0 --all`: every
  added and removed line of every commit, and every commit message, of the
  778 commits, searched for each kind below. A second script read every
  file version reachable from every branch (4,312 of them, the binary
  ones too, as bytes), because the diff cannot see inside `.wasm`, `.png`
  or `.ico` files.
- **The VM user's password** was searched by its value: the script read it
  from `_io\vm\vm-user.txt` and never printed it. A search for the file's
  name alone cannot tell whether the value leaked.
- **What GitHub holds.** `git ls-remote origin` lists only `main` (759
  commits) and `HEAD`. The local branches `claude/funny-easley-6da534`,
  `worktree-agent-a21066120fda85fae` and this one exist only on this PC and
  stay private unless someone pushes them. All the private kinds below live
  on `main` itself: the counts over `main` alone are the same.

### The kinds, with counts and verdicts

| Kind | Commits | Files | Examples | Verdict |
|---|---|---|---|---|
| gitleaks `generic-api-key` | 1 (2769f2f) | 1 | `ui/CabinetOS/Assets/xterm/xterm/xterm.js`, the bundled xterm.js: `t.FourKeyMap=t.TwoKeyMap=void 0` and `t.SequencerByKey=…`, minified names | Fine: a false positive |
| The VM password's value | 0 | 0 of 4,312 | none | Fine |
| The file name `vm-user.txt` | 4 | 4 | `ui/livecheck/vm-livecheck.ps1` and `vm-install-check.ps1` read the file at run time; `docs/ui.md` and `docs/log/2026-10-02/terminal-unit6-report.md` say where it is | Fine: the place, not the content. The VM's user name is `cabinetos`, the project's own name |
| `password:` and `password=` lines | 4 | 4 | the two VM scripts' line that parses the file; `password=hunter2`, the masking test in `ui/CabinetOS.Tests/HeavyLogTests.cs`; `account_password: None` in the indexer's service code | Fine |
| Telegram bot tokens (`bot<digits>:`, the token's shape, `chat_id`, `t.me/`, the word "Telegram") | 0 | 0 | none | Fine |
| GitHub tokens (`ghp_`, `github_pat_`, `gho_`, `ghs_`, `ghu_`, `ghr_`) | 0 | 0 | none | Fine |
| Notion secrets (`secret_` or `ntn_` with a token's length) | 0 | 0 | The loose search for `secret_` matched 141 lines in 16 commits and 35 files: all are the names of the core's secret store messages (`secret_set`, `secret_get`, `secret_header`, `secret_error`) | Fine |
| SSH and PEM private keys (`BEGIN OPENSSH`, `PRIVATE KEY-----`) | 0 | 0 | none | Fine |
| `.env` contents | 0 | 0 | No file named `.env` was ever committed, nor `*.pem`, `id_rsa*`, `*.pfx`, `*.key` or `settings.local.json` | Fine |
| Model API keys (`sk-ant-`, `sk-proj-`, long `sk-`), AWS keys | 0 | 0 | One made-up test value, `sk-test-NET-DO-NOT-LOG-41d8`, in `core/crates/cabinetos-core/tests/plugins.rs` | Fine |
| The creator's e-mail address in file content | 0 | 0 | none in content on `main`. This unit's commit 24d9f58 wrote it once into `docs/release.md`; 5a06f06 took it out again | Fine |
| The creator's e-mail address in commit metadata | 778 (759 on `main`) | — | the author and the committer of every commit | **Private; the creator decides** ("The e-mail in every commit", below) |
| Other e-mail addresses | — | 5 | `noreply@anthropic.com` (the co-author lines), `someone@example.org` (a test), `user:pass@api.anthropic.com` (a test of addresses with a password in them), the two theme authors' addresses in `sdk/themes/collection/NOTICES.md`, from their MIT licences | Fine |
| `C:\Users\Admin` (any slash, also `/mnt/c/Users/Admin`) | 15 | 30 at `main`'s head: 23 in `docs/log/`, 5 plugin fixtures, 1 test, 1 skill | the fixtures `sdk/fixtures/plugins/*/plugin.wasm` carry Cargo's registry paths (`C:\Users\Admin\.cargo\registry\…`) in their panic messages; `ui/CabinetOS.Tests/SidebarTests.cs` uses the folder as test data (93 lines); `.claude/skills/design-handouts/SKILL.md` names `C:\Users\Admin\.local\bin` | Private but harmless: `Admin` is Windows' generic account name |
| `C:\Users\Omen` | 5 (3 files, 2 commit messages) | 2, both in `docs/log/` | `docs/log/2026-10-01/live-check-portability-report.md` | Private but harmless |
| `E:\codespace\_claude_code\_rde` (also `/mnt/e/…`) | 20 | 32 at `main`'s head: 26 in `docs/log/` | outside the log: `docs/release.md`, `docs/dev-setup.md`, `docs/ui.md`, `.claude/skills/heavy-logging/SKILL.md`, where they are the creator's paste-ready commands, which carry their absolute path by the creator's own rule | Private but harmless. This unit's publish sheet adds more, for the same reason |
| The laptop's name `RD-Omen-Laptop` | 10 (with 1 commit message) | 8 (7 at the head: 6 in `docs/log/`, and `docs/PLAN.md`) | names of run files, such as `run-2026-10-02-1449-rd-omen-laptop.txt`. The live check's laptop scripts use the SSH alias `omen` and the folder `C:\Dev\cabinetos` | Private but harmless |
| The PC's name `RDE-HOME-PC` | 0 | 0 | none | Fine |
| The homelab's host names (`rdeserverpc`, `rdehomelab2`), `rde@`, `.ts.net` | 0 | 0 | The word "homelab" appears in 2 commit messages (line endings for WSL and the homelab) | Fine |
| The Notion board's ids | 1 (51bf1e7) | 1 | `.claude/notion-desk.json`: the board, its view and its data source | Private but harmless: the board is private, and an id gives no access without the creator's Notion login |
| The Claude Design project id | 1 (18a8c72) | 1 | `.claude/skills/design-handouts/SKILL.md`, the id and its `claude.ai/design` address | Private but harmless, for the same reason |
| Home-network addresses: `192.168.`, `10.` (not the SDK's `10.0.` versions), `172.16` to `172.31`, Tailscale's `100.64` to `100.127` | 0 | 0 | none. No MAC address and no phone number either | Fine |
| Screenshots | — | 62 images; 57 are screenshots in `docs/log/` | Six were looked at. They show the labels of this PC's eleven drives in the sidebar and the `Admin` profile. `docs/log/2026-09-28/live-window.png` lists the names of the folders in the home folder (which tools are installed) and in Documents; `docs/log/2026-09-30/claude-code-in-the-dock.png` shows a Claude Code session in the dock | Private but harmless: no secret is visible. Deleting them from the current tree would not take them out of the history |

Private paths in the `docs/log/` reports are expected: the reports record
where each run happened. They are harmless and are not listed line by line.

### The e-mail in every commit

Every one of the 778 commits has the same author and committer: the name
`D25` and the creator's personal e-mail address (from the git settings in
`E:\codespace\.gitconfig`; in WSL the name is `Oliver` with the same
address, so commits made in WSL say `Oliver`). The GitHub profile keeps the
address private today (`email: null` in the public profile), and the
repository being private hides the commits. After the flip, anyone who
clones the repository, or opens a commit's `.patch` page, sees the address.

- **It is not a secret.** Nobody can sign in with it. The risk is spam and
  the link between the account and the address.
- **Only a history rewrite removes it** (for example `git filter-repo` with
  a mailmap, then a force-push of `main`). This session never does that.
  It would also give every commit a new id: the plan and the build log cite
  hundreds of commit ids, which would all point at nothing, and every
  clone (the laptop, the homelab, the worktrees) would have to be made
  again.
- **Recommendation: keep the history**, and give new commits GitHub's
  private address, `197441449+OliverD25@users.noreply.github.com` (the
  account's id 197441449 is from GitHub's public API). The first block of
  the publish sheet sets it for this repository. When all machines use
  it, GitHub's setting "Block command line pushes that expose my email"
  keeps the old address out from then on.

### Not in the repository, but worth knowing

- **The release programs will carry build paths.** Rust writes the source
  path of a panic's location into the program, so `cabinetos-core.exe` and
  the others will contain `E:\codespace\_claude_code\_rde\…\core\crates\…`
  and `C:\Users\Admin\.cargo\registry\…`, and each program names the full
  path of its `.pdb` file. Harmless; a later small change could add
  `--remap-path-prefix` to the rustflags in `core/.cargo/config.toml`.
- **The failed Actions runs of 2026-09-28** (billing, 3 to 5 seconds each)
  and their logs become visible with the repository. Harmless.

## 2. The repository as a first visitor sees it

| Item | What is there | What changed |
|---|---|---|
| `README.md` | The North Star words and the four architecture lines were good. The status was one long sentence that counted phases and tests as of 2026-09-28, said "no release is published yet" and had no way to build from a fresh clone or to report a problem | Rewritten (ce23e43): a short status list that stays true before and after 0.1.0 is published, what is not there yet, install from the Releases page, a build-from-source block that works from a fresh clone, "Problems and ideas" (issues, the version, the crash trace), and the licence. The documents and layout tables stay |
| `LICENSE` | MIT, "Copyright (c) 2026 OliverD25 and the CabinetOS contributors", as ADR 0003 decided. The year is right. GitHub detects it as "MIT License" | Nothing. The name differs elsewhere: `ui/Directory.Build.props` (`<Copyright>`) and `build/setup.iss` (`VersionInfoCopyright`) say "the CabinetOS authors", the winget manifest says "OliverD25 and the CabinetOS contributors", and the git author is `D25` (or `Oliver` in WSL). The creator chooses one |
| `THIRD-PARTY-NOTICES.md` | Not in the repository. `build/notices.ps1` writes it into each release folder, so it ships in the zip and in the setup's install. The repository carries the licences of the code it bundles next to that code (`ui/CabinetOS/Assets/xterm/*/LICENSE`, `sdk/tools/markdown-preview/marked/LICENSE`) and the themes' `sdk/themes/collection/NOTICES.md`; the Rust crates and NuGet packages are only named in `Cargo.lock` and the project files, not copied | Nothing: that is enough for the source repository. The README now says the release carries the file |
| `CONTRIBUTING` | None | None written. Article 2 aims at a community, but neither it nor the plan asks for outside contributions now. The README's "Problems and ideas" says this is the first release and how to report a problem |
| `.github/workflows/ci.yml` | Runs on `workflow_dispatch` and on pull requests into `main` that touch code (its path list leaves out `docs/`); each job cancels its own older run | Nothing |
| `.github/workflows/release.yml` | Runs on `workflow_dispatch` only; publishes nothing | Nothing |
| Issue and pull request templates | None | None added: not needed for a first release |
| Description and topics | Description today: "CabinetOS — Modern System Commander. A dual-pane Windows file manager with VS Code-style extensibility." No topics | A description and 16 topics are in the sheet's optional block after (a) |

**What runs at the flip.** Nothing starts by itself: no workflow has a
`push` or `schedule` trigger. After the flip, GitHub's own runners cost no
minutes for a public repository, so `ci.yml` runs free on the next pull
request into `main` that touches code, or by hand. The actions it names
exist in those versions (checked: `actions/checkout@v7`,
`actions/setup-dotnet@v6`, `actions/cache@v6`, `actions/upload-artifact@v7`,
`Swatinem/rust-cache@v2`, `EmbarkStudios/cargo-deny-action@v2`). Its jobs
are the checks every report runs locally, last green at 39705c8 and
31babbe ([short-paths-and-icon-report.md](short-paths-and-icon-report.md)).
Not known: whether the account's billing state also stops jobs of a public
repository. If a first run fails after a few seconds with zero steps, the
`github-actions-budget` skill's procedure applies: nothing is bought.
`release.yml` does not make the setup file (the runner has no Inno Setup,
so `release.ps1` skips that step with a warning) and keeps only the zip,
its hash and the winget manifests, not the setup file or the symbols zip.
That belongs to the CI card on the desk, which comes after the flip.

**GitHub's state, read on 2026-10-02.** Private; only `main` on GitHub; no
release, issue or pull request; no Actions secret or variable; one
collaborator (`OliverD25`); issues on, wiki and discussions off; Actions
allowed for all actions. The marketplace repository
`OliverD25/cabinetos-marketplace` is public already, and GitHub Pages
serves its `main` branch from the root.

### `.claude/` in the repository

| Part | State | What it holds |
|---|---|---|
| `.claude/settings.json` | Committed | The "ask" rule and the `PreToolUse` hook that protect `CONSTITUTION.md` |
| `.claude/hooks/protect-constitution.js` | Committed | That hook |
| `.claude/skills/design-handouts/SKILL.md` | Committed | The path from the creator's Claude Design session; its project id |
| `.claude/skills/heavy-logging/SKILL.md` | Committed | How to use the heavy logging mode |
| `.claude/notion-desk.json` | Committed | The Notion desk binding: the board's, view's and data source's ids |
| `.claude/settings.local.json`, `.claude/worktrees/` | Ignored by `.gitignore` | Local permissions; the agents' worktrees |

**Recommendation: keep the committed parts public, as they are.** None
holds a secret. The hook and the two skills help anyone who works on
CabinetOS with Claude Code, and they show how the project is built. The
two ids give a stranger nothing without the creator's Notion and Claude
logins. The desk tooling reads `notion-desk.json` from the repository on
every machine (this PC, the laptop, the homelab), so taking it out would
break the desk there, and the history keeps it anyway. The other way, if
the creator prefers it: `git rm --cached .claude/notion-desk.json` and a
line in `.gitignore`, then copy the file by hand to each machine.

## 3. The release notes and the publish sheet

The sheet is in [docs/release.md](../../release.md), section "Publish",
replacing "Publish (not done yet)". Every step is a WSL bash block for the
creator, with what it changes, what to check afterwards, and its undo:

- **Before the flip:** the e-mail decision, and the command that gives new
  commits the private address.
- **(a) The flip:** `gh repo edit OliverD25/cabinetos --visibility public --accept-visibility-change-consequences`,
  with a check that GitHub says `PUBLIC` and the page answers without a
  login. Optional after it: the description, the topics, and GitHub's
  private vulnerability reporting.
- **(b) After Phase 22's other units land:** the CHANGELOG cut, then a
  commit, a push and `build/release.ps1` (PowerShell 7), with checks that
  `release.json` names that commit with no uncommitted changes and that
  the notes are the 0.1.0 section.
- **(c) The GitHub Release:** the tag `v0.1.0` and `gh release create`
  with the zip, the setup file and the symbols zip, each with its
  `.sha256`, and the notes file. It first checks that all six files exist,
  that the build is of the current commit and that the commit is on
  GitHub; afterwards, that the zip downloaded from the release has the
  built hash.
- **(d) The marketplace site:** it is not cloned on this PC (nothing
  named like it under `E:\codespace`); the block clones it into
  `E:\codespace\_claude_code\_rde\cabinetos-marketplace\cabinetos-marketplace`,
  copies `latest.json` and `notes-0.1.0.md` into `update/stable/`, commits
  and pushes. The check waits for GitHub Pages and compares the hash; then
  `cab update check` in an installed copy's terminal must say "up to date".
- **(e) winget:** last and optional, only when winget carries the Windows
  App Runtime 2.5, with `wingetcreate submit`.

**The CHANGELOG is not cut now**: the other Phase 22 units still add to
Unreleased. The cut is step (b). It is a merge, not a rename: `CHANGELOG.md`
already has a `## [0.1.0] - 2026-09-30` section, and `release.ps1` takes
the notes from `## [0.1.0]` first. The old sheet called that step "done",
so the published notes would have missed everything under Unreleased
since 2026-09-30. The block moves Unreleased's entries under the 0.1.0
section's headings, dates it, and leaves an empty Unreleased. It was run on
a copy of today's CHANGELOG, in Windows' Python and in WSL's python3 3.12:
all 103 lines that are not headings are kept, the headings are Added,
Changed, Removed and Fixed, and the notes `release.ps1` would take hold
both an entry of 2026-09-30 ("Two file panes…") and one from Unreleased
("Toggle Compact Overlay…"). A second run stops without a change. Every bash
block of the sheet passes `bash -n` in WSL.

## Checks

| Check | Result |
|---|---|
| `gitleaks git` (full history) | 769 commits scanned, 3 findings, all the `xterm.js` false positive |
| `gitleaks dir` (working tree of 2a71b7d) | 2 findings, the same false positive |
| Second pass over 778 commits, and over 4,312 files as bytes | the counts in the table above |
| README links | every relative link points at an existing file |
| Publish sheet | 11 bash blocks pass `bash -n`; the CHANGELOG cut ran on a copy (see above) |
| Build and tests | not run: this unit changed only Markdown files |

## Decisions (what — because — undo)

- **gitleaks installed for this user with winget**
  (`winget install --id Gitleaks.Gitleaks --exact --scope user`, version
  8.30.1, with winget's source and package agreements accepted on the
  command line) — because the task named it and it was missing — undo:
  `winget uninstall --id Gitleaks.Gitleaks --exact`.
- **The second pass ran in this worktree, gitleaks on the main checkout's
  folder** — because the session allows git commands only in its own
  worktree, and a worktree shares the main checkout's commits and branches,
  so `--all` reads the same 778 commits — undo: nothing to undo.
- **The VM password was searched by its value**, read inside the script and
  never printed — because only the value shows whether it leaked — undo:
  nothing to undo (read only).
- **No private path in a current file was edited** — because each one is a
  paste-ready command that the creator's rule wants with its absolute path,
  test data, a compiled test fixture, or the record of a run in
  `docs/log/` — undo: nothing to undo.
- **No screenshot was removed** — because deleting files is not this
  session's to do, and the history keeps them anyway — undo: nothing to
  undo.
- **The README was rewritten in place**, with the documents and layout
  tables kept — because its status and install text were out of date for a
  first visitor — undo: `git revert ce23e43`.
- **No CONTRIBUTING, SECURITY or template files** — because neither
  Article 2 nor the plan asks for outside contributions yet; the README
  says how to report a problem, and the sheet offers private vulnerability
  reporting as an optional step — undo: nothing to undo.
- **The CHANGELOG cut merges Unreleased into the existing 0.1.0 section**
  instead of renaming Unreleased — because a rename would leave two 0.1.0
  sections and `release.ps1` would take the older one — undo: rewrite
  step (b) of the sheet.
- **The marketplace clone gets a folder of its own,
  `…\_rde\cabinetos-marketplace\cabinetos-marketplace`** — because the
  creator's layout rule allows only `_io` and one repository in CabinetOS's
  root folder, and the old sheet put the clone next to `cabinetos` — undo:
  change the paths in step (d).
- **The tag is annotated, and step (c) checks the files, the build's commit
  and GitHub's `main` first** — because the release must be the build of
  the commit it is tagged on — undo: the old one-line command, in the git
  history of `docs/release.md`.
- **The address was taken out of `docs/release.md` with a new commit
  (5a06f06), not by changing 24d9f58** — because changing a commit is a
  history rewrite. 24d9f58's text keeps the address, which every commit's
  metadata already shows — undo: nothing to undo; the branch can be
  squashed when it is merged.
- **The commits end with "Co-Authored-By: Claude Opus 5.5"**, not
  "Claude Opus 5" as the handout said — because that is the model that ran
  this session and the session's attribution instruction names it — undo:
  nothing to undo.
- **Nothing was pushed.**

## Needs the creator

1. **The e-mail in every commit**: keep the history and switch new commits
   to the private address (recommended; the sheet's first block), or a
   history rewrite before the flip, which changes every commit id.
2. **One copyright name**: "OliverD25 and the CabinetOS contributors"
   (`LICENSE`, ADR 0003, winget) or "the CabinetOS authors"
   (`ui/Directory.Build.props`, `build/setup.iss`).
3. **The screenshots** in `docs/log/`, if any should leave the current
   tree; `live-window.png` shows the most (the home folder's names).
4. **`.claude/`**: keep it public as it is (recommended), or untrack
   `notion-desk.json`.
5. **The flip and everything after it**: the sheet in
   [docs/release.md](../../release.md), "Publish".
