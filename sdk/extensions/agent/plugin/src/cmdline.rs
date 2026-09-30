//! The model's side of the command line: finding command lines in a reply,
//! splitting them into words, parsing them with the command line's own
//! definitions (`cabinetos-cli-args`, the code `cab` itself uses), and
//! keeping only the commands the agent may run.

use cabinetos_cli_args::{Command, OnConflictArg, SortArg, TransferArgs};

/// What a command does to the disk.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Access {
    /// Looks only: runs at every tier.
    Read,
    /// Changes files: proposed, applied or refused by the tier.
    Write,
}

/// The commands that only look, in the order the model's reference lists
/// them.
pub const READ_COMMANDS: [&str; 4] = ["ls", "describe", "search", "state"];
/// The commands that change files.
pub const WRITE_COMMANDS: [&str; 7] = [
    "mkdir", "mkfile", "rename", "copy", "move", "delete", "undo",
];

/// A command the agent knows how to run, with the options it supports.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum AgentCommand {
    Ls {
        path: String,
        long: bool,
        sort: Option<SortArg>,
        desc: bool,
    },
    Describe {
        path: String,
        from: u32,
        count: u32,
    },
    Search {
        query: String,
        limit: u32,
        root: Option<String>,
    },
    State {
        json: bool,
    },
    Mkdir {
        path: String,
    },
    Mkfile {
        path: String,
    },
    Rename {
        path: String,
        new_name: String,
    },
    Copy {
        sources: Vec<String>,
        destination: String,
    },
    Move {
        sources: Vec<String>,
        destination: String,
    },
    Delete {
        paths: Vec<String>,
    },
    Undo {
        job: Option<u64>,
    },
}

impl AgentCommand {
    /// Whether it only looks or changes files.
    #[must_use]
    pub fn access(&self) -> Access {
        match self {
            Self::Ls { .. } | Self::Describe { .. } | Self::Search { .. } | Self::State { .. } => {
                Access::Read
            }
            _ => Access::Write,
        }
    }
}

/// What a reply says: the words, and the command lines of its fenced blocks.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Reply {
    /// The text outside the fences, trimmed.
    pub text: String,
    /// One command line per entry, in order.
    pub lines: Vec<String>,
}

/// Splits a model's reply into its words and its command lines. A command
/// line is a line inside a fenced block (a line that starts with three
/// backticks opens and closes one, with or without a language name);
/// blank lines and lines that start with `#` are left out, and so is a
/// leading `$ ` or `cab `, which a model likes to write.
#[must_use]
pub fn split_reply(reply: &str) -> Reply {
    let mut text = Vec::new();
    let mut lines = Vec::new();
    let mut inside = false;
    for line in reply.lines() {
        let trimmed = line.trim();
        if trimmed.starts_with("```") {
            inside = !inside;
            continue;
        }
        if inside {
            let command = strip_prompt(trimmed);
            if !command.is_empty() && !command.starts_with('#') {
                lines.push(command.to_owned());
            }
        } else {
            text.push(line);
        }
    }
    Reply {
        text: text.join("\n").trim().to_owned(),
        lines,
    }
}

fn strip_prompt(line: &str) -> &str {
    let line = line.strip_prefix("$ ").unwrap_or(line).trim_start();
    for program in ["cab.exe ", "cab "] {
        if let Some(rest) = line.strip_prefix(program) {
            return rest.trim_start();
        }
    }
    line
}

/// Splits a command line into words. Words are separated by spaces; a word
/// may be quoted with `"` or `'`. Backslashes are plain characters, since
/// they are the separator of a Windows path; inside double quotes only
/// `\"` is an escape, for a quote.
pub fn split_words(line: &str) -> Result<Vec<String>, String> {
    let mut words = Vec::new();
    let mut word = String::new();
    let mut started = false;
    let mut chars = line.chars().peekable();
    while let Some(c) = chars.next() {
        match c {
            c if c.is_whitespace() => {
                if started {
                    words.push(std::mem::take(&mut word));
                    started = false;
                }
            }
            '"' => {
                started = true;
                loop {
                    match chars.next() {
                        Some('\\') if chars.peek() == Some(&'"') => {
                            chars.next();
                            word.push('"');
                        }
                        Some('"') => break,
                        Some(other) => word.push(other),
                        None => return Err("a double quote is not closed".to_owned()),
                    }
                }
            }
            '\'' => {
                started = true;
                loop {
                    match chars.next() {
                        Some('\'') => break,
                        Some(other) => word.push(other),
                        None => return Err("a single quote is not closed".to_owned()),
                    }
                }
            }
            other => {
                started = true;
                word.push(other);
            }
        }
    }
    if started {
        words.push(word);
    }
    Ok(words)
}

