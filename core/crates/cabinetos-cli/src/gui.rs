//! `pane`, `selection` and `copy`/`move` with `--selection`: the live GUI
//! context. The core answers `gui_context` from what the window last said
//! (`window_state`), so a `cab` typed in a CabinetOS terminal sees what the
//! window shows now, and no environment variable goes stale.
//!
//! The exit codes: 0 for success, 1 for a failure (no core, a job that did
//! not complete, a selection the window cut), 2 when there is nothing to act
//! on (no window, no selection, a pane that shows no folder). The second is
//! [`NothingToUse`].

use std::time::Duration;

use anyhow::{Context as _, anyhow, bail};
use cabinetos_cli_args::{Command, TransferArgs};
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{ErrorCode, JobKind, JobRequest, Pane, Request, Response};

use crate::{absolute, failure, jobs, resolution_of_resolve, say, send_waiting, transfer_options};

/// How long to wait for the context: the core answers from memory.
const CONTEXT_TIMEOUT: Duration = Duration::from_secs(5);

/// The exit code for "nothing to act on", apart from 1, a failure.
pub(crate) const NOTHING_TO_USE_EXIT: u8 = 2;

/// There is nothing to act on: no window has said what it shows, a pane shows
/// no folder, or nothing is selected. The program exits with code 2.
#[derive(Debug)]
pub(crate) struct NothingToUse(String);

impl std::fmt::Display for NothingToUse {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl std::error::Error for NothingToUse {}

fn nothing(message: impl Into<String>) -> anyhow::Error {
    anyhow::Error::new(NothingToUse(message.into()))
}

/// The core's `gui_context` answer.
#[derive(Debug, PartialEq, Eq)]
pub(crate) struct Context {
    pub(crate) active: Pane,
    pub(crate) left: Option<String>,
    pub(crate) right: Option<String>,
    pub(crate) selection: Vec<String>,
    pub(crate) selection_total: u32,
    pub(crate) cursor: Option<String>,
    /// Whether the window shows both panes.
    pub(crate) dual: bool,
}

fn pane_word(pane: Pane) -> &'static str {
    match pane {
        Pane::Left => "left",
        Pane::Right => "right",
    }
}

impl Context {
    /// The folder `pane` shows.
    fn folder(&self, pane: Pane) -> Option<&str> {
        match pane {
            Pane::Left => self.left.as_deref(),
            Pane::Right => self.right.as_deref(),
        }
    }

    /// The selection, when the core knows all of it. The window tells the
    /// core at most the first 1,000 marked rows and says how many there
    /// are; a copy or a move of part of a selection that reports success
    /// would be worse than a refusal.
    fn whole_selection(&self) -> anyhow::Result<&[String]> {
        let known = u32::try_from(self.selection.len()).unwrap_or(u32::MAX);
        if self.selection_total > known {
            bail!(
                "{} rows are selected in the {} pane, but the window told the core about only {known} of them; cab does not act on part of a selection",
                self.selection_total,
                pane_word(self.active)
            );
        }
        Ok(&self.selection)
    }

    /// The whole context as JSON, the same words the protocol uses.
    fn json(&self) -> serde_json::Value {
        serde_json::json!({
            "active": pane_word(self.active),
            "left": self.left,
            "right": self.right,
            "selection": self.selection,
            "selection_total": self.selection_total,
            "cursor": self.cursor,
            "dual": self.dual,
        })
    }

    /// Where `--dest` says: `opposite_pane`, the folder the other pane
    /// shows, or a path as the other commands read one (against the folder
    /// the shell is in). With one pane shown the other pane is hidden, and
    /// a copy into a folder the user cannot see is refused, as the window's
    /// own "copy to the other pane" refuses it.
    fn destination(&self, dest: &str) -> anyhow::Result<String> {
        if dest != "opposite_pane" {
            return absolute(dest);
        }
        if !self.dual {
            bail!("the other pane is hidden; show both panes or name a path");
        }
        let other = match self.active {
            Pane::Left => Pane::Right,
            Pane::Right => Pane::Left,
        };
        self.folder(other).map(str::to_owned).ok_or_else(|| {
            anyhow!(
                "the {} pane, the opposite pane, shows no folder (a tool tab is in front, or it is not a full path); name a folder with --dest",
                pane_word(other)
            )
        })
    }
}

