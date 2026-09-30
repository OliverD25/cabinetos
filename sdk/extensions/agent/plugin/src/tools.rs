//! The commands that only look: `ls`, `describe`, `search` and `state`. The
//! plugin answers `ls` and `describe` itself from the folders it may read
//! (`fs:read`), and asks the core for `search` and `state`
//! (`core-request`). Their output goes back to the model, so it is plain
//! text and short.

use cabinetos_cli_args::SortArg;
use serde_json::{Value, json};

use crate::cmdline::AgentCommand;
use crate::host::{DirEntry, Host};
use crate::paths;
use crate::time;

/// The most lines `ls` prints; the rest is counted.
pub const MAX_LS_LINES: usize = 300;
/// The most entries `describe` describes at once.
pub const MAX_DESCRIBED: u32 = 200;
/// The most hits `search` prints.
pub const MAX_HITS: u32 = 200;

/// Runs a command that only looks and returns its output.
pub fn run_read(
    host: &dyn Host,
    command: &AgentCommand,
    roots: &[String],
) -> Result<String, String> {
    match command {
        AgentCommand::Ls {
            path,
            long,
            sort,
            desc,
        } => {
            let path = paths::check_allowed(path, roots)?;
            let entries = host.read_dir(&path)?;
            Ok(ls_text(
                &path,
                entries,
                *long,
                sort.unwrap_or(SortArg::Name),
                *desc,
            ))
        }
        AgentCommand::Describe { path, from, count } => {
            let path = paths::check_allowed(path, roots)?;
            let entries = host.read_dir(&path)?;
            Ok(describe_text(
                &path,
                entries,
                *from,
                (*count).min(MAX_DESCRIBED),
            ))
        }
        AgentCommand::Search { query, limit, root } => {
            search(host, query, *limit, root.as_deref(), roots)
        }
        AgentCommand::State { json } => state(host, *json),
        _ => Err("that command changes files; it is not a read".to_owned()),
    }
}

/// The entries in the order `ls` shows them: folders first, then the sort
/// key, reversed within each group when `desc`.
fn sorted(mut entries: Vec<DirEntry>, sort: SortArg, desc: bool) -> Vec<DirEntry> {
    let key = |entry: &DirEntry| -> (String, u64, u64) {
        let name = entry.name.to_lowercase();
        match sort {
            SortArg::Name => (name, 0, 0),
            SortArg::Size => (name, entry.size, 0),
            SortArg::Modified => (name, entry.modified_ms.unwrap_or(0), 0),
            SortArg::Kind => (name, 0, 0),
            SortArg::Extension => (paths::extension(&entry.name), 0, 0),
        }
    };
    entries.sort_by(|a, b| {
        // Folders first, whatever the order.
        b.is_dir.cmp(&a.is_dir).then_with(|| {
            let order = match sort {
                SortArg::Size | SortArg::Modified => {
                    let (a_name, a_number, _) = key(a);
                    let (b_name, b_number, _) = key(b);
                    a_number.cmp(&b_number).then(a_name.cmp(&b_name))
                }
                SortArg::Extension => {
                    let (a_ext, ..) = key(a);
                    let (b_ext, ..) = key(b);
                    a_ext
                        .cmp(&b_ext)
                        .then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase()))
                }
                _ => a.name.to_lowercase().cmp(&b.name.to_lowercase()),
            };
            if desc { order.reverse() } else { order }
        })
    });
    entries
}

fn size_text(size: u64) -> String {
    let mut digits = size.to_string();
    let mut grouped = String::new();
    while digits.len() > 3 {
        let tail = digits.split_off(digits.len() - 3);
        grouped = format!(",{tail}{grouped}");
    }
    format!("{digits}{grouped}")
}

fn modified_text(entry: &DirEntry) -> String {
    entry
        .modified_ms
        .map_or_else(|| "-".to_owned(), |ms| format!("{} UTC", time::minute(ms)))
}

fn ls_text(path: &str, entries: Vec<DirEntry>, long: bool, sort: SortArg, desc: bool) -> String {
    let total = entries.len();
    let entries = sorted(entries, sort, desc);
    let mut lines: Vec<String> = entries
        .iter()
        .take(MAX_LS_LINES)
        .map(|entry| {
            let glyph = if entry.is_dir { 'd' } else { 'f' };
            if long {
                let size = if entry.is_dir {
                    String::new()
                } else {
                    size_text(entry.size)
                };
                format!(
                    "{glyph} {}  {size:>15}  {}",
                    modified_text(entry),
                    entry.name
                )
            } else {
                format!("{glyph} {}", entry.name)
            }
        })
        .collect();
    if total > MAX_LS_LINES {
        lines.push(format!(
            "... {} more entries not shown (describe {path} --from {MAX_LS_LINES} lists them)",
            total - MAX_LS_LINES
        ));
    }
    lines.push(format!("{total} entries in {path}"));
    lines.join("\n")
}