/// Parses one command line with the command line's own definitions, and
/// accepts only the commands the agent may run, with the options it
/// supports. The error is what the model is told.
pub fn parse_line(line: &str) -> Result<AgentCommand, String> {
    let words = split_words(line)?;
    let Some(first) = words.first() else {
        return Err("the line is empty".to_owned());
    };
    if !READ_COMMANDS.contains(&first.as_str()) && !WRITE_COMMANDS.contains(&first.as_str()) {
        return Err(format!(
            "`{first}` is not a command you may use; use one of: {}",
            READ_COMMANDS
                .iter()
                .chain(&WRITE_COMMANDS)
                .copied()
                .collect::<Vec<_>>()
                .join(", ")
        ));
    }
    let mut argv = vec![cabinetos_cli_args::PROGRAM.to_owned()];
    argv.extend(words);
    let cli = cabinetos_cli_args::parse(argv)?;
    command_of(cli.command)
}

fn unsupported(command: &str, option: &str) -> String {
    format!("{command}: {option} is not supported here")
}

/// The sources and the folder of a `copy` or a `move`, with the options
/// the agent leaves at their defaults: a preview cannot carry them.
fn transfer(name: &str, arguments: TransferArgs) -> Result<(Vec<String>, String), String> {
    if arguments.on_conflict != OnConflictArg::Ask {
        return Err(unsupported(name, "--on-conflict"));
    }
    if arguments.verify || arguments.resolve.is_some() || arguments.stats {
        return Err(unsupported(name, "--verify, --resolve and --stats"));
    }
    let mut paths = arguments.paths;
    let destination = paths.pop().ok_or("a source and a destination are needed")?;
    Ok((paths, destination))
}

