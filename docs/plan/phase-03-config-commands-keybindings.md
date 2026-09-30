# Phase 03 — Config, commands and keybindings

The history of this phase, moved from [PLAN.md](../PLAN.md) on 2026-09-30; the plan keeps the decisions and the done-when list.

Measured 2026-09-28 on this PC (debug build): a hand edit saved with `sed -i` reached a watching client 119 ms after the save; a broken save was reported as `config_error` 104 ms after it (`cabinetos-cli keys watch`). The end-to-end test `the_immutable_tier_cannot_be_rebound` proves the tier holds. Specs: [keybindings.md](../keybindings.md), [config.md](../config.md).
