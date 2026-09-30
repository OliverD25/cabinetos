//! Running the user's programs: the command `program.<name>` of each
//! `programs` entry (Phase 18, ADR 0015).
//!
//! The window never builds a command line (brief §1): it runs the command,
//! and the core fills in `{path}`, `{selection}` and `{cwd}` from the state
//! the window last sent (`window_state`), finds the program as
//! `files.editor` finds its editor, and starts it through the shell as
//! `edit_path` does. Nothing but a listed program can start this way; each
//! refusal is logged with the program's name.

use cabinetos_config::{ArgPart, ProgramEntry, parse_arg};
use cabinetos_protocol::{ErrorCode, Response, WindowState};
use serde_json::json;

/// The longest command line a program gets, in UTF-16 units: Windows takes
/// 32,767, and the rest is room for the program's own path.
pub(crate) const MAX_COMMAND_LINE: usize = 30_000;

/// What the tokens stand for, from a window's state.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Targets {
    /// `{path}`: the active pane's cursor row.
    pub(crate) path: Option<String>,
    /// `{selection}`: its marked rows, or the cursor row when none is
    /// marked.
    pub(crate) selection: Vec<String>,
    /// `{cwd}`: the folder of the active pane's tab in front (a tool's
    /// tab: the folder of its file).
    pub(crate) cwd: Option<String>,
}

impl Targets {
    pub(crate) fn from_state(state: &WindowState) -> Self {
        let pane = state.active();
        let tab = usize::try_from(pane.active)
            .ok()
            .and_then(|index| pane.tabs.get(index));
        let cwd = tab.map(|tab| match tab.tool {
            Some(_) => std::path::Path::new(&tab.path)
                .parent()
                .map_or_else(|| tab.path.clone(), |folder| folder.display().to_string()),
            None => tab.path.clone(),
        });
        let path = pane.cursor.clone().filter(|path| !path.is_empty());
        let selection = if pane.marked.is_empty() {
            path.iter().cloned().collect()
        } else {
            pane.marked.clone()
        };
        Self {
            path,
            selection,
            cwd,
        }
    }
}

/// The program's arguments with the tokens filled in: `{path}` and `{cwd}`
/// inside an argument, `{selection}` as one argument per path. A token
/// with nothing to stand for is an error that says which.
pub(crate) fn resolve_args(args: &[String], targets: &Targets) -> Result<Vec<String>, String> {
    let mut resolved = Vec::with_capacity(args.len());
    for arg in args {
        let parts = parse_arg(arg)?;
        if parts == [ArgPart::Selection] {
            if targets.selection.is_empty() {
                return Err("nothing is selected for {selection}".to_owned());
            }
            resolved.extend(targets.selection.iter().cloned());
            continue;
        }
        let mut text = String::new();
        for part in parts {
            match part {
                ArgPart::Text(piece) => text.push_str(piece),
                ArgPart::Path => text.push_str(
                    targets
                        .path
                        .as_deref()
                        .ok_or("no file or folder is focused for {path}")?,
                ),
                ArgPart::Cwd => text.push_str(
                    targets
                        .cwd
                        .as_deref()
                        .ok_or("the active pane shows no folder for {cwd}")?,
                ),
                // `parse_arg` allows it only as a whole argument.
                ArgPart::Selection => {}
            }
        }
        resolved.push(text);
    }
    Ok(resolved)
}

/// Starts `program` for the window whose state is `state`. Blocking: the
/// shell may take a moment to hand the program over.
pub(crate) fn run(program: &ProgramEntry, state: &WindowState) -> Response {
    let targets = Targets::from_state(state);
    let result = start(program, &targets);
    match result {
        Ok(started) => {
            tracing::info!(program = %program.name, started = %started, "program started");
            Response::CommandResult {
                result: json!({"program": program.name, "started": started}),
            }
        }
        Err((code, message)) => refuse(&program.name, code, message),
    }
}

/// The error reply for a program that does not start, logged at warn with
/// the program's name.
pub(crate) fn refuse(name: &str, code: ErrorCode, message: String) -> Response {
    tracing::warn!(program = %name, ?code, error = %message, "program refused");
    Response::Error { code, message }
}

fn start(program: &ProgramEntry, targets: &Targets) -> Result<String, (ErrorCode, String)> {
    let name = &program.name;
    let args = resolve_args(&program.args, targets).map_err(|message| {
        (
            ErrorCode::ProgramRefused,
            format!("program.{name}: {message}"),
        )
    })?;
    let Some(exe) = cabinetos_terminal::find_program(&program.command) else {
        return Err((
            ErrorCode::ProgramRefused,
            format!(
                "program.{name}: `{}` is neither a file nor a program on the PATH",
                program.command
            ),
        ));
    };
    let line = cabinetos_fs::command_line(&args);
    // The program's own path, quoted, and a space come before the arguments.
    let length = exe.as_os_str().encode_wide_len() + 3 + line.encode_utf16().count();
    if length > MAX_COMMAND_LINE {
        return Err((
            ErrorCode::CommandLineTooLong,
            format!(
                "program.{name}: its command line would have {length} characters; Windows takes about {MAX_COMMAND_LINE}. Select fewer files"
            ),
        ));
    }
    cabinetos_fs::start_program(&exe, &args, targets.cwd.as_deref()).map_err(|error| {
        (
            ErrorCode::ProgramRefused,
            format!("program.{name}: {error}"),
        )
    })?;
    Ok(exe.display().to_string())
}

