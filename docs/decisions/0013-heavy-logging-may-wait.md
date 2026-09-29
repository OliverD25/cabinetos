# ADR 0013: Heavy logging may wait for the log writer

- Status: accepted
- Date: 2026-09-29
- Decided by: the creator, in chat on 2026-09-29, on the planning session's
  question of what should happen when the log writer cannot keep up in heavy
  mode. It is the first of six points the creator chose for heavy mode
  (Phase 15, [PLAN.md](../PLAN.md)).

## Context

Article 12 says that logging is "completely asynchronous to guarantee it never
blocks the main I/O pipeline or the UI thread". Today's writer keeps that to
the letter. A thread formats its line and queues it for a background writer.
When the writer is far behind, new lines are dropped and counted instead of
stalling the caller (16,384 lines in the window; the non-blocking appender's
buffer in the core). A dropped line is a hole in the record.

Phase 15 adds **heavy mode** ([diagnostics.md](../diagnostics.md), "Heavy
mode"): a switch for the times when a problem has to be found, in which every
operation is logged, at every level, with the full payloads, even at the cost
of speed. A drop-when-behind rule defeats that purpose. The busiest moments
(a copy of 100,000 files, a held key in a huge folder) are where a fault shows
and also where the writer falls behind, so the record would have holes exactly
where they hurt.

Options considered:

- **Drop when behind, as in normal mode.** Article 12 to the letter, but a heavy
  log with holes at the highest load.
- **An unbounded queue.** No hole and no wait, but memory grows until the
  process dies. A diagnostic mode that crashes the program it observes is
  worse than none.
- **A queue counted in bytes with a cap; above the cap, the thread that logs
  waits until the writer has written enough.** No hole. The operation is slower
  while the disk is the slower part, and memory is bounded.

## Decision

The third option, **in heavy mode only, and only for the core's operations**:

- While `logging.heavy` is on (or `CABINETOS_LOG_HEAVY=1`), a thread that logs
  in the core waits for the writer once the heavy queue holds 256 MiB, and goes
  on when the writer has drained it below the cap. Jobs, plugin calls and the
  other blocking threads are such threads. The wait is itself logged, afterwards:
  one line `heavy log waited` with the milliseconds, under the same trace and
  request, so a slow operation explains itself.
- **The UI thread and the pipe never wait.** The window's UI thread and the
  reader of its pipe, and the core's async runtime workers (which read and
  answer the pipe) and its main thread (which accepts connections), are marked
  as threads that never wait. Above the cap plus a margin (32 MiB in the core,
  8 MiB in the window) their lines are dropped and counted; the count is written
  into the heavy file and shown in the status bar. So the half of Article 12
  that protects the UI thread holds in heavy mode too, and the pipe stays alive
  however slow the disk is.
- **Normal mode is unchanged.** Every user who never switches heavy mode on has
  Article 12 as written: lines are dropped and counted when the writer is far
  behind, and no thread waits for the log.
- **The mode is never silent.** It is a setting (`logging.heavy`, in the file
  and in the palette), it stays on until someone turns it off, the status bar
  shows a `HEAVY LOG` pill while it is on, the window says so at start, and both
  logs record when it goes on and off.

## Consequences

- **Heavy mode can slow a job.** While the writer is more than 256 MiB behind,
  each thread that logs waits for it. That is the price the creator accepted;
  the `heavy log waited` lines show where it was paid. In the window the same
  rule applies to its background threads, with a 64 MiB queue.
- **The exception is narrow:** an opt-in mode, core operations only, never the
  UI thread and never the reader of the pipe.
- **The Constitution is not edited.** Only its author can, and the creator has
  not asked for it. This record is the exception. The
  [README](README.md) of this folder says a record may not contradict an
  article; this one leaves Article 12 whole for the default mode and states the
  one exception the article's author chose, for a switch that has to be turned
  on. If the
  creator wants the article itself to carry it, the sentence to consider adding
  after "or the UI thread" is: "The one exception is the opt-in heavy mode,
  where a thread of the core may wait for the log writer so that no operation
  goes unrecorded (ADR 0013)." It is a proposal, not an edit.
- **Two tests stand behind it.** `cabinetos-diag` shows a deliberately slow
  writer making a thread wait, with no line lost and the wait written; the same
  is shown for the window's `LogWriter`, together with the UI thread dropping
  and counting instead of waiting.
- **Undoing it** is one rule: mark every thread as one that never waits
  (`never_wait_for_heavy_log` in `cabinetos-diag`, `LogWriter.NeverWaitForHeavyLog`
  in the window), and heavy mode drops lines like normal mode does.
