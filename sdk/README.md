# sdk/ — the plugin and protocol SDK

Not started. This folder fills up in Phases 1, 3 and 7 of
[../docs/PLAN.md](../docs/PLAN.md).

What will live here:

- `protocol/` — the JSON Schema of the control-channel messages, exported from
  the Rust types in `core/crates/cabinetos-protocol` (Phase 1 onward). The C#
  side is validated against it.
- `wit/` — the WebAssembly Component Model interface (`cabinetos:plugin`) that
  Core Plugins implement and the capabilities they may request (Phase 7).
- `templates/` — starter projects for a Core Plugin and a Tool Extension
  (Phase 7).
- `themes/` — the JSON theme format and the default theme (Phase 9).

Extension architecture: Constitution Article 11 and brief §6.
