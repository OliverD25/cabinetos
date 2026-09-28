# The indexer

`cabinetos-indexer.exe` keeps every file and folder of the NTFS volumes in
memory and answers name searches in milliseconds. Reading a volume's MFT
(the NTFS master file table, the disk's own list of every file) and its
change journal needs Administrator rights, so the indexer is a separate
process: the UI and the core stay unelevated and ask it over a pipe
([ADR 0002](decisions/0002-separate-elevated-indexer.md)). Without it, search
still works: the core walks folders itself. Constitution Article 1
(real-time NT-level file indexing); brief §2.

The code is in `core/crates/cabinetos-index` (the index) and
`core/crates/cabinetos-indexer` (the process).

## At a glance

Measured on 2026-09-28 on a GitHub Actions Windows runner (its `C:`, release
build):

| What | Result |
|---|---|
| Build the index of `C:` | 1,357,918 entries in 3.5 s |
| Memory | 119 MB, 87 bytes per entry |
| A 3-letter query over the whole volume | 11.5 ms |
| A new file visible to search | 26 ms after it was written |
| A rename, a move, a delete visible to search | 9 ms each |

The index is not saved: every start builds it again from the MFT.

## How the index is built

1. The indexer opens the volume (`\\.\C:`) for reading, shared with every
   other reader and writer. Without Administrator rights this fails with
   access denied, and the volume reports `failed`.
2. It asks the change journal for its ID and its current position
   (`FSCTL_QUERY_USN_JOURNAL`). Whatever changes from now on is in the journal
   after that position.
3. It reads every MFT entry with `FSCTL_ENUM_USN_DATA` (`MFT_ENUM_DATA_V1`,
   record versions 2 and 3). The NTFS driver returns one USN record per entry,
   with its file reference number (FRN), its parent's FRN, its name and its
   attributes. About 10,000 records come per 1 MiB call.
4. It then applies the journal from the position of step 2, so nothing that
   changed during the enumeration is lost.

This is the first-version reading of the brief's "read `C:\$MFT` directly":
the driver hands over exactly what the index needs, and no NTFS on-disk
structure is parsed. Parsing the raw `$MFT` could come later if the
enumeration ever proves too slow; at 3.5 s for 1.36 million entries it does
not.

Only NTFS volumes are indexed. A volume without a change journal fails
with a message: the indexer never creates one, because it writes nothing.

## What is in memory

- A map from FRN to a slot, and per slot the parent's FRN, a name ID and the
  attributes, in parallel arrays.
- Each distinct name once, twice over: in UTF-16 as NTFS stores it (for
  paths) and folded in UTF-8 (for search; see "Case and accents" below). A
  system drive repeats names a
  lot (`index.js`, `LICENSE`, the component store), so this saves much of the
  memory.
- No paths. A path is rebuilt by walking the parents up to the root (MFT
  segment 5), so renaming a folder is one update, not one per file under it.

The target was under 120 bytes per entry. Measured: 83.8 on a synthetic
400,000-entry volume (a test counts every allocation), 87 on the CI runner's
`C:`.

NTFS's own files (MFT segments below 16, such as `$MFT`, and everything
under `$Extend`) never appear in results. A file with several hard links
is indexed under one of its names.

## Search

A search is a case-insensitive substring match on names, in three passes:

1. For each distinct name: does it contain the query, and does it start
   with it? There are fewer distinct names than entries.
2. For each entry: is its name among the matches, and (with a root) is it
   under the root folder? The root is found by its FRN, so the check walks
   parents, and each folder is checked once per search.
3. The best `limit` hits, and a path for each of those only.

Passes 1 and 2 run on several threads. The rank: names that start with the
query first, then shorter names, then paths in order. An exact name is the
shortest name that starts with the query, so it comes first. `limit` is at
most 1,000.

### Case and accents

Names and the query are folded the same way before they are compared:
lowercased by Unicode's rules (`ЗВІТ` finds `звіт`, `Ґ` finds `ґ`), then
composed (Unicode's NFC, by Windows' `NormalizeString`). Composing makes
the two spellings of an accented letter one text: `é` as one character
(U+00E9, what a keyboard types) and `e` followed by a combining accent
(U+0301, what macOS and some downloads write). So `café` finds both
`café.txt` files, whichever spelling the query has. Each hit keeps the name
as NTFS stores it.

