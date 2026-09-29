//! Log bundles: one zip with what someone needs to find a problem, the
//! last minutes of every process's logs (docs/diagnostics.md, "Bundles").
//!
//! A bundle holds, from the log folder:
//!
//! - every `*.jsonl` file (every process, normal and heavy files), cut to
//!   the lines of the last `minutes`, under its own name; a file with no
//!   such line is left out;
//! - every `crash-*.json` of the last 24 hours;
//! - `bundle.json`: when and why the bundle was made, the versions, the
//!   Windows build, the `CABINETOS_*` environment variables and the
//!   configuration in effect, secrets masked in both, and the files.
//!
//! [`save_bundle`] makes `bundle-<time>.zip` on request; the panic hook
//! makes `crash-<time>.zip` while heavy mode is on. Both read only the
//! files on disk, so they need nothing from the threads that log.

use std::fs::File;
use std::io::{BufWriter, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::{Mutex, PoisonError, TryLockError};
use std::time::{Duration, SystemTime};

use serde::Serialize;
use serde_json::{Map, Value};
use time::OffsetDateTime;
use zip::CompressionMethod;
use zip::write::SimpleFileOptions;

use crate::{PROCESS, clock, mask};

/// The minutes a bundle holds when nobody says otherwise.
pub const BUNDLE_MINUTES: u32 = 10;

/// The most minutes a bundle may hold: a day.
pub const MAX_BUNDLE_MINUTES: u32 = 24 * 60;

/// How old a crash trace may be and still go into a bundle.
const CRASHES_KEPT_FOR: Duration = Duration::from_hours(24);

/// A log file is read backwards in pieces this large, until a piece holds
/// only older lines.
const CHUNK: u64 = 1024 * 1024;

/// What the process tells bundles about itself, set while it runs.
#[derive(Clone, Debug, Default)]
pub(crate) struct Facts {
    windows_build: Option<String>,
    config: Option<Value>,
}

static FACTS: Mutex<Facts> = Mutex::new(Facts {
    windows_build: None,
    config: None,
});

fn facts() -> std::sync::MutexGuard<'static, Facts> {
    FACTS.lock().unwrap_or_else(PoisonError::into_inner)
}

/// Tells later bundles the Windows build, for example
/// `10.0.26200.6899 (25H2)`. The core reads it once at start.
pub fn set_bundle_windows_build(build: String) {
    facts().windows_build = Some(build);
}

/// Tells later bundles the configuration in effect. Its secrets are masked
/// here, so the bundle never holds them.
pub fn set_bundle_config(config: &Value) {
    let mut masked = config.clone();
    mask::mask_secrets(&mut masked);
    facts().config = Some(masked);
}

/// Why a bundle was made.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Reason {
    /// Someone asked (`save_log_bundle`, `cabinetos-cli log bundle`).
    Asked,
    /// The process crashed while heavy mode was on.
    Crash,
}

/// Writes `bundle-<YYYYMMDDTHHMMSSmmmZ>.zip` into this process's log folder
/// with the last `minutes` (1 to 1,440) of every log file there, and
/// returns its path.
pub fn save_bundle(minutes: u32) -> std::io::Result<PathBuf> {
    let Some(process) = PROCESS.get() else {
        return Err(std::io::Error::other("diagnostics are not initialized"));
    };
    let facts = facts().clone();
    let path = unique_path(&process.log_dir, "bundle", clock::now())?;
    write_bundle(
        &process.log_dir,
        &path,
        &Bundle {
            reason: Reason::Asked,
            minutes,
            process: process.process,
            now: clock::now(),
        },
        &facts,
    )?;
    Ok(path)
}

/// The crash hook's bundle, `crash-<time>.zip`: the same contents, without
/// waiting for any lock (the configuration and Windows build are left out
/// when another thread holds them).
pub(crate) fn save_crash_bundle(dir: &Path, process: &str) -> std::io::Result<PathBuf> {
    let facts = match FACTS.try_lock() {
        Ok(facts) => facts.clone(),
        Err(TryLockError::Poisoned(poisoned)) => poisoned.into_inner().clone(),
        Err(TryLockError::WouldBlock) => Facts::default(),
    };
    let path = unique_path(dir, "crash", clock::now())?;
    write_bundle(
        dir,
        &path,
        &Bundle {
            reason: Reason::Crash,
            minutes: BUNDLE_MINUTES,
            process,
            now: clock::now(),
        },
        &facts,
    )?;
    Ok(path)
}

/// What is known about a bundle before it is written.
pub(crate) struct Bundle<'a> {
    pub(crate) reason: Reason,
    pub(crate) minutes: u32,
    pub(crate) process: &'a str,
    pub(crate) now: OffsetDateTime,
}

