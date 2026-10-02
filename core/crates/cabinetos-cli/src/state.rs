//! `state`: what the window shows, as it last told the core
//! (`get_window_state`).

use cabinetos_fs::time::local_time;
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{Pane, PaneState, Request, Response, WindowState};

use crate::{failure, say, send};

/// FILETIME ticks (100 ns since 1601) at 1970-01-01 UTC.
const UNIX_EPOCH_TICKS: i64 = 116_444_736_000_000_000;

/// `state [--json] [--client <id>]`.
pub(crate) async fn state(
    client: &mut PipeClient,
    json: bool,
    named: Option<&str>,
) -> anyhow::Result<()> {
    let reply = send(
        client,
        Request::GetWindowState {
            client: named.map(str::to_owned),
        },
    )
    .await?;
    let Response::WindowState {
        client: sender,
        sent_at_ms,
        state,
    } = reply.body
    else {
        return Err(failure("state", &reply.body));
    };
    if json {
        say(format_args!("{}", serde_json::to_string_pretty(&state)?));
        return Ok(());
    }
    for line in table(&sender, sent_at_ms, &state) {
        if !say(format_args!("{line}")) {
            break;
        }
    }
    Ok(())
}

/// The human form: who sent it and when, then each pane with its tabs, its
/// cursor and how many rows are marked.
fn table(sender: &str, sent_at_ms: u64, state: &WindowState) -> Vec<String> {
    let mut lines = vec![format!("window {sender}, sent {}", when(sent_at_ms))];
    for (pane, name) in [(Pane::Left, "left"), (Pane::Right, "right")] {
        let pane_state = match pane {
            Pane::Left => &state.panes.left,
            Pane::Right => &state.panes.right,
        };
        let focus = if state.active_pane == pane {
            " (has the keyboard)"
        } else {
            ""
        };
        lines.push(format!("{name} pane{focus}"));
        lines.extend(pane_lines(pane_state));
    }
    lines
}

fn pane_lines(pane: &PaneState) -> Vec<String> {
    let mut lines = Vec::new();
    if pane.tabs.is_empty() {
        lines.push("  no tabs".to_owned());
    }
    for (index, tab) in pane.tabs.iter().enumerate() {
        let front = if usize::try_from(pane.active).is_ok_and(|active| active == index) {
            '*'
        } else {
            ' '
        };
        let mut line = format!("  {front}{:<3} {}", index + 1, tab.path);
        if tab.locked {
            line.push_str("  [locked]");
        }
        if let Some(tool) = &tab.tool {
            line.push_str("  [tool ");
            line.push_str(tool);
            line.push(']');
        }
        lines.push(line);
    }
    lines.push(format!(
        "  cursor: {}",
        pane.cursor.as_deref().unwrap_or("none")
    ));
    lines.push(format!("  marked: {}", pane.marked.len()));
    lines
}

/// `2026-09-30 01:02:03`, local time.
pub(crate) fn when(sent_at_ms: u64) -> String {
    let ticks = i64::try_from(sent_at_ms)
        .ok()
        .and_then(|ms| ms.checked_mul(10_000))
        .and_then(|ticks| ticks.checked_add(UNIX_EPOCH_TICKS));
    match ticks.and_then(local_time) {
        Some(t) => format!(
            "{:04}-{:02}-{:02} {:02}:{:02}:{:02}",
            t.year, t.month, t.day, t.hour, t.minute, t.second
        ),
        None => format!("{sent_at_ms} ms after 1970"),
    }
}

#[cfg(test)]
mod tests {
    use cabinetos_protocol::{WindowPanes, WindowTab};

    use super::*;

    #[test]
    fn the_table_names_the_panes_their_tabs_and_the_marks() {
        let state = WindowState {
            active_pane: Pane::Right,
            panes: WindowPanes {
                left: PaneState::default(),
                right: PaneState {
                    tabs: vec![
                        WindowTab {
                            path: r"D:\work".to_owned(),
                            locked: true,
                            tool: None,
                        },
                        WindowTab {
                            path: r"D:\work\README.md".to_owned(),
                            locked: false,
                            tool: Some("md-preview".to_owned()),
                        },
                    ],
                    active: 1,
                    cursor: Some(r"D:\work\a.txt".to_owned()),
                    marked: vec![r"D:\work\a.txt".to_owned(), r"D:\work\b.txt".to_owned()],
                    marked_total: None,
                },
            },
        };
        let lines = table("CabinetOS#2", 1_790_000_000_000, &state);
        assert!(
            lines[0].starts_with("window CabinetOS#2, sent 2026-"),
            "{lines:?}"
        );
        assert_eq!(
            lines[1..],
            [
                "left pane",
                "  no tabs",
                "  cursor: none",
                "  marked: 0",
                "right pane (has the keyboard)",
                r"   1   D:\work  [locked]",
                r"  *2   D:\work\README.md  [tool md-preview]",
                r"  cursor: D:\work\a.txt",
                "  marked: 2",
            ]
        );
    }
}