/// The length of an OS string in UTF-16 units, as Windows counts a command
/// line.
trait WideLen {
    fn encode_wide_len(&self) -> usize;
}

impl WideLen for std::ffi::OsStr {
    fn encode_wide_len(&self) -> usize {
        use std::os::windows::ffi::OsStrExt;
        self.encode_wide().count()
    }
}

#[cfg(test)]
mod tests {
    use cabinetos_protocol::{Pane, PaneState, WindowPanes, WindowTab};

    use super::*;

    fn state(cursor: Option<&str>, marked: &[&str], tool: Option<&str>) -> WindowState {
        let pane = PaneState {
            tabs: vec![
                WindowTab {
                    path: r"C:\other".to_owned(),
                    locked: false,
                    tool: None,
                },
                WindowTab {
                    path: r"C:\work\notes\readme.md".to_owned(),
                    locked: false,
                    tool: tool.map(str::to_owned),
                },
            ],
            active: 1,
            cursor: cursor.map(str::to_owned),
            marked: marked.iter().map(|path| (*path).to_owned()).collect(),
        };
        WindowState {
            active_pane: Pane::Right,
            panes: WindowPanes {
                left: PaneState::default(),
                right: pane,
            },
        }
    }

    fn args(texts: &[&str]) -> Vec<String> {
        texts.iter().map(|text| (*text).to_owned()).collect()
    }

    #[test]
    fn the_tokens_come_from_the_active_pane() {
        let targets = Targets::from_state(&state(
            Some(r"C:\work\a b.txt"),
            &[r"C:\work\a b.txt", r"C:\work\c.md"],
            None,
        ));
        assert_eq!(targets.path.as_deref(), Some(r"C:\work\a b.txt"));
        assert_eq!(targets.selection, [r"C:\work\a b.txt", r"C:\work\c.md"]);
        assert_eq!(targets.cwd.as_deref(), Some(r"C:\work\notes\readme.md"));
        let resolved = resolve_args(
            &args(&["--goto={path}:1", "{selection}", "--in", "{cwd}"]),
            &targets,
        )
        .unwrap();
        assert_eq!(
            resolved,
            [
                r"--goto=C:\work\a b.txt:1",
                r"C:\work\a b.txt",
                r"C:\work\c.md",
                "--in",
                r"C:\work\notes\readme.md"
            ]
        );
    }

    #[test]
    fn without_marks_the_selection_is_the_cursor_row_and_a_tool_tab_s_folder_is_its_file_s() {
        let targets = Targets::from_state(&state(Some(r"C:\work\a.txt"), &[], Some("md-preview")));
        assert_eq!(targets.selection, [r"C:\work\a.txt"]);
        assert_eq!(targets.cwd.as_deref(), Some(r"C:\work\notes"));
    }

    #[test]
    fn a_token_with_nothing_to_stand_for_says_which() {
        let targets = Targets::from_state(&state(None, &[], None));
        let error = resolve_args(&args(&["{path}"]), &targets).unwrap_err();
        assert!(error.contains("{path}"), "{error}");
        let error = resolve_args(&args(&["{selection}"]), &targets).unwrap_err();
        assert!(error.contains("{selection}"), "{error}");
        // A program that wants neither runs without a cursor.
        assert_eq!(
            resolve_args(&args(&["{cwd}"]), &targets).unwrap(),
            [r"C:\work\notes\readme.md"]
        );
        let empty = WindowState::default();
        let error = resolve_args(&args(&["{cwd}"]), &Targets::from_state(&empty)).unwrap_err();
        assert!(error.contains("{cwd}"), "{error}");
    }

    #[test]
    fn an_unknown_program_on_the_path_is_refused_before_anything_starts() {
        let program = ProgramEntry {
            name: "nowhere".to_owned(),
            title: None,
            command: "cabinetos-no-such-program".to_owned(),
            args: args(&["{path}"]),
        };
        let reply = run(&program, &state(Some(r"C:\work\a.txt"), &[], None));
        let Response::Error { code, message } = reply else {
            panic!("{reply:?}")
        };
        assert_eq!(code, ErrorCode::ProgramRefused);
        assert!(
            message.contains("program.nowhere") && message.contains("cabinetos-no-such-program"),
            "{message}"
        );
    }

    #[test]
    fn a_command_line_over_the_limit_is_refused() {
        let marked: Vec<String> = (0..1000)
            .map(|i| format!(r"C:\a folder with a long name\file number {i:04}.txt"))
            .collect();
        let marked: Vec<&str> = marked.iter().map(String::as_str).collect();
        let program = ProgramEntry {
            name: "many".to_owned(),
            title: None,
            command: "cmd".to_owned(),
            args: args(&["/c", "rem", "{selection}"]),
        };
        let reply = run(&program, &state(Some(marked[0]), &marked, None));
        let Response::Error { code, message } = reply else {
            panic!("{reply:?}")
        };
        assert_eq!(code, ErrorCode::CommandLineTooLong);
        assert!(message.contains("program.many"), "{message}");
    }
}