/// `bundle.json`.
#[derive(Serialize)]
struct Manifest<'a> {
    created: String,
    reason: &'static str,
    process: &'a str,
    minutes: u32,
    since: String,
    versions: Versions,
    windows_build: Option<&'a str>,
    environment: Map<String, Value>,
    config: Option<&'a Value>,
    files: Vec<Included>,
}

#[derive(Serialize)]
struct Versions {
    cabinetos: &'static str,
    protocol: u32,
}

#[derive(Serialize)]
struct Included {
    name: String,
    /// Lines of a log file in the window; `None` for a crash trace.
    #[serde(skip_serializing_if = "Option::is_none")]
    lines: Option<usize>,
    bytes: u64,
}

/// `<dir>\<prefix>-<stamp>.zip`, with the process ID added when another
/// bundle of the same millisecond is there.
fn unique_path(dir: &Path, prefix: &str, now: OffsetDateTime) -> std::io::Result<PathBuf> {
    std::fs::create_dir_all(dir)?;
    let stamp = clock::compact_millis(now);
    let path = dir.join(format!("{prefix}-{stamp}.zip"));
    if path.exists() {
        return Ok(dir.join(format!("{prefix}-{stamp}-{}.zip", std::process::id())));
    }
    Ok(path)
}

/// Writes the bundle of `dir` into `path`.
pub(crate) fn write_bundle(
    dir: &Path,
    path: &Path,
    bundle: &Bundle<'_>,
    facts: &Facts,
) -> std::io::Result<()> {
    let minutes = bundle.minutes.clamp(1, MAX_BUNDLE_MINUTES);
    let since = bundle.now - time::Duration::minutes(i64::from(minutes));
    let since_text = clock::rfc3339_millis(since);
    let since_time = SystemTime::from(since);
    let crash_since = SystemTime::from(bundle.now)
        .checked_sub(CRASHES_KEPT_FOR)
        .unwrap_or(SystemTime::UNIX_EPOCH);

    let mut names: Vec<(String, PathBuf, SystemTime)> = std::fs::read_dir(dir)?
        .flatten()
        .filter_map(|entry| {
            let name = entry.file_name().into_string().ok()?;
            let modified = entry.metadata().ok()?.modified().ok()?;
            Some((name, entry.path(), modified))
        })
        .collect();
    names.sort();

    let file = File::create(path)?;
    let mut zip = zip::ZipWriter::new(BufWriter::new(file));
    // Fast rather than small: a crash bundle is written while the process
    // goes down.
    let options = SimpleFileOptions::default()
        .compression_method(CompressionMethod::Deflated)
        .compression_level(Some(1))
        .large_file(true);
    let mut included = Vec::new();
    for (name, source, modified) in &names {
        let is_log = std::path::Path::new(name)
            .extension()
            .is_some_and(|extension| extension.eq_ignore_ascii_case("jsonl"));
        let is_crash = name.starts_with("crash-")
            && std::path::Path::new(name)
                .extension()
                .is_some_and(|extension| extension.eq_ignore_ascii_case("json"));
        if is_log && *modified >= since_time {
            let (lines, count) = recent_lines(source, &since_text)?;
            if count > 0 {
                zip.start_file(name.as_str(), options)
                    .map_err(std::io::Error::other)?;
                zip.write_all(&lines)?;
                included.push(Included {
                    name: name.clone(),
                    lines: Some(count),
                    bytes: lines.len() as u64,
                });
            }
        } else if is_crash && *modified >= crash_since {
            let mut bytes = Vec::new();
            File::open(source)?.read_to_end(&mut bytes)?;
            zip.start_file(name.as_str(), options)
                .map_err(std::io::Error::other)?;
            zip.write_all(&bytes)?;
            included.push(Included {
                name: name.clone(),
                lines: None,
                bytes: bytes.len() as u64,
            });
        }
    }

    let manifest = Manifest {
        created: clock::rfc3339_millis(bundle.now),
        reason: match bundle.reason {
            Reason::Asked => "asked",
            Reason::Crash => "crash",
        },
        process: bundle.process,
        minutes,
        since: since_text,
        versions: Versions {
            cabinetos: env!("CARGO_PKG_VERSION"),
            protocol: cabinetos_protocol::PROTOCOL_VERSION,
        },
        windows_build: facts.windows_build.as_deref(),
        environment: environment(std::env::vars_os()),
        config: facts.config.as_ref(),
        files: included,
    };
    zip.start_file("bundle.json", options)
        .map_err(std::io::Error::other)?;
    zip.write_all(&serde_json::to_vec_pretty(&manifest)?)?;
    let mut writer = zip.finish().map_err(std::io::Error::other)?;
    writer.flush()
}