/// The type name a person sees: Windows says "JPG File" for `a.jpg`.
fn type_name(entry: &DirEntry) -> String {
    if entry.is_dir {
        return "File folder".to_owned();
    }
    match paths::extension(&entry.name).as_str() {
        "" => "File".to_owned(),
        extension => format!("{} File", extension.to_uppercase()),
    }
}

fn describe_text(path: &str, entries: Vec<DirEntry>, from: u32, count: u32) -> String {
    let total = entries.len();
    let entries = sorted(entries, SortArg::Name, false);
    let start = usize::try_from(from).unwrap_or(usize::MAX).min(total);
    let mut lines: Vec<String> = entries
        .iter()
        .enumerate()
        .skip(start)
        .take(usize::try_from(count).unwrap_or(0))
        .map(|(index, entry)| {
            let size = if entry.is_dir {
                "-".to_owned()
            } else {
                size_text(entry.size)
            };
            format!(
                "{index}: {} | {} | {size} | {}",
                entry.name,
                type_name(entry),
                modified_text(entry)
            )
        })
        .collect();
    lines.push(format!(
        "entries {start} to {} of {total} in {path}",
        (start + lines.len()).saturating_sub(1).max(start)
    ));
    lines.join("\n")
}

fn search(
    host: &dyn Host,
    query: &str,
    limit: u32,
    root: Option<&str>,
    roots: &[String],
) -> Result<String, String> {
    // The walk or the index starts at a folder the agent may use; hits
    // outside its folders are left out below.
    let root = match root {
        Some(root) => paths::check_allowed(root, roots)?,
        None => roots
            .first()
            .cloned()
            .ok_or("the agent has no folder to search")?,
    };
    let limit = limit.clamp(1, MAX_HITS);
    let reply = host.core_request(&json!({
        "type": "search", "query": query, "limit": limit, "root": root,
    }))?;
    if let Some(error) = reply_error(&reply) {
        return Err(error);
    }
    let hits = reply["hits"].as_array().cloned().unwrap_or_default();
    let mut lines = Vec::new();
    let mut hidden = 0;
    for hit in &hits {
        let path = hit["path"].as_str().unwrap_or_default();
        if !roots.iter().any(|allowed| paths::is_under(path, allowed)) {
            hidden += 1;
            continue;
        }
        let glyph = if hit["kind"] == "directory" { 'd' } else { 'f' };
        lines.push(format!("{glyph} {path}"));
    }
    let complete = reply["complete"].as_bool().unwrap_or(true);
    lines.push(format!(
        "{} hit(s); source: {}{}{}",
        lines.len(),
        reply["source"].as_str().unwrap_or("?"),
        if hidden > 0 {
            format!("; {hidden} outside your folders left out")
        } else {
            String::new()
        },
        if complete {
            ""
        } else {
            "; incomplete (a limit stopped the search)"
        },
    ));
    Ok(lines.join("\n"))
}

/// The message of an `error` reply, or `None` when the reply is not one.
#[must_use]
pub fn reply_error(reply: &Value) -> Option<String> {
    (reply["type"] == "error").then(|| {
        format!(
            "{} ({})",
            reply["message"].as_str().unwrap_or("the core refused"),
            reply["code"].as_str().unwrap_or("error")
        )
    })
}

/// What the window shows, from `get_window_state`: the reply, or the words
/// for a core that has no window yet. `None` for no window.
pub fn window_state(host: &dyn Host) -> Option<Value> {
    let reply = host
        .core_request(&json!({ "type": "get_window_state" }))
        .ok()?;
    (reply["type"] == "window_state").then(|| reply["state"].clone())
}

fn state(host: &dyn Host, as_json: bool) -> Result<String, String> {
    let reply = host.core_request(&json!({ "type": "get_window_state" }))?;
    if let Some(error) = reply_error(&reply) {
        return Err(error);
    }
    let state = &reply["state"];
    if as_json {
        return Ok(serde_json::to_string_pretty(state).unwrap_or_default());
    }
    Ok(state_text(state))
}