What folding does not do: it keeps accents (`cafe` does not find `café`),
and it keeps compatibility forms apart (`𝔘` is not `U`, the ligature `ﬁ` is
not `fi`). ASCII names, most of a system drive, are lowercased without a
call to Windows, and so is any name whose letters all lie below U+0300,
where no combining accent can appear. The core's own walk
(["Without the indexer"](#without-the-indexer)) ranks with the same
matcher, so both searches find the same names.

A radix tree or a trigram index would make searches faster still. That
waits until the scan is measured too slow; at 11.5 ms for 1.36 million
entries it is not.

### Links

The index holds what the MFT holds: a junction, a symbolic link or a mount
point is one entry, with the folder it sits in as its parent, and nothing
has it as a parent. So what a link points to is found once, under its real
path; a search limited to a link's folder finds nothing below it; and the
index can never loop through a link, whatever the link points to. The
core's own walk ([Without the indexer](#without-the-indexer)) does the
same: it enters folders only, never a link (kind 3), so a junction that
points back at its own folder costs one entry and ends there.

### Cloud files

The index reads names from the MFT, so a cloud file or folder is indexed
like any other, whether its data is here or not, and a search never
fetches anything. The core's walk lists folders, and a cloud folder whose
list of files is not fetched yet (`FILE_ATTRIBUTE_RECALL_ON_OPEN`) makes
the sync provider fetch that list when the walk enters it: names only, no
file data. (That is the Cloud Files behaviour Windows documents; it is not
tested here, since it needs a sync provider.)

### What the index reveals

The index holds names, parents and attributes, never file contents. It
answers any user logged on to the PC, regardless of that user's rights on
the folders the names sit in, as the "Everything" tool does
([ADR 0011](decisions/0011-search-without-per-user-filtering.md)). Opening
a hit still goes through NTFS, which refuses what the user may not read. A
user who needs otherwise runs without the indexer: the core's own walk sees
only what that user may see.

## Keeping the index current

One thread per volume reads the change journal (`FSCTL_READ_USN_JOURNAL`,
every reason, not only when a file is closed, so a rename shows before the
program that made it closes the file). The read waits until there is a new
record, and the thread applies it at once:

- a delete removes the entry;
- the first half of a rename (the old name) is skipped: the second half
  follows;
- a hard-link change is skipped: the file keeps the name it is indexed
  under;
- any other record says how the entry looks now, and is stored as such.

Searches read the index under a read lock; the thread takes the write lock
for each batch of records, which is well under a millisecond.

When the journal can no longer say what changed (it was deleted and made
again, or it dropped records the index had not read yet), the thread builds
a new index. The old one keeps answering meanwhile (state `rebuilding`), and
the new one takes its place when done. When the journal is deleted for good,
or reading it keeps failing, the volume reports `failed`.

`journal_lag` in `index_status` is the number of journal bytes written but
not yet applied; 0 means current.

On a quiet volume the journal read can wait a long time, and its timeout
does not end the wait. So stopping cancels the read (`CancelSynchronousIo`):
the indexer stops within a moment.

## Volume states

| State | Meaning |
|---|---|
| `building` | The first index is being built; searches skip the volume and say `complete: false` |
| `ready` | The index is current and follows the journal |
| `rebuilding` | A new index is being built; the previous one answers |
| `failed` | The volume cannot be indexed; `message` says why |

## The pipe

`\\.\pipe\cabinetos-indexer`. The name is fixed: there is one indexer per
machine, and a second one cannot take the pipe (it is created as the first
instance, and fails if the name exists). The framing is the core's: a 4-byte
little-endian length, then a JSON object with `id` and `type`
([ipc.md](ipc.md)).

Its security descriptor:

```text
D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)S:(ML;;NW;;;ME)
```

| Part | Meaning |
|---|---|
| `D:P` | A protected DACL: nothing inherited |
| `(D;;GA;;;NU)` | Network logons are denied. The pipe also refuses remote clients (`PIPE_REJECT_REMOTE_CLIENTS`). |
| `(A;;GA;;;SY)(A;;GA;;;BA)` | SYSTEM and Administrators have full access |
| `(A;;GRGW;;;IU)` | The interactive user may read and write: what a client needs to ask |
| `S:(ML;;NW;;;ME)` | A medium mandatory integrity label, no write up |

The label matters. An object an elevated process creates gets a high
integrity label by default, and Windows then refuses writes from a
normal-rights (medium) process such as the core. A test reads back what
Windows stored: `D:P(D;;FA;;;NU)(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x12019f;;;IU)S:AI(ML;;NW;;;ME)`
(`0x12019f` is file read plus file write).

**Requests.** Only three, and none changes anything:

```json
{"id":"01M…","type":"ping"}
{"id":"01M…","type":"pong","indexer_version":"0.1.0"}
{"id":"01M…","type":"index_status"}
{"id":"01M…","type":"index_status","volumes":[{"letter":"C","state":{"type":"ready"},
 "entries":1357918,"built_in_ms":3480,"journal_lag":0}]}
{"id":"01M…","type":"search","query":"budget","limit":50,"root":"C:\\Users\\me"}
{"id":"01M…","type":"file_search_results","hits":[{"path":"C:\\Users\\me\\Budget-2026.xlsx",
 "kind":"file","frn":1407374883553540}],"took_us":812,"complete":true}
```

`root` may be left out (every indexed volume). Errors come as
`{"type":"error","code":…,"message":…}` with `unknown_request` (any other
type), `protocol_error`, `not_indexed` (the root's volume is not indexed),
`invalid_root` or `internal`. The JSON Schema is in
[sdk/protocol/](../sdk/protocol/) (`indexer-request`, `indexer-response`).
Each request's ID appears in the indexer's log.

**Who can ask.** Any interactive user of the machine can search the names
of every file on the indexed volumes, including other users' folders, the
way the "Everything" search tool works. Hits are not filtered by the
asker's own rights yet.

## Running it

From an elevated terminal (Run as administrator), in the foreground until
Ctrl+C:

```text
cabinetos-indexer --console
cabinetos-indexer --console --volumes C,D
```

As a Windows service (start type manual, account LocalSystem):

```text
cabinetos-indexer --install
sc start cabinetos-indexer
sc stop cabinetos-indexer
cabinetos-indexer --uninstall
```

`--install` records this program's current path and the `--volumes` and
`--log-dir` given, so reinstall after moving the program. `--uninstall`
stops the service first.

A release installs and starts the service with `install.ps1 -AllUsers
-Indexer`, only into Program Files: a LocalSystem service must run a
program that only administrators can change ([release.md](release.md)).

Logs: `indexer.<date>.jsonl`, boundary `indexer`, in
`%LOCALAPPDATA%\CabinetOS\logs` for `--console` and
`%ProgramData%\CabinetOS\logs` for the service; `--log-dir` changes it
([diagnostics.md](diagnostics.md)). The log is the only thing the indexer
writes.

## Without the indexer

The core asks the indexer with a 200 ms limit. When it does not answer, the
core:

- walks one folder tree itself, breadth first, with the NT enumeration of
  [ipc.md](ipc.md) "Listing a directory", without following links, for
  about 2 s and 20,000 entries (`complete: false` when a limit stops it), and
  ranks the hits the same way. The limits are checked before each folder,
  and a folder is always read whole, so one slow folder can hold the walk
  past 2 s: a huge one, or a network folder whose server stopped answering
  (Windows waits for its network timeout);
- starts the walk at the search's `root`, else at the folder the connection
  listed last, else at the user's profile folder;
- says so once in its log, at INFO, and waits 1 s before asking the indexer
  again, doubling to 30 s while it stays away. `index_status` always asks.

The reply says where the hits came from: `"source":"index"` or
`"source":"walk"`. An indexer that answers but cannot help (the root's
volume is not indexed) also leads to a walk.

## Tests

- The index on synthetic records: applying creates, deletes, renames and
  moves; paths; ranking; the root filter; memory per entry with a counting
  allocator.
- The indexer's pipe: its DACL and label as stored, and the protocol with a
  stand-in index. The core end to end without an indexer and with a stand-in
  one; the CLI without one.
- Elevated tests, ignored elsewhere and skipped when not elevated, run in
  CI (the runner is an Administrator): the index of `C:` with a create, a
  rename, a move and a delete each visible within a second; the real
  indexer process; and installing, starting, querying, stopping and
  removing the service (only with `CABINETOS_TEST_SERVICE=1`, which only CI
  sets). From an elevated terminal, in `core/`:

  ```text
  cargo test --release -p cabinetos-index -p cabinetos-indexer -- --ignored --nocapture
  ```
