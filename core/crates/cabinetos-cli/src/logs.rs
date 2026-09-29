//! `log`: the log folder. `log trace <id>` prints every line of one user
//! action (or one request) from every process's files, normal and heavy,
//! in time order; `log tail` prints the newest lines of one process's
//! file. Both read the files and need no core. `log bundle` asks the core
//! for a zip of the last minutes (docs/diagnostics.md).

use std::collections::HashMap;
use std::fmt::Write as _;
use std::fs::File;
use std::io::{BufRead, BufReader, Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::time::Duration;

use anyhow::{Context, bail};
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{Request, Response};
use serde_json::Value;

use crate::say;

/// `log bundle`: asks the core to write a bundle and prints its path.
pub(crate) async fn bundle(client: &mut PipeClient, minutes: u32) -> anyhow::Result<()> {
    let reply = crate::send(client, Request::SaveLogBundle { minutes }).await?;
    match reply.body {
        Response::LogBundle { path } => {
            say(format_args!("{path}"));
            Ok(())
        }
        other => Err(crate::failure("save_log_bundle", &other)),
    }
}

/// Heavy files start with this, then the process name: `heavy-core.<date>.jsonl`.
const HEAVY_PREFIX: &str = "heavy-";

/// How far apart the same event may be stamped in a process's normal file
/// and its heavy file: each file's writer takes its own time.
const TWIN_TOLERANCE_MS: i64 = 10;

/// How often `log tail --follow` looks for new lines.
const FOLLOW_EVERY: Duration = Duration::from_millis(250);

/// One `*.jsonl` file of the log folder.
#[derive(Clone, Debug, PartialEq, Eq)]
struct LogFile {
    path: PathBuf,
    /// The name before the first dot: `core`, `heavy-core`, `ui`.
    label: String,
    /// The process: the label without `heavy-`.
    process: String,
    heavy: bool,
    /// The rest of the name, for ordering: `2026-09-29` or `2026-09-29.1`.
    stamp: String,
}

/// Every `*.jsonl` file in `dir`, ordered by name.
fn log_files(dir: &Path) -> anyhow::Result<Vec<LogFile>> {
    let entries = std::fs::read_dir(dir)
        .with_context(|| format!("cannot read the log folder {}", dir.display()))?;
    let mut files = Vec::new();
    for entry in entries.flatten() {
        let path = entry.path();
        let Some(name) = path.file_name().and_then(|name| name.to_str()) else {
            continue;
        };
        let Some(stem) = name.strip_suffix(".jsonl") else {
            continue;
        };
        if !path.is_file() {
            continue;
        }
        let (label, stamp) = stem.split_once('.').unwrap_or((stem, ""));
        let (process, heavy) = match label.strip_prefix(HEAVY_PREFIX) {
            Some(process) => (process, true),
            None => (label, false),
        };
        files.push(LogFile {
            label: label.to_owned(),
            process: process.to_owned(),
            heavy,
            stamp: stamp.to_owned(),
            path,
        });
    }
    files.sort_by(|a, b| a.path.cmp(&b.path));
    Ok(files)
}

/// One matching line, with where it came from.
struct Found {
    ts: String,
    file: usize,
    raw: String,
    value: Value,
}

/// `log trace <id>`: the lines whose `trace_id` or `request_id` is `id`,
/// from every file of the folder, oldest first. The same event in a
/// process's normal and heavy file is printed once.
pub(crate) fn trace(dir: &Path, id: &str, json: bool) -> anyhow::Result<()> {
    let files = log_files(dir)?;
    let found = find(&files, id)?;
    if found.is_empty() {
        bail!("no line in {} has trace or request {id}", dir.display());
    }
    for line in &found {
        let printed = if json {
            say(format_args!("{}", line.raw))
        } else {
            say(format_args!(
                "{}",
                readable(&files[line.file].label, &line.value)
            ))
        };
        if !printed {
            break;
        }
    }
    Ok(())
}

/// The lines of `files` that belong to `id`, sorted by time, twins removed.
fn find(files: &[LogFile], id: &str) -> anyhow::Result<Vec<Found>> {
    let upper = id.to_ascii_uppercase();
    let mut found = Vec::new();
    for (index, file) in files.iter().enumerate() {
        let reader = BufReader::new(
            File::open(&file.path)
                .with_context(|| format!("cannot open {}", file.path.display()))?,
        );
        for raw in reader.lines() {
            let Ok(raw) = raw else { break };
            // Most lines are not about this action; skip them unparsed.
            if !raw.contains(id) && !raw.contains(&upper) {
                continue;
            }
            let Ok(value) = serde_json::from_str::<Value>(&raw) else {
                continue;
            };
            let matches = ["trace_id", "request_id"].iter().any(|key| {
                value
                    .get(key)
                    .and_then(Value::as_str)
                    .is_some_and(|text| text.eq_ignore_ascii_case(id))
            });
            if matches {
                found.push(Found {
                    ts: value
                        .get("ts")
                        .and_then(Value::as_str)
                        .unwrap_or_default()
                        .to_owned(),
                    file: index,
                    raw,
                    value,
                });
            }
        }
    }
    found.sort_by(|a, b| a.ts.cmp(&b.ts).then(a.file.cmp(&b.file)));
    Ok(without_twins(files, found))
}

/// Drops a line that its process wrote into both its normal and its heavy
/// file: same event, stamped a moment apart.
fn without_twins(files: &[LogFile], found: Vec<Found>) -> Vec<Found> {
    let mut kept: HashMap<String, Vec<(i64, bool)>> = HashMap::new();
    let mut result = Vec::with_capacity(found.len());
    for line in found {
        let file = &files[line.file];
        let key = twin_key(&file.process, &line.value);
        let at = millis(&line.ts);
        let seen = kept.entry(key).or_default();
        if let Some(position) = seen.iter().position(|(when, heavy)| {
            *heavy != file.heavy && (at - when).abs() <= TWIN_TOLERANCE_MS
        }) {
            // Its twin was printed; neither matches a third line.
            seen.remove(position);
            continue;
        }
        seen.push((at, file.heavy));
        result.push(line);
    }
    result
}

/// What makes two lines the same event, apart from their time.
fn twin_key(process: &str, value: &Value) -> String {
    let part = |key: &str| value.get(key).map(Value::to_string).unwrap_or_default();
    [
        process.to_owned(),
        part("level"),
        part("target"),
        part("message"),
        part("request_id"),
        part("plugin_id"),
        part("fields"),
        part("thread"),
    ]
    .join("\u{1f}")
}

/// Milliseconds since 1970 for `2026-09-29T01:02:03.004Z`; 0 for anything
/// else, which is never a twin of a real time.
fn millis(ts: &str) -> i64 {
    let number =
        |range: std::ops::Range<usize>| ts.get(range).and_then(|text| text.parse::<i64>().ok());
    let (Some(year), Some(month), Some(day), Some(hour), Some(minute), Some(second), Some(milli)) = (
        number(0..4),
        number(5..7),
        number(8..10),
        number(11..13),
        number(14..16),
        number(17..19),
        number(20..23),
    ) else {
        return 0;
    };
    // Days from the civil date (Howard Hinnant's algorithm).
    let year = if month <= 2 { year - 1 } else { year };
    let era = year.div_euclid(400);
    let of_era = year - era * 400;
    let day_of_year = (153 * (month + if month > 2 { -3 } else { 9 }) + 2) / 5 + day - 1;
    let of_era_days = of_era * 365 + of_era / 4 - of_era / 100 + day_of_year;
    let days = era * 146_097 + of_era_days - 719_468;
    ((days * 24 + hour) * 60 + minute) * 60_000 + second * 1000 + milli
}

/// One line for a person: boundary and process first, then time, level,
/// target and message, then the IDs and fields, then the thread.
fn readable(label: &str, value: &Value) -> String {
    let text = |key: &str| value.get(key).and_then(Value::as_str).unwrap_or_default();
    let mut line = format!(
        "{:<8} {:<13} {} {:<5} {}: {}",
        text("boundary"),
        label,
        text("ts"),
        text("level"),
        text("target"),
        text("message"),
    );
    for key in ["request_id", "plugin_id"] {
        if let Some(id) = value.get(key).and_then(Value::as_str) {
            let _ = write!(line, " {key}={id}");
        }
    }
    if let Some(fields) = value.get("fields").and_then(Value::as_object) {
        for (name, field) in fields {
            let _ = match field {
                Value::String(text) => write!(line, " {name}={text}"),
                other => write!(line, " {name}={other}"),
            };
        }
    }
    let _ = write!(line, " [{}]", text("thread"));
    line
}

/// What `log tail` prints.
pub(crate) struct Tail {
    pub(crate) process: String,
    pub(crate) heavy: bool,
    pub(crate) lines: usize,
    pub(crate) follow: bool,
    pub(crate) json: bool,
}

/// `log tail`: the newest lines of a process's newest file, and with
/// `follow` every line added after them, until Ctrl+C.
pub(crate) fn tail(dir: &Path, tail: &Tail) -> anyhow::Result<()> {
    let label = if tail.heavy {
        format!("{HEAVY_PREFIX}{}", tail.process)
    } else {
        tail.process.clone()
    };
    let Some(mut file) = newest(dir, &label)? else {
        bail!("no {label}.*.jsonl file in {}", dir.display());
    };
    let (lines, mut offset) = last_lines(&file.path, tail.lines)?;
    for line in &lines {
        if !print_line(&label, line, tail.json) {
            return Ok(());
        }
    }
    if !tail.follow {
        return Ok(());
    }
    let mut partial = String::new();
    loop {
        std::thread::sleep(FOLLOW_EVERY);
        let len = std::fs::metadata(&file.path).map_or(0, |metadata| metadata.len());
        if len > offset {
            let mut reader = File::open(&file.path)?;
            reader.seek(SeekFrom::Start(offset))?;
            let mut added = String::new();
            reader.take(len - offset).read_to_string(&mut added)?;
            offset = len;
            partial.push_str(&added);
            while let Some(end) = partial.find('\n') {
                let line: String = partial.drain(..=end).collect();
                if !print_line(&label, line.trim_end(), tail.json) {
                    return Ok(());
                }
            }
        }
        // A new day, or a heavy file that grew to its size limit.
        if let Some(newer) = newest(dir, &label)?
            && newer.path != file.path
        {
            file = newer;
            offset = 0;
            partial.clear();
        }
    }
}

fn print_line(label: &str, raw: &str, json: bool) -> bool {
    if json {
        return say(format_args!("{raw}"));
    }
    match serde_json::from_str::<Value>(raw) {
        Ok(value) => say(format_args!("{}", readable(label, &value))),
        Err(_) => say(format_args!("{raw}")),
    }
}

/// The newest file of `label` (`core`, `heavy-core`): the latest date, and
/// on that date the highest part number.
fn newest(dir: &Path, label: &str) -> anyhow::Result<Option<LogFile>> {
    Ok(log_files(dir)?
        .into_iter()
        .filter(|file| file.label == label)
        .max_by(|a, b| stamp_order(&a.stamp).cmp(&stamp_order(&b.stamp))))
}

/// `2026-09-29` → (`2026-09-29`, 0); `2026-09-29.3` → (`2026-09-29`, 3).
fn stamp_order(stamp: &str) -> (String, u64) {
    match stamp.split_once('.') {
        Some((date, part)) => (date.to_owned(), part.parse().unwrap_or(0)),
        None => (stamp.to_owned(), 0),
    }
}

/// The last `count` complete lines of `path`, and the file's length. Reads
/// from the end, so a large heavy file is not read whole.
fn last_lines(path: &Path, count: usize) -> anyhow::Result<(Vec<String>, u64)> {
    let mut file = File::open(path).with_context(|| format!("cannot open {}", path.display()))?;
    let len = file.metadata()?.len();
    let mut window: u64 = 64 * 1024;
    loop {
        let start = len.saturating_sub(window);
        file.seek(SeekFrom::Start(start))?;
        let mut bytes = Vec::new();
        (&mut file).take(len - start).read_to_end(&mut bytes)?;
        let text = String::from_utf8_lossy(&bytes);
        let mut lines: Vec<&str> = text.lines().collect();
        // The first line of a window that starts mid-file may be cut.
        if start > 0 && !lines.is_empty() {
            lines.remove(0);
        }
        if lines.len() >= count || start == 0 {
            let skip = lines.len().saturating_sub(count);
            let kept = lines[skip..]
                .iter()
                .map(|line| (*line).to_owned())
                .collect();
            return Ok((kept, len));
        }
        window *= 4;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn millis_counts_from_1970() {
        assert_eq!(millis("1970-01-01T00:00:00.000Z"), 0);
        assert_eq!(millis("2026-09-28T01:02:03.004Z"), 1_790_557_323_004);
        assert_eq!(millis("2024-02-29T23:59:59.999Z"), 1_709_251_199_999);
        assert_eq!(millis("not a time"), 0);
    }

    #[test]
    fn stamps_order_by_date_then_part() {
        let mut stamps = ["2026-09-29.2", "2026-09-28", "2026-09-29", "2026-09-29.10"];
        stamps.sort_by_key(|stamp| stamp_order(stamp));
        assert_eq!(
            stamps,
            ["2026-09-28", "2026-09-29", "2026-09-29.2", "2026-09-29.10"]
        );
    }

    #[test]
    fn a_readable_line_starts_with_boundary_and_process() {
        let value = serde_json::json!({
            "ts": "2026-09-29T01:02:03.004Z",
            "level": "INFO",
            "boundary": "engine",
            "target": "cabinetos_core::connection",
            "message": "request handled",
            "trace_id": "T",
            "request_id": "R",
            "fields": {"elapsed_us": 57},
            "thread": "core-rt-1"
        });
        assert_eq!(
            readable("core", &value),
            "engine   core          2026-09-29T01:02:03.004Z INFO  cabinetos_core::connection: \
             request handled request_id=R elapsed_us=57 [core-rt-1]"
        );
    }
}