/// Asks the core what the window shows.
async fn context(client: &mut PipeClient) -> anyhow::Result<Context> {
    let reply = send_waiting(client, Request::GuiContext, CONTEXT_TIMEOUT).await?;
    match reply.body {
        Response::GuiContext {
            active,
            left,
            right,
            selection,
            selection_total,
            cursor,
            dual,
        } => Ok(Context {
            active,
            left,
            right,
            selection,
            selection_total,
            cursor,
            dual,
        }),
        Response::Error {
            code: ErrorCode::NoWindow,
            ..
        } => Err(nothing(
            "no window has said what it shows; is a CabinetOS window open?",
        )),
        other => Err(failure("gui_context", &other)),
    }
}

/// `pane` and `selection`.
pub(crate) async fn command(client: &mut PipeClient, command: &Command) -> anyhow::Result<()> {
    match command {
        Command::Pane { left, right, json } => pane(client, *left, *right, *json).await,
        Command::Selection { json } => selection(client, *json).await,
        _ => unreachable!("only the GUI context commands come here"),
    }
}

/// `pane [--left|--right|--json]`.
async fn pane(client: &mut PipeClient, left: bool, right: bool, json: bool) -> anyhow::Result<()> {
    let context = context(client).await?;
    if json {
        say(format_args!(
            "{}",
            serde_json::to_string_pretty(&context.json())?
        ));
        return Ok(());
    }
    let asked = if left {
        Pane::Left
    } else if right {
        Pane::Right
    } else {
        context.active
    };
    match context.folder(asked) {
        Some(folder) => {
            say(format_args!("{folder}"));
            Ok(())
        }
        None => Err(nothing(format!(
            "the {} pane shows no folder (a tool tab is in front, or it is not a full path)",
            pane_word(asked)
        ))),
    }
}

/// `selection [--json]`.
async fn selection(client: &mut PipeClient, json: bool) -> anyhow::Result<()> {
    let context = context(client).await?;
    let paths = context.whole_selection()?;
    if json {
        say(format_args!("{}", serde_json::to_string_pretty(paths)?));
    } else {
        for path in paths {
            if !say(format_args!("{path}")) {
                break;
            }
        }
    }
    if paths.is_empty() {
        return Err(nothing(format!(
            "nothing is selected in the {} pane",
            pane_word(context.active)
        )));
    }
    Ok(())
}

/// `copy` or `move` with `--selection --dest`: the job of the active pane's
/// selection, followed as `copy` and `move` follow theirs.
pub(crate) async fn transfer(
    client: &mut PipeClient,
    kind: JobKind,
    arguments: &TransferArgs,
) -> anyhow::Result<()> {
    let dest = arguments
        .dest
        .as_deref()
        .context("--selection needs --dest")?;
    let context = context(client).await?;
    let sources = context.whole_selection()?.to_vec();
    if sources.is_empty() {
        return Err(nothing(format!(
            "nothing is selected in the {} pane",
            pane_word(context.active)
        )));
    }
    let destination = context.destination(dest)?;
    let verb = match kind {
        JobKind::Move => "moving",
        _ => "copying",
    };
    let count = sources.len();
    let items = if count == 1 { "item" } else { "items" };
    say(format_args!(
        "{verb} {count} selected {items} of the {} pane to {destination}",
        pane_word(context.active)
    ));
    tracing::info!(
        kind = ?kind,
        pane = pane_word(context.active),
        sources = count,
        destination = %destination,
        "starting a job from the selection"
    );
    let job = jobs::JobRun {
        request: JobRequest {
            kind,
            sources,
            destination: Some(destination),
            options: transfer_options(arguments),
        },
        resolve: arguments.resolve.map(resolution_of_resolve),
        stats: arguments.stats,
    };
    jobs::run(client, job).await
}

