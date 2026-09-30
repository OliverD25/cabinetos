# Phase 04 — Job engine (copy, move, delete)

The history of this phase, moved from [PLAN.md](../PLAN.md) on 2026-09-30; the plan keeps the decisions and the done-when list.

Measured 2026-09-28 on this PC (E:, NVMe, release build): 10,000 files of 1–64 KiB plus one 20 GiB file copied in 9.7 s (2.3 GB/s); the progress stream peaked at 22 events per second (limit 30; Windows's 15.6 ms timer tick is why it is not closer to 30). A second run into the same destination skipped 10,001 existing files through conflicts while 1,000 new files were copied, in 1.5 s. A benchmark of `CopyFileExW` against a plain read/write loop is in [jobs.md](../jobs.md); IoRing is still unmeasured. Details: [jobs.md](../jobs.md).
