# ADR 0006: The control channel carries JSON

- Status: accepted
- Date: 2026-09-28
- Decided by: the creator, asked in chat

## Context

The brief (§4) leaves the control-channel format open: "lightweight JSON or
Protobuf". The control channel is the named pipe that carries commands and
small replies between the UI and the core. Large data never travels on it;
directory listings go through shared memory (the data channel).

Options considered:

- **JSON.** Human-readable text. Easy to log, debug and test by hand. No
  code-generation step. Payloads are tiny, so parse speed does not matter.
- **Protobuf.** Compact binary with a `.proto` schema compiled into both Rust
  and C#. Faster to parse and typed from one source, but adds a build step
  and makes logs unreadable without tooling.

## Decision

JSON, serialized with `serde_json` in Rust and `System.Text.Json` in C#.

## Consequences

- Messages are length-prefixed frames: a 4-byte little-endian length followed
  by UTF-8 JSON. The maximum frame is 16 MiB; anything larger is a protocol
  error, because large data belongs in shared memory.
- Every message is an envelope with `id` (a ULID, a sortable unique ID that
  the UI creates) and `type`; the core echoes `id` in the reply and logs it,
  so one action can be traced across all boundaries (Article 12).
- The Rust types in `cabinetos-protocol` are the source of truth. A JSON
  Schema exported from them (via `schemars`) is checked into `sdk/protocol/`
  and the C# types are validated against it in CI.
- MessagePack (the same shapes, binary) is the upgrade path if a message ever
  becomes large enough to matter. It is a one-flag switch, not a redesign.
