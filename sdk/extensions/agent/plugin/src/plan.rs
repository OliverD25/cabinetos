//! From the commands that change files to the rows of a preview
//! (`preview_listing`). Every path is checked here first: absolute, plain,
//! and under the folders the agent may use. The core checks the rows again
//! and runs them in order as jobs.

use serde_json::{Value, json};

use crate::cmdline::AgentCommand;
use crate::paths;

/// The most rows one preview holds; more is refused so a mistake stays
/// small enough to read.
pub const MAX_ROWS: usize = 500;

/// One proposed change.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Row {
    /// The file or folder; for `create`, a trailing `\` makes a folder.
    pub path: String,
    /// `rename`, `move`, `copy`, `delete` or `create`.
    pub kind: &'static str,
    /// For `rename` the new name, for `move` and `copy` the folder.
    pub to: Option<String>,
}

impl Row {
    /// The row as `preview_listing` takes it.
    #[must_use]
    pub fn to_json(&self) -> Value {
        json!({ "path": self.path, "kind": self.kind, "to": self.to })
    }
}

/// The rows one write command stands for. `undo` has none: it is not a
/// change to propose but a reversal of one, and the agent runs it apart.
pub fn rows_of(command: &AgentCommand, roots: &[String]) -> Result<Vec<Row>, String> {
    let allowed = |path: &str| paths::check_allowed(path, roots);
    Ok(match command {
        AgentCommand::Mkdir { path } => vec![Row {
            path: format!("{}\\", allowed(path)?),
            kind: "create",
            to: None,
        }],
        AgentCommand::Mkfile { path } => vec![Row {
            path: allowed(path)?,
            kind: "create",
            to: None,
        }],
        AgentCommand::Rename { path, new_name } => {
            if new_name.contains(['\\', '/']) {
                return Err(format!(
                    "rename: `{new_name}` has a folder in it; a rename keeps the file in its folder and takes only a new name (use move to go elsewhere)"
                ));
            }
            vec![Row {
                path: allowed(path)?,
                kind: "rename",
                to: Some(new_name.clone()),
            }]
        }
        AgentCommand::Copy {
            sources,
            destination,
        } => transfer("copy", sources, destination, roots)?,
        AgentCommand::Move {
            sources,
            destination,
        } => transfer("move", sources, destination, roots)?,
        AgentCommand::Delete { paths } => paths
            .iter()
            .map(|path| {
                Ok(Row {
                    path: allowed(path)?,
                    kind: "delete",
                    to: None,
                })
            })
            .collect::<Result<_, String>>()?,
        _ => return Err("that command does not change files".to_owned()),
    })
}

fn transfer(
    kind: &'static str,
    sources: &[String],
    destination: &str,
    roots: &[String],
) -> Result<Vec<Row>, String> {
    let destination = paths::check_allowed(destination, roots)?;
    sources
        .iter()
        .map(|source| {
            Ok(Row {
                path: paths::check_allowed(source, roots)?,
                kind,
                to: Some(destination.clone()),
            })
        })
        .collect()
}

/// The one line a row stands for, for the chat and the audit log.
#[must_use]
pub fn describe(row: &Row) -> String {
    match (row.kind, &row.to) {
        ("create", _) if row.path.ends_with('\\') => format!("create the folder {}", row.path),
        ("create", _) => format!("create the file {}", row.path),
        ("rename", Some(to)) => format!("rename {} to {to}", row.path),
        (kind, Some(to)) => format!("{kind} {} into {to}", row.path),
        (kind, None) => format!("{kind} {}", row.path),
    }
}

