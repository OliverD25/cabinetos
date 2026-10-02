# ADR 0021: The symbols ship in a zip of their own, not in the release zip or the setup file

- Status: accepted
- Date: 2026-10-02
- Decided by: the planning session's recommendation, taken as the decision
  for Phase 22, unit 2 ([PLAN.md](../PLAN.md)), until the creator says
  otherwise. The question was open since Phase 10: "whether the `.pdb`
  files stay in the zip or move to a symbols zip of their own".

## Context

A crash trace names a function, a file and a line only when the program's
`.pdb` file is next to it (Article 12: the trace must pinpoint where the
failure occurred). So `release.ps1` copied the five `.pdb` files into the
release folder, and they went into the zip and into the setup file.

They are the largest part of both. Measured on 2026-10-02 for 0.1.0, the
release folder had 71 files and 251.7 MB; the five `.pdb` files are 151 MB
of that (`cabinetos_core.pdb` alone is 116 MB), and zipped they are
43.6 MB of the 75.7 MB zip. The setup file was 42.5 MB. Nobody needs them
to run CabinetOS, and a first download of 76 MB for a file manager is a poor
start (Article 1: speed is the ultimate metric, and a download is the first
thing a user waits for).

## Decision

`release.ps1` moves every `.pdb` file out of the release folder into
`CabinetOS-<version>-win-x64-symbols.zip` (with its `.sha256`), before it
zips the folder, so neither the release zip nor the setup file holds one.
`setup.iss` also excludes `*.pdb` in its `[Files]`. The in-app updater's
swap copies every file of the release (ADR 0014) and needs no change. A
`.pdb` file the script cannot move stops it.

[release.md](../release.md), "The symbols", says what they are for and how
to use them: unpack the zip next to the programs.

Options considered:

- **Keep the symbols in the zip.** Nothing to change, and every crash trace
  has file and line. Rejected: 43.6 MB of 75.7 MB for something a user
  reads at most once.
- **Ship no symbols at all.** A smaller build still, but a crash trace
  could never be read in full, even by the people who build the release.
  Rejected.
- **Make the symbols smaller** (split debug information, fewer crates with
  line tables). Possible later; it does not remove the question of where
  they go, and `cabinetos_core.pdb` is the one to look at.

## Consequences

- **Smaller downloads.** The release zip is 32.1 MB and the setup file
  20.0 MB, against 75.7 MB and 42.5 MB.
- **A crash trace from an install has no function names for the Rust
  programs** until the symbols are unpacked next to them: the frames read
  `<unknown>`. The crash file still holds the panic's `location` (file,
  line and column), the message, the boundary and the last log lines. The
  window's trace names its methods without file and line, and `location` is
  null. With the symbols, both name function, file and line. Measured, not
  assumed: `cabinetos-core --self-test-panic` and an exception thrown in
  `CabinetOS.Core.dll`, on a copy of the release folder, with and without
  the symbols ([release.md](../release.md), "The symbols").
- **The symbols must match the build.** The file name carries the version;
  a `.pdb` of another build does not match.
- **A release has one more asset**, the symbols zip and its hash. Publishing
  stays the creator's step ([release.md](../release.md), "Publish"); the
  GitHub Release should carry it beside the zip and the setup file.
- **Unpacked symbols do not survive an in-app update**: the swap moves every
  file of the install folder into `previous\`, and the next version's
  symbols are another file.
- **To undo:** copy the `.pdb` files into the folder again in step 3 of
  `release.ps1` and remove the move in step 5; drop `*.pdb` from `Excludes`
  in `setup.iss`.