#[cfg(test)]
mod tests {
    use super::*;

    fn context(active: Pane, left: Option<&str>, right: Option<&str>, paths: &[&str]) -> Context {
        Context {
            active,
            left: left.map(str::to_owned),
            right: right.map(str::to_owned),
            selection: paths.iter().map(|path| (*path).to_owned()).collect(),
            selection_total: u32::try_from(paths.len()).unwrap(),
            cursor: paths.first().map(|path| (*path).to_owned()),
            dual: true,
        }
    }

    #[test]
    fn the_opposite_pane_is_refused_while_only_one_pane_shows() {
        let mut single = context(Pane::Left, Some(r"E:\l"), Some(r"D:\r"), &["a"]);
        single.dual = false;
        let refused = single.destination("opposite_pane").unwrap_err();
        assert_eq!(
            refused.to_string(),
            "the other pane is hidden; show both panes or name a path"
        );
        assert!(
            !refused.is::<NothingToUse>(),
            "a refused destination is a failure (1), not nothing (2)"
        );
        // A named path is still a destination with one pane shown.
        assert_eq!(single.destination(r"C:\x").unwrap(), r"C:\x");
        assert_eq!(single.json()["dual"], false);
    }

    #[test]
    fn the_opposite_pane_is_the_other_one_from_the_active_pane() {
        let left_active = context(Pane::Left, Some(r"E:\l"), Some(r"D:\r"), &[]);
        assert_eq!(left_active.destination("opposite_pane").unwrap(), r"D:\r");
        let right_active = context(Pane::Right, Some(r"E:\l"), Some(r"D:\r"), &[]);
        assert_eq!(right_active.destination("opposite_pane").unwrap(), r"E:\l");
        // A path is a path, whichever pane is active.
        assert_eq!(left_active.destination(r"C:\x y").unwrap(), r"C:\x y");
        let tool = context(Pane::Left, Some(r"E:\l"), None, &[]);
        let refused = tool.destination("opposite_pane").unwrap_err().to_string();
        assert!(refused.contains("right pane"), "{refused}");
        assert!(refused.contains("--dest"), "{refused}");
        assert!(
            !refused.contains("nothing") && tool.destination(r"C:\x").is_ok(),
            "a missing opposite folder is a failure, and a path still works"
        );
    }

    #[test]
    fn a_selection_the_window_cut_is_refused_not_cut() {
        let mut cut = context(Pane::Left, Some(r"E:\l"), None, &["a", "b"]);
        assert_eq!(cut.whole_selection().unwrap(), ["a", "b"]);
        cut.selection_total = 5000;
        let refused = cut.whole_selection().unwrap_err();
        let text = refused.to_string();
        assert!(text.contains("5000") && text.contains("only 2"), "{text}");
        assert!(
            !refused.is::<NothingToUse>(),
            "a cut selection is a failure (1), not nothing (2)"
        );
    }

    #[test]
    fn the_json_has_the_protocol_s_words_and_keeps_the_nulls() {
        let shown = context(Pane::Right, Some(r"E:\l"), None, &[r"D:\r\a"]).json();
        assert_eq!(
            shown,
            serde_json::json!({
                "active": "right", "left": "E:\\l", "right": null,
                "selection": ["D:\\r\\a"], "selection_total": 1, "cursor": "D:\\r\\a",
                "dual": true
            })
        );
    }

    #[test]
    fn nothing_to_use_is_found_through_the_error() {
        let error = nothing("no window");
        assert!(error.is::<NothingToUse>());
        assert_eq!(error.to_string(), "no window");
        assert!(!anyhow!("other").is::<NothingToUse>());
    }
}