fn command_of(command: Command) -> Result<AgentCommand, String> {
    Ok(match command {
        Command::Ls {
            path,
            long,
            hidden: _,
            sort,
            desc,
            watch,
        } => {
            if watch {
                return Err(unsupported("ls", "--watch"));
            }
            AgentCommand::Ls {
                path,
                long,
                sort,
                desc,
            }
        }
        Command::Describe { path, from, count } => AgentCommand::Describe { path, from, count },
        Command::Search { query, limit, root } => AgentCommand::Search { query, limit, root },
        Command::State { json, client } => {
            if client.is_some() {
                return Err(unsupported("state", "--client"));
            }
            AgentCommand::State { json }
        }
        Command::Mkdir { path } => AgentCommand::Mkdir { path },
        Command::Mkfile { path } => AgentCommand::Mkfile { path },
        Command::Rename { path, new_name } => AgentCommand::Rename { path, new_name },
        Command::Copy(arguments) => {
            let (sources, destination) = transfer("copy", arguments)?;
            AgentCommand::Copy {
                sources,
                destination,
            }
        }
        Command::Move(arguments) => {
            let (sources, destination) = transfer("move", arguments)?;
            AgentCommand::Move {
                sources,
                destination,
            }
        }
        Command::Delete {
            paths,
            permanent,
            resolve,
            stats,
        } => {
            if permanent {
                return Err(
                    "delete: --permanent is never allowed; delete sends files to the Recycle Bin"
                        .to_owned(),
                );
            }
            if resolve.is_some() || stats {
                return Err(unsupported("delete", "--resolve and --stats"));
            }
            AgentCommand::Delete { paths }
        }
        // `--last` is the same as no job: the newest one.
        Command::Undo { job, last: _ } => AgentCommand::Undo { job },
        _ => return Err("that command is not one you may use".to_owned()),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_reply_is_its_words_and_the_lines_of_its_fenced_blocks() {
        let reply = "I will look at the folder first.\n\n```bash\n$ cab ls C:\\Users\\me\\Pictures --long\n# a comment\n\ndescribe \"C:\\Users\\me\\My Pictures\" --count 5\n```\nThen I will rename.";
        let parsed = split_reply(reply);
        assert_eq!(
            parsed.text,
            "I will look at the folder first.\n\nThen I will rename."
        );
        assert_eq!(
            parsed.lines,
            [
                r"ls C:\Users\me\Pictures --long",
                r#"describe "C:\Users\me\My Pictures" --count 5"#
            ]
        );
        // No fence: only words. Several fences: every block counts.
        assert_eq!(split_reply("Nothing to do.").lines, Vec::<String>::new());
        assert_eq!(
            split_reply("```\nstate\n```\ntext\n```cab\ncab.exe mkdir C:\\x\n```").lines,
            ["state", r"mkdir C:\x"]
        );
    }

    #[test]
    fn words_split_at_spaces_and_keep_backslashes() {
        assert_eq!(
            split_words(r#"rename C:\a\b.txt "new name.txt""#).unwrap(),
            [r"rename", r"C:\a\b.txt", "new name.txt"]
        );
        assert_eq!(
            split_words(r#"a 'b c' "d \" e" ''"#).unwrap(),
            ["a", "b c", "d \" e", ""]
        );
        assert_eq!(split_words("  ").unwrap(), Vec::<String>::new());
        assert!(split_words("a \"b").unwrap_err().contains("double quote"));
        assert!(split_words("a 'b").unwrap_err().contains("single quote"));
    }

    #[test]
    fn the_commands_the_agent_may_run_parse_with_the_cli_s_own_definitions() {
        assert_eq!(
            parse_line(r"ls C:\x --long --sort size --desc").unwrap(),
            AgentCommand::Ls {
                path: r"C:\x".to_owned(),
                long: true,
                sort: Some(SortArg::Size),
                desc: true
            }
        );
        assert_eq!(
            parse_line("describe C:\\x --from 10 --count 20").unwrap(),
            AgentCommand::Describe {
                path: r"C:\x".to_owned(),
                from: 10,
                count: 20
            }
        );
        assert_eq!(
            parse_line("search cat --limit 5 --root C:\\pics").unwrap(),
            AgentCommand::Search {
                query: "cat".to_owned(),
                limit: 5,
                root: Some(r"C:\pics".to_owned())
            }
        );
        assert_eq!(
            parse_line("state --json").unwrap(),
            AgentCommand::State { json: true }
        );
        assert_eq!(
            parse_line(r"rename C:\a\x.txt y.txt").unwrap(),
            AgentCommand::Rename {
                path: r"C:\a\x.txt".to_owned(),
                new_name: "y.txt".to_owned()
            }
        );
        assert_eq!(
            parse_line(r"copy C:\a\1.txt C:\a\2.txt C:\b").unwrap(),
            AgentCommand::Copy {
                sources: vec![r"C:\a\1.txt".to_owned(), r"C:\a\2.txt".to_owned()],
                destination: r"C:\b".to_owned()
            }
        );
        assert_eq!(
            parse_line(r"move C:\a\1.txt C:\b").unwrap(),
            AgentCommand::Move {
                sources: vec![r"C:\a\1.txt".to_owned()],
                destination: r"C:\b".to_owned()
            }
        );
        assert_eq!(
            parse_line(r"delete C:\a\1.txt C:\a\2.txt").unwrap(),
            AgentCommand::Delete {
                paths: vec![r"C:\a\1.txt".to_owned(), r"C:\a\2.txt".to_owned()]
            }
        );
        assert_eq!(
            parse_line(r"mkdir C:\a\new").unwrap(),
            AgentCommand::Mkdir {
                path: r"C:\a\new".to_owned()
            }
        );
        assert_eq!(
            parse_line(r"mkfile C:\a\n.txt").unwrap(),
            AgentCommand::Mkfile {
                path: r"C:\a\n.txt".to_owned()
            }
        );
        assert_eq!(
            parse_line("undo 12").unwrap(),
            AgentCommand::Undo { job: Some(12) }
        );
        assert_eq!(
            parse_line("undo --last").unwrap(),
            AgentCommand::Undo { job: None }
        );
    }

    #[test]
    fn reads_and_writes_are_told_apart() {
        for (line, access) in [
            ("ls C:\\x", Access::Read),
            ("describe C:\\x", Access::Read),
            ("search a", Access::Read),
            ("state", Access::Read),
            ("mkdir C:\\x", Access::Write),
            ("mkfile C:\\x", Access::Write),
            ("rename C:\\x y", Access::Write),
            ("copy C:\\a C:\\b", Access::Write),
            ("move C:\\a C:\\b", Access::Write),
            ("delete C:\\a", Access::Write),
            ("undo --last", Access::Write),
        ] {
            assert_eq!(parse_line(line).unwrap().access(), access, "{line}");
        }
    }

    #[test]
    fn what_the_agent_may_not_run_is_refused_with_a_reason_for_the_model() {
        // Every command of `cab` that is not in the two lists.
        for line in [
            "shutdown",
            "config set ui.theme nord",
            "secret get anthropic",
            "plugins grant agent net",
            "term",
            "props C:\\x",
            "open C:\\x",
            "edit C:\\x",
            "log bundle",
            "ping",
            "--pipe other ls C:\\x",
        ] {
            let error = parse_line(line).unwrap_err();
            assert!(
                error.contains("is not a command you may use"),
                "{line}: {error}"
            );
            assert!(error.contains("ls, describe, search, state"), "{error}");
        }
        // Options a preview cannot carry, and the ones that would destroy.
        for (line, part) in [
            ("ls C:\\x --watch", "--watch"),
            ("state --client A#2", "--client"),
            ("copy C:\\a C:\\b --on-conflict overwrite", "--on-conflict"),
            ("move C:\\a C:\\b --verify", "--verify"),
            ("copy C:\\a C:\\b --resolve skip", "--resolve"),
            ("delete C:\\a --permanent", "never allowed"),
            ("delete C:\\a --stats", "--stats"),
        ] {
            let error = parse_line(line).unwrap_err();
            assert!(error.contains(part), "{line}: {error}");
        }
    }

    #[test]
    fn a_mistake_in_a_line_comes_back_as_the_parser_s_own_message() {
        let error = parse_line("rename C:\\only-one-argument").unwrap_err();
        assert!(
            error.contains("<NEW_NAME>") || error.contains("new_name"),
            "{error}"
        );
        let error = parse_line("ls").unwrap_err();
        assert!(error.contains("<PATH>"), "{error}");
        let error = parse_line("ls C:\\x --sort weight").unwrap_err();
        assert!(error.contains("weight"), "{error}");
        assert!(parse_line("copy C:\\only").is_err(), "needs a destination");
        assert!(parse_line("").unwrap_err().contains("empty"));
        assert!(parse_line("ls \"C:\\x").unwrap_err().contains("quote"));
    }
}
