# ADR 0001: The WinUI 3 frontend is written in C# on .NET

- Status: accepted
- Date: 2026-09-28
- Decided by: the creator, asked in chat

## Context

The brief says "WinUI 3 (C#/C++)" and mentions "unsafe C#" for reading shared
memory. The design handout says "C# or Rust via windows-rs". The choice sets
the build system, the IPC code on the UI side, and the second toolchain that
must be installed.

Options considered:

- **C# on .NET.** The mainstream WinUI 3 path: best tooling, docs and samples.
  `unsafe` blocks read shared-memory structs by pointer, without copying. The
  risk is garbage-collector pauses.
- **C++/WinRT.** No garbage collector, lower memory, but slow builds, weaker
  tooling, and XAML with C++ is rare and hard to get help with.
- **Rust via windows-rs.** One language for the whole project, but WinUI 3 from
  Rust has no XAML compiler support and almost no examples.

## Decision

C# on .NET.

## Consequences

- The garbage-collector risk is contained by the Dumb UI Rule (brief §1): the
  UI holds almost no data, so the collector has little to do. Listings are
  read from native memory, not copied into managed objects.
- `ui/` is a C# solution. The .NET SDK and the Windows App SDK must be
  installed before Phase 5 (see [../dev-setup.md](../dev-setup.md)).
- The shared-memory struct layouts need a C# mirror with
  `[StructLayout(LayoutKind.Sequential)]` and a test that compares sizes and
  offsets with the Rust side.
