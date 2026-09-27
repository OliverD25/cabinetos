# ADR 0003: The project is licensed under MIT

- Status: accepted
- Date: 2026-09-28
- Decided by: the creator, asked in chat

## Context

Article 2 of the Constitution requires the core application and the
marketplace infrastructure to be free and open source. The license must be in
the repository before it goes public, and it affects whether plugin authors and
companies build on CabinetOS.

Options considered:

- **MIT.** Shortest permissive license. Anyone can use, change and sell the
  code and must only keep the copyright notice. Most common in the Rust and C#
  ecosystems. The design prototype's README already shows an MIT badge.
- **Apache-2.0.** Permissive like MIT plus an explicit patent promise. Longer.
- **MPL-2.0.** File-level copyleft: changed files must be published; plugins
  and apps that only use CabinetOS can stay closed.
- **GPL-3.0.** Strong copyleft: distributed derivatives must be GPL. Many
  companies and plugin authors avoid it.

## Decision

MIT. The copyright line names the creator's GitHub account and the
contributors: "Copyright (c) 2026 OliverD25 and the CabinetOS contributors".

## Consequences

- `LICENSE` at the repository root holds the MIT text.
- Every Cargo package declares `license = "MIT"`; the C# projects declare
  `<PackageLicenseExpression>MIT</PackageLicenseExpression>`.
- `cargo-deny` allows dependencies under MIT, Apache-2.0, BSD-2/3, ISC,
  Unicode and Zlib. Copyleft dependencies are rejected so the MIT promise
  holds for the whole binary.
- Plugins in the marketplace may use any license; the sandbox boundary keeps
  them separate works.