/// The `CABINETOS_*` variables, those that look like keys masked.
fn environment(
    vars: impl Iterator<Item = (std::ffi::OsString, std::ffi::OsString)>,
) -> Map<String, Value> {
    let mut picked: Vec<(String, String)> = vars
        .filter_map(|(name, value)| Some((name.into_string().ok()?, value.into_string().ok()?)))
        .filter(|(name, _)| name.to_ascii_uppercase().starts_with("CABINETOS_"))
        .collect();
    picked.sort();
    picked
        .into_iter()
        .map(|(name, value)| {
            let value = if mask::is_secret_env(&name) {
                mask::MASK.to_owned()
            } else {
                value
            };
            (name, Value::String(value))
        })
        .collect()
}

/// The lines of `path` stamped `since` or later, as bytes with their line
/// ends, and how many there are. Reads from the end, a piece at a time,
/// and stops at a piece that holds only older lines: the files are
/// written in time order, give or take the lines of two processes that
/// share one.
fn recent_lines(path: &Path, since: &str) -> std::io::Result<(Vec<u8>, usize)> {
    let mut file = File::open(path)?;
    let len = file.metadata()?.len();
    let mut end = len;
    // The start of the piece read last: a line cut at its front edge.
    let mut carry: Vec<u8> = Vec::new();
    let mut pieces: Vec<Vec<u8>> = Vec::new();
    let mut count = 0;
    while end > 0 {
        let start = end.saturating_sub(CHUNK);
        let mut buffer = vec![0; usize::try_from(end - start).unwrap_or(0)];
        file.seek(SeekFrom::Start(start))?;
        file.read_exact(&mut buffer)?;
        buffer.extend_from_slice(&carry);
        carry.clear();
        let body = if start > 0 {
            let Some(first) = buffer.iter().position(|&byte| byte == b'\n') else {
                carry = buffer;
                end = start;
                continue;
            };
            carry = buffer[..=first].to_vec();
            buffer.split_off(first + 1)
        } else {
            buffer
        };
        let (kept, lines, seen, old) = keep_recent(&body, since);
        count += lines;
        pieces.push(kept);
        end = start;
        if seen > 0 && old == seen {
            break;
        }
    }
    let mut lines = Vec::new();
    for piece in pieces.into_iter().rev() {
        lines.extend_from_slice(&piece);
    }
    Ok((lines, count))
}

/// The lines of `body` stamped `since` or later (with a line end each), how
/// many they are, how many lines had a stamp, and how many of those were
/// older.
fn keep_recent(body: &[u8], since: &str) -> (Vec<u8>, usize, usize, usize) {
    let mut kept = Vec::with_capacity(body.len());
    let mut lines = 0;
    let mut seen = 0;
    let mut old = 0;
    for line in body.split(|&byte| byte == b'\n') {
        if line.is_empty() {
            continue;
        }
        let keep = match stamp_of(line) {
            Some(ts) => {
                seen += 1;
                let recent = ts >= since.as_bytes();
                if !recent {
                    old += 1;
                }
                recent
            }
            // A line without a stamp cannot be placed; keep it with its
            // neighbours in the window.
            None => true,
        };
        if keep {
            kept.extend_from_slice(line);
            kept.push(b'\n');
            lines += 1;
        }
    }
    (kept, lines, seen, old)
}

/// The `ts` of a line: every writer puts it first,
/// `{"ts":"2026-09-30T01:02:03.004Z",…`.
fn stamp_of(line: &[u8]) -> Option<&[u8]> {
    const PREFIX: &[u8] = br#"{"ts":""#;
    let rest = line.strip_prefix(PREFIX)?;
    rest.get(..24)
}

#[cfg(test)]
mod tests {
    use std::io::Cursor;

    use super::*;

    fn line(ts: &str, message: &str) -> String {
        format!(
            r#"{{"ts":"{ts}","level":"INFO","boundary":"engine","target":"t","message":"{message}","thread":"x"}}"#
        )
    }

    fn zip_entries(path: &Path) -> Vec<(String, String)> {
        let bytes = std::fs::read(path).unwrap();
        let mut archive = zip::ZipArchive::new(Cursor::new(bytes)).unwrap();
        (0..archive.len())
            .map(|index| {
                let mut entry = archive.by_index(index).unwrap();
                let mut text = String::new();
                entry.read_to_string(&mut text).unwrap();
                (entry.name().to_owned(), text)
            })
            .collect()
    }