/// The window's state as a few lines: each pane's tabs, its cursor and the
/// rows the user marked.
#[must_use]
pub fn state_text(state: &Value) -> String {
    let active = state["active_pane"].as_str().unwrap_or("left");
    let mut lines = vec![format!("The pane that has the keyboard: {active}")];
    for pane in ["left", "right"] {
        let view = &state["panes"][pane];
        let tabs = view["tabs"].as_array().cloned().unwrap_or_default();
        lines.push(format!("{pane} pane:"));
        if tabs.is_empty() {
            lines.push("  no tabs".to_owned());
        }
        let front = view["active"].as_u64().unwrap_or(0);
        for (index, tab) in tabs.iter().enumerate() {
            let mut line = format!(
                "  {}{}. {}",
                if index as u64 == front { "*" } else { " " },
                index + 1,
                tab["path"].as_str().unwrap_or("?")
            );
            if tab["locked"] == true {
                line.push_str(" [locked]");
            }
            if let Some(tool) = tab["tool"].as_str() {
                line.push_str(&format!(" [tool {tool}]"));
            }
            lines.push(line);
        }
        lines.push(format!(
            "  cursor: {}",
            view["cursor"].as_str().unwrap_or("none")
        ));
        let marked = view["marked"].as_array().cloned().unwrap_or_default();
        if marked.is_empty() {
            lines.push("  marked: 0".to_owned());
        } else {
            let shown: Vec<&str> = marked.iter().filter_map(Value::as_str).take(30).collect();
            let more = marked.len().saturating_sub(shown.len());
            lines.push(format!(
                "  marked: {}: {}{}",
                marked.len(),
                shown.join(", "),
                if more > 0 {
                    format!(" and {more} more")
                } else {
                    String::new()
                }
            ));
        }
    }
    lines.join("\n")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(name: &str, is_dir: bool, size: u64, modified_ms: Option<u64>) -> DirEntry {
        DirEntry {
            name: name.to_owned(),
            is_dir,
            size,
            modified_ms,
        }
    }

    fn folder() -> Vec<DirEntry> {
        vec![
            entry("b.txt", false, 20, Some(1_790_730_123_000)),
            entry("Zeta", true, 0, None),
            entry("a.JPG", false, 1_234_567, Some(0)),
            entry("alpha", true, 0, None),
        ]
    }

    #[test]
    fn ls_puts_folders_first_and_counts() {
        let text = ls_text(r"C:\x", folder(), false, SortArg::Name, false);
        assert_eq!(
            text,
            "d alpha\nd Zeta\nf a.JPG\nf b.txt\n4 entries in C:\\x"
        );
        let text = ls_text(r"C:\x", folder(), false, SortArg::Name, true);
        assert!(
            text.starts_with("d Zeta\nd alpha\nf b.txt\nf a.JPG"),
            "{text}"
        );
    }

    #[test]
    fn ls_long_shows_date_and_size_and_sorts_by_size() {
        let text = ls_text(r"C:\x", folder(), true, SortArg::Size, false);
        assert!(
            text.contains("f 1970-01-01 00:00 UTC        1,234,567  a.JPG"),
            "{text}"
        );
        assert!(
            text.contains("f 2026-09-30 01:02 UTC               20  b.txt"),
            "{text}"
        );
        let order: Vec<&str> = text
            .lines()
            .map(|line| line.rsplit("  ").next().unwrap())
            .collect();
        assert_eq!(&order[..4], ["alpha", "Zeta", "b.txt", "a.JPG"], "{text}");
    }

    #[test]
    fn a_long_folder_is_cut_and_says_how_to_see_the_rest() {
        let many: Vec<DirEntry> = (0..350)
            .map(|n| entry(&format!("f{n:03}.txt"), false, 1, None))
            .collect();
        let text = ls_text(r"C:\big", many, false, SortArg::Name, false);
        assert_eq!(text.lines().count(), MAX_LS_LINES + 2);
        assert!(text.contains("... 50 more entries not shown"), "{text}");
        assert!(text.ends_with("350 entries in C:\\big"));
    }

    #[test]
    fn describe_names_the_type_and_pages() {
        let text = describe_text(r"C:\x", folder(), 1, 2);
        assert_eq!(
            text,
            "1: Zeta | File folder | - | -\n2: a.JPG | JPG File | 1,234,567 | 1970-01-01 00:00 UTC\nentries 1 to 2 of 4 in C:\\x"
        );
        let past_the_end = describe_text(r"C:\x", folder(), 9, 5);
        assert_eq!(past_the_end, "entries 4 to 4 of 4 in C:\\x");
    }

    #[test]
    fn sizes_are_grouped_by_thousands() {
        assert_eq!(size_text(0), "0");
        assert_eq!(size_text(999), "999");
        assert_eq!(size_text(1000), "1,000");
        assert_eq!(size_text(12_345_678), "12,345,678");
    }

    #[test]
    fn the_window_state_reads_as_a_few_lines() {
        let state = json!({
            "active_pane": "right",
            "panes": {
                "left": { "tabs": [{ "path": "C:\\a", "locked": true, "tool": null }, { "path": "C:\\b", "locked": false, "tool": "markdown-preview" }],
                          "active": 1, "cursor": "C:\\b\\x.txt", "marked": [] },
                "right": { "tabs": [], "active": 0, "cursor": null, "marked": ["C:\\r\\1", "C:\\r\\2"] },
            }
        });
        assert_eq!(
            state_text(&state),
            "The pane that has the keyboard: right\nleft pane:\n   1. C:\\a [locked]\n  *2. C:\\b [tool markdown-preview]\n  cursor: C:\\b\\x.txt\n  marked: 0\nright pane:\n  no tabs\n  cursor: none\n  marked: 2: C:\\r\\1, C:\\r\\2"
        );
    }

    #[test]
    fn an_error_reply_reads_as_its_message() {
        assert_eq!(
            reply_error(&json!({ "type": "error", "code": "no_window", "message": "no window" }))
                .as_deref(),
            Some("no window (no_window)")
        );
        assert_eq!(reply_error(&json!({ "type": "ok" })), None);
    }
}
