# ADR 0004: Minimum supported Windows is 11 22H2 (build 22621)

- Status: accepted
- Date: 2026-09-28
- Decided by: the creator, asked in chat

## Context

The brief mentions IoRing (a Windows 11 22H2 API for very fast disk I/O). The
Constitution (Article 3) requires Mica, which exists only on Windows 11.
Supporting Windows 10 would mean a fallback code path for each of these, plus
substitutes for the Segoe Fluent Icons font and Segoe UI Variable.

Options considered:

- **Windows 11 22H2 or newer.** Everything the brief and the Constitution name
  exists. No fallback code.
- **Windows 10 1809 and newer as well.** WinUI 3 runs there, but Mica, the
  Fluent icon font and IoRing are missing. A second look and a second copy
  engine would be needed, for an OS whose support ended in October 2025.

## Decision

Windows 11 22H2 (build 22621) or newer.

## Consequences

- No Windows 10 code paths anywhere. A startup check refuses to run on older
  builds with a plain message.
- The application manifest declares the minimum version; the `windows-rs`
  bindings may use any API up to build 22621 without a runtime probe, and
  newer APIs (for example later IoRing additions) still need a probe.
- CI runs on `windows-latest` (Windows Server 2025, build 26100), which is
  newer than the minimum, so a test on the minimum build needs a VM later.