    #[test]
    fn a_bundle_holds_the_last_minutes_of_every_log_and_the_recent_crashes() {
        let dir = tempfile::tempdir().unwrap();
        // The files are written now, so the bundle is made now too.
        let now = clock::now();
        let ago = |minutes: i64| clock::rfc3339_millis(now - time::Duration::minutes(minutes));
        let write = |name: &str, lines: &[String]| {
            std::fs::write(dir.path().join(name), lines.join("\n") + "\n").unwrap();
        };
        write(
            "core.2026-09-30.jsonl",
            &[
                line(&ago(20), "too old"),
                line(&ago(10), "just in"),
                line(&ago(0), "last"),
            ],
        );
        write("heavy-core.2026-09-30.1.jsonl", &[line(&ago(5), "heavy")]);
        write("ui.2026-09-30.jsonl", &[line(&ago(120), "nothing recent")]);
        std::fs::write(dir.path().join("crash-20260930T115000000Z.json"), "{}").unwrap();
        let old_crash = dir.path().join("crash-20260920T000000000Z.json");
        std::fs::write(&old_crash, "{}").unwrap();
        File::options()
            .write(true)
            .open(&old_crash)
            .unwrap()
            .set_modified(SystemTime::now() - Duration::from_hours(72))
            .unwrap();
        std::fs::write(
            dir.path().join("bundle-20260930T000000000Z.zip"),
            "old bundle",
        )
        .unwrap();
        std::fs::write(dir.path().join("cabinetos.json"), "{}").unwrap();

        let path = dir.path().join("out.zip");
        let mut facts = Facts {
            windows_build: Some("10.0.26200.1 (25H2)".to_owned()),
            config: None,
        };
        let mut config = serde_json::json!({"logging": {"heavy": true}, "ai": {"api_key": "sk-1"}});
        mask::mask_secrets(&mut config);
        facts.config = Some(config);
        write_bundle(
            dir.path(),
            &path,
            &Bundle {
                reason: Reason::Asked,
                minutes: 10,
                process: "core",
                now,
            },
            &facts,
        )
        .unwrap();

        let entries = zip_entries(&path);
        let names: Vec<&str> = entries.iter().map(|(name, _)| name.as_str()).collect();
        assert_eq!(
            names,
            [
                "core.2026-09-30.jsonl",
                "crash-20260930T115000000Z.json",
                "heavy-core.2026-09-30.1.jsonl",
                "bundle.json"
            ]
        );
        let core = &entries[0].1;
        assert!(!core.contains("too old"));
        assert!(core.contains("just in") && core.contains("last"));
        assert_eq!(core.lines().count(), 2);

        let manifest: Value = serde_json::from_str(&entries[3].1).unwrap();
        assert_eq!(manifest["reason"], "asked");
        assert_eq!(manifest["minutes"], 10);
        assert_eq!(manifest["since"], ago(10));
        assert_eq!(manifest["windows_build"], "10.0.26200.1 (25H2)");
        assert_eq!(
            manifest["versions"]["protocol"],
            cabinetos_protocol::PROTOCOL_VERSION
        );
        assert_eq!(manifest["config"]["ai"]["api_key"], "***");
        assert_eq!(manifest["config"]["logging"]["heavy"], true);
        assert_eq!(manifest["files"][0]["lines"], 2);
    }

    #[test]
    fn keys_in_the_environment_are_masked() {
        let vars = [
            ("CABINETOS_LOG_DIR", r"D:\logs"),
            ("CABINETOS_OPENAI_API_KEY", "sk-secret"),
            ("PATH", r"C:\Windows"),
        ]
        .map(|(name, value)| (name.into(), value.into()));
        let picked = environment(vars.into_iter());
        assert_eq!(
            Value::Object(picked),
            serde_json::json!({"CABINETOS_LOG_DIR": r"D:\logs", "CABINETOS_OPENAI_API_KEY": "***"})
        );
    }

    #[test]
    fn a_large_file_is_read_from_its_end() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("core.2026-09-30.jsonl");
        let mut text = String::new();
        // About 3 MiB of old lines, then three recent ones.
        let old = line("2026-09-30T08:00:00.000Z", &"x".repeat(900));
        while text.len() < 3 * 1024 * 1024 {
            text.push_str(&old);
            text.push('\n');
        }
        for n in 0..3 {
            text.push_str(&line(
                &format!("2026-09-30T11:5{n}:00.000Z"),
                &format!("recent {n}"),
            ));
            text.push('\n');
        }
        std::fs::write(&path, &text).unwrap();
        let (lines, count) = recent_lines(&path, "2026-09-30T11:50:00.000Z").unwrap();
        assert_eq!(count, 3);
        let lines = String::from_utf8(lines).unwrap();
        assert!(
            lines.starts_with(&line("2026-09-30T11:50:00.000Z", "recent 0")),
            "{lines}"
        );
        assert!(lines.ends_with("recent 2\",\"thread\":\"x\"}\n"));
    }
}