/// The title of a preview: what was asked, short.
#[must_use]
pub fn title(prompt: &str) -> String {
    let one_line: String = prompt.split_whitespace().collect::<Vec<_>>().join(" ");
    let short: String = one_line.chars().take(60).collect();
    if one_line.chars().count() > 60 {
        format!("Agent: {short}...")
    } else {
        format!("Agent: {short}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::cmdline::parse_line;

    fn roots() -> Vec<String> {
        vec![r"C:\Users\Me".to_owned()]
    }

    fn rows(line: &str) -> Result<Vec<Row>, String> {
        rows_of(&parse_line(line).unwrap(), &roots())
    }

    #[test]
    fn each_write_command_becomes_its_rows() {
        assert_eq!(
            rows(r"mkdir C:\Users\Me\Sorted").unwrap(),
            [Row {
                path: r"C:\Users\Me\Sorted\".to_owned(),
                kind: "create",
                to: None
            }]
        );
        assert_eq!(
            rows(r"mkfile C:\Users\Me\n.txt").unwrap(),
            [Row {
                path: r"C:\Users\Me\n.txt".to_owned(),
                kind: "create",
                to: None
            }]
        );
        assert_eq!(
            rows(r"rename C:\Users\Me\a.jpg vacation_a.jpg").unwrap(),
            [Row {
                path: r"C:\Users\Me\a.jpg".to_owned(),
                kind: "rename",
                to: Some("vacation_a.jpg".to_owned())
            }]
        );
        let moved = rows(r"move C:\Users\Me\a.jpg C:\Users\Me\b.jpg C:\Users\Me\Sorted").unwrap();
        assert_eq!(moved.len(), 2);
        assert!(
            moved
                .iter()
                .all(|row| row.kind == "move" && row.to.as_deref() == Some(r"C:\Users\Me\Sorted"))
        );
        let copied = rows(r"copy C:\Users\Me\a.jpg D:\ignored").unwrap_err();
        assert!(copied.contains("outside the folders"), "{copied}");
        let deleted = rows(r"delete C:\Users\Me\a.jpg C:\Users\Me\b.jpg").unwrap();
        assert_eq!(
            deleted
                .iter()
                .map(|row| (row.kind, row.to.clone()))
                .collect::<Vec<_>>(),
            [("delete", None), ("delete", None)]
        );
    }

    #[test]
    fn a_path_outside_the_roots_or_a_rename_with_a_folder_is_refused() {
        assert!(
            rows(r"delete C:\Windows\x")
                .unwrap_err()
                .contains("outside")
        );
        assert!(
            rows(r"mkdir C:\Users\Me\..\x")
                .unwrap_err()
                .contains("`..`")
        );
        assert!(rows(r"mkdir relative").unwrap_err().contains("absolute"));
        let error = rows(r"rename C:\Users\Me\a.jpg C:\Users\Me\Other\a.jpg").unwrap_err();
        assert!(error.contains("use move"), "{error}");
        // A move is refused for a destination outside, though its source is fine.
        assert!(
            rows(r"move C:\Users\Me\a D:\x")
                .unwrap_err()
                .contains("outside")
        );
    }

    #[test]
    fn undo_is_not_a_row_and_a_row_is_json_the_core_takes() {
        assert!(rows_of(&AgentCommand::Undo { job: None }, &roots()).is_err());
        let row = Row {
            path: r"C:\a".to_owned(),
            kind: "rename",
            to: Some("b".to_owned()),
        };
        assert_eq!(
            row.to_json(),
            json!({ "path": r"C:\a", "kind": "rename", "to": "b" })
        );
        let row = Row {
            path: r"C:\a".to_owned(),
            kind: "delete",
            to: None,
        };
        assert_eq!(
            row.to_json(),
            json!({ "path": r"C:\a", "kind": "delete", "to": null })
        );
    }

    #[test]
    fn rows_read_as_one_line_and_a_title_is_short() {
        let row = |kind, path: &str, to: Option<&str>| Row {
            path: path.to_owned(),
            kind,
            to: to.map(str::to_owned),
        };
        assert_eq!(
            describe(&row("create", r"C:\x\", None)),
            r"create the folder C:\x\"
        );
        assert_eq!(
            describe(&row("create", r"C:\x.txt", None)),
            r"create the file C:\x.txt"
        );
        assert_eq!(
            describe(&row("rename", r"C:\a", Some("b"))),
            r"rename C:\a to b"
        );
        assert_eq!(
            describe(&row("move", r"C:\a", Some(r"C:\d"))),
            r"move C:\a into C:\d"
        );
        assert_eq!(describe(&row("delete", r"C:\a", None)), r"delete C:\a");
        assert_eq!(
            title("rename these\n to   vacation_*"),
            "Agent: rename these to vacation_*"
        );
        let long = title(&"word ".repeat(30));
        assert!(long.ends_with("...") && long.chars().count() < 75, "{long}");
    }
}
