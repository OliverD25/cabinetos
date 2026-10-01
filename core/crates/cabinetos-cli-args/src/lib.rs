//! The command line's arguments, as `clap` definitions: what `cabinetos-cli`
//! (and its second name, `cab`) accepts. They live in a crate of their own
//! so that the program and the AI agent extension read one definition: the
//! agent's plugin parses the lines a model writes with these types, and
//! generates the model's reference from these help texts, so `cab --help`
//! and what the model is told are the same words. Nothing here talks to the
//! core; `cabinetos-cli/src/main.rs` runs the commands.
//!
//! Builds for `wasm32-wasip2` too: `clap` is used without its colour and
//! suggestion features.
#![forbid(unsafe_code)]
// The doc comments are the command line's help text. A variant that needs
// none has none.
#![allow(missing_docs)]

use std::path::PathBuf;

use clap::{Args, CommandFactory, Parser, Subcommand, ValueEnum};

/// The name the model's reference gives the program.
pub const PROGRAM: &str = "cab";

/// Talks to a running cabinetos-core over its named pipe.
#[derive(Debug, Parser)]
#[command(name = "cabinetos-cli", version)]
pub struct Cli {
    /// Pipe token of the core: \\.\pipe\cabinetos-core-<TOKEN>.
    #[arg(long, global = true, value_name = "TOKEN", default_value = "dev")]
    pub pipe: String,

    /// Also write this client's log (cli.<date>.jsonl) into PATH.
    #[arg(long, global = true, value_name = "PATH")]
    pub log_dir: Option<PathBuf>,

    #[command(subcommand)]
    pub command: Command,
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum Command {
    /// Send ping requests and print each reply with its round-trip time.
    Ping {
        /// How many pings to send, one after another.
        #[arg(long, default_value_t = 1, value_parser = clap::value_parser!(u32).range(1..))]
        count: u32,
    },
    /// Ask the core to exit cleanly.
    Shutdown,
    /// Read the log folder: one action through every process (`log trace`),
    /// the newest lines of one process's file (`log tail`), or a zip of the
    /// last minutes of every log (`log bundle`).
    Log {
        #[command(subcommand)]
        action: LogAction,
    },
    /// List a directory: the core reads it into shared memory, this client
    /// maps it and prints it.
    Ls {
        /// The directory to list.
        path: String,
        /// Also show attributes, modification time (local) and size.
        #[arg(long)]
        long: bool,
        /// Include hidden and system entries.
        #[arg(long)]
        hidden: bool,
        /// What to sort by; directories always come first. Without it (and
        /// without --desc) the core's configured order applies.
        #[arg(long, value_enum)]
        sort: Option<SortArg>,
        /// Reverse the order within directories and within the rest.
        #[arg(long)]
        desc: bool,
        /// Keep watching and print a line for each refresh, until Ctrl+C.
        #[arg(long)]
        watch: bool,
    },
    /// List a folder and print the shell's type name and icon key of some
    /// of its entries.
    Describe {
        /// The folder.
        path: String,
        /// The first entry, by its place in the listing.
        #[arg(long, default_value_t = 0)]
        from: u32,
        /// How many entries, at most 512.
        #[arg(long, default_value_t = 50)]
        count: u32,
    },
    /// List a folder and find the entries whose names match patterns, as
    /// the pattern box and quick search do.
    Match {
        /// The folder.
        path: String,
        /// Total Commander's patterns: * and ?, ; between patterns, | before
        /// the ones to leave out; case is ignored. For example
        /// "*.txt;*.md|readme*".
        patterns: String,
        /// Folders never match.
        #[arg(long)]
        files_only: bool,
        /// Only the first match at or after this entry, going round to the
        /// start, as quick search asks.
        #[arg(long, value_name = "INDEX")]
        first_from: Option<u32>,
    },
    /// Write the icon of an icon key (as `describe` prints them) to a PNG
    /// file, for example: icon ext:.txt --size 32 --out txt.png.
    Icon {
        /// The key: folder, generic, ext:.txt or path:….
        key: String,
        /// Its size in pixels: 16, 24, 32 or 48.
        #[arg(long, default_value_t = 32)]
        size: u32,
        /// The file to write.
        #[arg(long, value_name = "FILE")]
        out: PathBuf,
    },
    /// Show which volume and physical disk a path is on.
    Volume {
        /// Any path on the volume; it does not have to exist.
        path: String,
    },
    /// List every volume that has a drive letter, with its disk.
    Volumes,
    /// Open a file or folder with its default application, as a
    /// double-click in Explorer does.
    Open {
        /// The file or folder.
        path: String,
    },
    /// Open a file for editing, never running it: with files.editor, else
    /// with its type's edit verb, else with Notepad.
    Edit {
        /// The file.
        path: String,
    },
    /// Show Windows' own property sheet: for one path its sheet, for
    /// several the combined one. The sheet belongs to the core and stays
    /// open until you close it.
    Props {
        /// The files and folders.
        #[arg(required = true, value_name = "PATH")]
        paths: Vec<String>,
    },
    /// Create a folder; its parent must exist.
    Mkdir {
        /// The new folder.
        path: String,
    },
    /// Create an empty file; its folder must exist, and nothing is
    /// replaced.
    Mkfile {
        /// The new file.
        path: String,
    },
    /// Rename a file or folder in the folder it is in; nothing is replaced.
    Rename {
        /// The file or folder.
        path: String,
        /// Its new name, without a folder.
        new_name: String,
    },
    /// Count the files, folders and bytes under paths, with the core's
    /// progress as it comes. Ctrl+C stops the count.
    Measure {
        /// The files and folders.
        #[arg(required = true, value_name = "PATH")]
        paths: Vec<String>,
    },
    /// Show or check the configuration file (cabinetos.json).
    Config {
        #[command(subcommand)]
        action: ConfigAction,
    },
    /// List the commands, or rank them as the command palette does.
    Commands {
        #[command(subcommand)]
        action: CommandsAction,
    },
    /// Show, change or follow the keybindings.
    Keys {
        #[command(subcommand)]
        action: KeysAction,
    },
    /// Copy files and folders into a folder, and follow the job.
    Copy(TransferArgs),
    /// Move files and folders into a folder, and follow the job.
    Move(TransferArgs),
    /// Delete files and folders (to the Recycle Bin unless --permanent), and
    /// follow the job.
    Delete {
        /// The files and folders to delete.
        #[arg(required = true, value_name = "PATH")]
        paths: Vec<String>,
        /// Delete for good instead of to the Recycle Bin.
        #[arg(long)]
        permanent: bool,
        /// Answer every conflict this way (overwrite clears a read-only
        /// attribute).
        #[arg(long, value_enum)]
        resolve: Option<ResolveArg>,
        /// At the end, print how many progress events arrived per second.
        #[arg(long)]
        stats: bool,
    },
    /// List the jobs the core knows, running and finished.
    Jobs,
    /// Pause, resume or cancel a job, or decide a conflict.
    Job {
        #[command(subcommand)]
        action: JobCommand,
    },
    /// List the Core Plugins, or reload, turn on, turn off or grant one.
    Plugins {
        #[command(subcommand)]
        action: PluginsAction,
    },
    /// Follow the events the core sends to every client.
    Events {
        #[command(subcommand)]
        action: EventsAction,
    },
    /// Search files and folders by name: the indexer's index when it runs,
    /// else a walk of one folder tree (at most 2 s and 200,000 entries).
    Search {
        /// Text the names must contain, compared without case.
        query: String,
        /// The most hits to print.
        #[arg(long, default_value_t = 50)]
        limit: u32,
        /// Only hits under this folder. Without it: every indexed volume, or
        /// (without an indexer) your profile folder.
        #[arg(long, value_name = "PATH")]
        root: Option<String>,
    },
    /// Ask about the indexer.
    Index {
        #[command(subcommand)]
        action: IndexAction,
    },
    /// Run a shell in the core, attached to this console until it exits
    /// (Ctrl+] detaches), or list, close or move the shells the core runs.
    Term(TermArgs),
    /// List the colour themes, or show one. `config set ui.theme <id>`
    /// changes the theme in effect.
    Themes {
        #[command(subcommand)]
        action: ThemesAction,
    },
    /// Browse the marketplace index (marketplace.index in the
    /// configuration), and install or remove extensions.
    Market {
        #[command(subcommand)]
        action: MarketAction,
    },
    /// Print what the window shows, as it last told the core: the two
    /// panes, their tabs, the cursor row and how many rows are marked.
    State {
        /// Print the state as the window sent it, as JSON.
        #[arg(long)]
        json: bool,
        /// Another window than the one that spoke last, by the name this
        /// command prints (for example CabinetOS#2).
        #[arg(long, value_name = "ID")]
        client: Option<String>,
    },
    /// Store, read, remove or list secrets, such as an API key, in the
    /// Windows Credential Manager (as CabinetOS/<name>). The core adds one
    /// to a plugin's web request; no plugin ever reads it.
    Secret {
        #[command(subcommand)]
        action: SecretAction,
    },
    /// Reverse a finished job as a new job, from the undo journal: renames
    /// and moves go back, copies go to the Recycle Bin, replaced files come
    /// back from their saved copies. A delete cannot be undone: restore
    /// from the Recycle Bin.
    Undo {
        /// The job to undo, as `jobs` lists it.
        #[arg(required_unless_present = "last", conflicts_with = "last")]
        job: Option<u64>,
        /// Undo the newest job that is not an undo and was not undone yet.
        #[arg(long)]
        last: bool,
    },
    /// See whether a newer CabinetOS is out, download it, put it in place,
    /// or go back to the version before. Only a per-user install of a
    /// release updates itself. Without an action: status.
    Update {
        #[command(subcommand)]
        action: Option<UpdateAction>,
        /// Print the core's reply as JSON.
        #[arg(long, global = true)]
        json: bool,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Subcommand)]
pub enum UpdateAction {
    /// Print where the updater is: the version running, the newest of the
    /// channel, what is downloaded, the version kept for a rollback.
    Status,
    /// Read the channel's latest.json now.
    Check,
    /// Download the newer version, check its SHA-256 and unpack it, with a
    /// progress line.
    Download,
    /// Put the downloaded version in place; the running version goes into
    /// previous\ in the install folder. Restart CabinetOS to run it.
    Apply,
    /// Bring the version in previous\ back. Restart CabinetOS to run it.
    Rollback,
    /// Open no update dialog for a day, as Later does.
    Snooze,
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum SecretAction {
    /// Store a secret, replacing one of that name. The value comes from
    /// --value, else from standard input (one line ending at its end is
    /// dropped): echo sk-... | cabinetos-cli secret set anthropic.
    Set {
        /// 1 to 128 letters, digits, -, _ and ., for example anthropic.
        name: String,
        /// The value. Without it, standard input; that keeps the value out
        /// of the shell's history.
        #[arg(long)]
        value: Option<String>,
    },
    /// Print a secret's value.
    Get {
        /// The secret's name.
        name: String,
    },
    /// Remove a secret.
    Delete {
        /// The secret's name.
        name: String,
    },
    /// Print the names of the stored secrets, never their values.
    List,
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum MarketAction {
    /// Read the index now and print its items.
    Refresh,
    /// Search the index (read first when needed), best first.
    Search {
        /// Text to look for in the name, the ID or the publisher.
        query: String,
        /// Only items of this kind.
        #[arg(long, value_enum)]
        kind: Option<KindArg>,
    },
    /// Download an extension, check its SHA-256 and install it, with a
    /// progress line. A plugin then waits for review (plugins list).
    Install {
        /// The extension's ID in the index.
        id: String,
        /// The version; without it, the newest this core can run.
        #[arg(long)]
        version: Option<String>,
    },
    /// Remove exactly the files an install put in place.
    Uninstall {
        /// The extension's ID.
        id: String,
    },
    /// List the installed Tool Extensions.
    Tools,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
pub enum KindArg {
    Plugin,
    Theme,
    Tool,
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum ThemesAction {
    /// Print every valid theme in the themes folder; `*` marks the one in
    /// effect.
    List,
    /// Print a whole theme as JSON: the one named, or the one in effect.
    Show {
        /// The theme's ID, for example nord.
        id: Option<String>,
    },
}

/// `term`: a new session, or an action on the sessions.
#[derive(Debug, PartialEq, Eq, Args)]
#[command(args_conflicts_with_subcommands = true)]
pub struct TermArgs {
    #[command(subcommand)]
    pub action: Option<TermAction>,
    /// A profile from terminal.profiles; without it, terminal.defaultProfile.
    #[arg(long, value_name = "NAME")]
    pub profile: Option<String>,
    /// The folder the shell starts in; without it, this folder.
    #[arg(long, value_name = "PATH")]
    pub cwd: Option<String>,
    /// The file pane the session belongs to.
    #[arg(long, value_enum, default_value_t = PaneArg::Left)]
    pub pane: PaneArg,
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum TermAction {
    /// Print every session: its ID, profile, pane, mode, process ID, size,
    /// state, whether a client is attached, and the folder it started in.
    List,
    /// Close a session: its shell gets a hang-up.
    Close {
        /// The session's ID.
        id: u64,
    },
    /// Type paths at a session's prompt, quoted for its shell, without
    /// Enter, as Ctrl+Alt+P and Ctrl+Shift+Enter do.
    Type {
        /// The session's ID.
        id: u64,
        /// The paths.
        #[arg(required = true, value_name = "PATH")]
        paths: Vec<String>,
    },
    /// Lock a session, or link it to its pane (a profile that is not
    /// linkable stays locked).
    Mode {
        /// The session's ID.
        id: u64,
        /// The new mode.
        #[arg(value_enum)]
        mode: ModeArg,
    },
}

/// A file pane on the command line.
#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
pub enum PaneArg {
    Left,
    Right,
}

/// A terminal session's mode on the command line.
#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
pub enum ModeArg {
    Locked,
    Linked,
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum IndexAction {
    /// Print whether an indexer answers, and each volume's state.
    Status,
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum PluginsAction {
    /// Print every installed plugin with its state, capabilities and
    /// commands.
    List {
        /// Print the list as JSON, as the core sends it.
        #[arg(long)]
        json: bool,
    },
    /// Start a plugin again from its folder, after a crash or a change.
    Reload {
        /// The plugin's ID.
        id: String,
    },
    /// Turn a plugin on; the core writes the configuration file.
    Enable {
        /// The plugin's ID.
        id: String,
    },
    /// Turn a plugin off; the core writes the configuration file.
    Disable {
        /// The plugin's ID.
        id: String,
    },
    /// Grant capabilities a plugin asks for, for example: plugins grant
    /// reader cmd:register fs:read. The core writes the configuration file.
    Grant {
        /// The plugin's ID.
        id: String,
        /// Capability names.
        #[arg(required = true, value_name = "CAPABILITY")]
        capabilities: Vec<String>,
    },
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum EventsAction {
    /// Print each event as one JSON line, until Ctrl+C.
    Watch,
}

/// The paths and options of `copy` and `move`.
#[derive(Debug, PartialEq, Eq, Args)]
pub struct TransferArgs {
    /// The files and folders, then the folder they go into (created if it
    /// does not exist).
    #[arg(required = true, num_args = 2.., value_name = "PATH")]
    pub paths: Vec<String>,
    /// What to do when a file already exists at the destination.
    #[arg(long, value_enum, default_value = "ask")]
    pub on_conflict: OnConflictArg,
    /// Compare each copy with its source (sizes and sampled bytes).
    #[arg(long)]
    pub verify: bool,
    /// Answer every conflict this way instead of waiting for `job resolve`.
    #[arg(long, value_enum)]
    pub resolve: Option<ResolveArg>,
    /// At the end, print how many progress events arrived per second.
    #[arg(long)]
    pub stats: bool,
}

/// `--on-conflict`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
pub enum OnConflictArg {
    /// Set the file aside and report a conflict.
    Ask,
    Overwrite,
    Skip,
    /// Give the new file a free name: `name (2).ext`.
    Rename,
    /// Overwrite only when the source is newer.
    Newer,
}

/// `--resolve`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
pub enum ResolveArg {
    Overwrite,
    Skip,
    Rename,
    /// For a delete: what the Recycle Bin cannot take is deleted for good.
    DeletePermanently,
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum JobCommand {
    /// Pause a job: no bytes move until it is resumed.
    Pause {
        /// The job's ID.
        id: u64,
    },
    /// Resume a paused job.
    Resume {
        /// The job's ID.
        id: u64,
    },
    /// Cancel a job; a partly copied file is removed.
    Cancel {
        /// The job's ID.
        id: u64,
    },
    /// Decide what happens to a file that waits on a conflict.
    Resolve {
        /// The job's ID.
        job: u64,
        /// The conflict's ID, as `copy` printed it.
        conflict: u64,
        /// The decision.
        #[arg(value_enum)]
        resolution: ResolutionArg,
    },
}

/// `job resolve`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
pub enum ResolutionArg {
    Overwrite,
    Skip,
    /// Under a free name: `name (2).ext`.
    Rename,
    /// Try once more, the same way.
    Retry,
    /// Delete for good what the Recycle Bin cannot take.
    DeletePermanently,
    /// Stop the whole job.
    Cancel,
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum LogAction {
    /// Print every line of one user action (its trace ID) or one request
    /// (its request ID), from every process's normal and heavy files,
    /// oldest first.
    Trace {
        /// The trace or request ID, a ULID.
        id: String,
        /// The log folder. Without it: `CABINETOS_LOG_DIR`, else
        /// `%LOCALAPPDATA%\CabinetOS\logs`.
        #[arg(long, value_name = "PATH")]
        dir: Option<PathBuf>,
        /// Print the lines as they are in the files.
        #[arg(long)]
        json: bool,
    },
    /// Print the newest lines of one process's newest log file.
    Tail {
        /// The process: core, ui, indexer or cli.
        #[arg(long, default_value = "core")]
        process: String,
        /// Its heavy file, heavy-<process>.<date>.jsonl, instead.
        #[arg(long)]
        heavy: bool,
        /// How many lines.
        #[arg(short = 'n', long, default_value_t = 20)]
        lines: usize,
        /// Keep printing the lines added to the file, until Ctrl+C.
        #[arg(long)]
        follow: bool,
        /// Print the lines as they are in the file.
        #[arg(long)]
        json: bool,
        /// The log folder, as for `log trace`.
        #[arg(long, value_name = "PATH")]
        dir: Option<PathBuf>,
    },
    /// Ask the core for a log bundle: a zip in the log folder with the last
    /// minutes of every process's logs, the recent crash traces and facts
    /// about the machine. Prints the zip's path.
    Bundle {
        /// How many minutes back, 1 to 1440.
        #[arg(long, default_value_t = 10)]
        minutes: u32,
    },
}

impl LogAction {
    /// The minutes of `log bundle`; the other log commands never reach the
    /// core.
    pub fn minutes(&self) -> u32 {
        match self {
            Self::Bundle { minutes } => *minutes,
            _ => unreachable!("only log bundle needs the core"),
        }
    }
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum ConfigAction {
    /// Print the path of the configuration file the core reads.
    Path,
    /// Print the settings in effect, defaults included.
    Show,
    /// Print one setting in effect, for example: config get ui.dualPane.
    Get {
        /// The setting, as a dotted path.
        path: String,
    },
    /// Change one setting, for example: config set ui.dualPane false. The
    /// core checks the value and writes the configuration file.
    Set {
        /// The setting, as a dotted path.
        path: String,
        /// The new value as JSON: false, 3, "rail", ["D:\\work"]. A word
        /// that is not JSON is taken as text: rail.
        value: String,
    },
    /// Check a configuration file the way the core would, without a core.
    /// Without FILE, checks the file a core would read by default.
    Validate {
        /// The file to check.
        file: Option<PathBuf>,
    },
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum CommandsAction {
    /// Print every command with its keys.
    List {
        /// Print the list as JSON, as the core sends it.
        #[arg(long)]
        json: bool,
    },
    /// Rank the commands against QUERY, best first, as the palette does.
    Search {
        /// What the user would type into the palette.
        query: String,
    },
    /// Run a command and print its JSON result, for example: commands exec
    /// reader.size '{"path": "C:\\x.txt"}'.
    Exec {
        /// The command's ID.
        command: String,
        /// Its arguments, as JSON.
        args: Option<String>,
    },
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
pub enum KeysAction {
    /// Print every binding in effect.
    List,
    /// Bind COMMAND to KEYS, for example: keys set view.toggleSidebar "ctrl+alt+b".
    /// Empty KEYS ("") leave the command without keys. The core writes the
    /// change into the configuration file.
    Set {
        /// The command's ID.
        command: String,
        /// One combination, or a chord of two separated by a space.
        keys: String,
    },
    /// Give COMMAND its default keys back.
    Reset {
        /// The command's ID.
        command: String,
    },
    /// Print configuration and keymap changes as they happen, until Ctrl+C.
    Watch,
}

/// Sort keys on the command line.
#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
pub enum SortArg {
    Name,
    Size,
    Modified,
    Kind,
    Extension,
}

/// Parses the words of one command line, the program's name first, as
/// `cab ls C:\x` is `["cab", "ls", "C:\\x"]`. The error is `clap`'s own
/// message, usage included.
pub fn parse(args: impl IntoIterator<Item = String>) -> Result<Cli, String> {
    Cli::try_parse_from(args).map_err(|error| error.to_string())
}

/// The help text of each named command, as `cab <command> --help` prints
/// it (less its `--pipe` and `--log-dir` lines), joined by blank lines; a
/// name that is not a command is left out.
#[must_use]
pub fn reference(commands: &[&str]) -> String {
    let mut program = Cli::command()
        .name(PROGRAM)
        .bin_name(PROGRAM)
        .term_width(100);
    program.build();
    commands
        .iter()
        .filter_map(|name| {
            program
                .find_subcommand_mut(name)
                .map(|command| command.render_help().to_string())
        })
        .map(|help| without_global_options(&help))
        .collect::<Vec<_>>()
        .join("\n\n")
}

/// The help without the two options every command has, `--pipe` and
/// `--log-dir`: they say how to reach a core, which the model never does.
fn without_global_options(help: &str) -> String {
    help.lines()
        .filter(|line| {
            let option = line.trim_start();
            !(option.starts_with("--pipe ") || option.starts_with("--log-dir "))
        })
        .collect::<Vec<_>>()
        .join("\n")
        .trim_end()
        .to_owned()
}

/// The names of every command, in the order `cab --help` lists them.
#[must_use]
pub fn command_names() -> Vec<String> {
    Cli::command()
        .get_subcommands()
        .map(|command| command.get_name().to_owned())
        .collect()
}

#[cfg(test)]
mod tests {
    use clap::CommandFactory;

    use super::*;

    #[test]
    fn the_command_line_definition_is_valid() {
        Cli::command().debug_assert();
    }

    #[test]
    fn ping_defaults_to_one_ping_on_the_dev_pipe() {
        let cli = Cli::try_parse_from(["cabinetos-cli", "ping"]).unwrap();
        assert_eq!(cli.pipe, "dev");
        assert_eq!(cli.log_dir, None);
        assert_eq!(cli.command, Command::Ping { count: 1 });
    }

    #[test]
    fn parses_pipe_and_count() {
        let cli = Cli::try_parse_from(["cabinetos-cli", "--pipe", "demo", "ping", "--count", "3"])
            .unwrap();
        assert_eq!(cli.pipe, "demo");
        assert_eq!(cli.command, Command::Ping { count: 3 });
    }

    #[test]
    fn global_options_may_follow_the_command() {
        let cli = Cli::try_parse_from([
            "cabinetos-cli",
            "shutdown",
            "--pipe",
            "demo",
            "--log-dir",
            r"C:\logs",
        ])
        .unwrap();
        assert_eq!(cli.pipe, "demo");
        assert_eq!(cli.log_dir, Some(PathBuf::from(r"C:\logs")));
        assert_eq!(cli.command, Command::Shutdown);
    }

    #[test]
    fn parses_ls_with_every_option() {
        let cli = Cli::try_parse_from([
            "cabinetos-cli",
            "ls",
            r"C:\Windows",
            "--long",
            "--hidden",
            "--sort",
            "modified",
            "--desc",
            "--watch",
        ])
        .unwrap();
        assert_eq!(
            cli.command,
            Command::Ls {
                path: r"C:\Windows".to_owned(),
                long: true,
                hidden: true,
                sort: Some(SortArg::Modified),
                desc: true,
                watch: true,
            }
        );
        let plain = Cli::try_parse_from(["cabinetos-cli", "ls", "."]).unwrap();
        assert!(matches!(
            plain.command,
            Command::Ls {
                sort: None,
                long: false,
                watch: false,
                ..
            }
        ));
        assert!(
            Cli::try_parse_from(["cabinetos-cli", "ls"]).is_err(),
            "path is required"
        );
        assert!(Cli::try_parse_from(["cabinetos-cli", "ls", ".", "--sort", "colour"]).is_err());
    }

    #[test]
    fn parses_volume() {
        let cli = Cli::try_parse_from(["cabinetos-cli", "volume", r"H:\"]).unwrap();
        assert_eq!(
            cli.command,
            Command::Volume {
                path: r"H:\".to_owned()
            }
        );
        let cli = Cli::try_parse_from(["cabinetos-cli", "volumes"]).unwrap();
        assert_eq!(cli.command, Command::Volumes);
        let cli = Cli::try_parse_from(["cabinetos-cli", "describe", r"C:\Windows"]).unwrap();
        assert_eq!(
            cli.command,
            Command::Describe {
                path: r"C:\Windows".to_owned(),
                from: 0,
                count: 50
            }
        );
        let cli = Cli::try_parse_from([
            "cabinetos-cli",
            "describe",
            ".",
            "--from",
            "10",
            "--count",
            "5",
        ])
        .unwrap();
        assert!(matches!(
            cli.command,
            Command::Describe {
                from: 10,
                count: 5,
                ..
            }
        ));
        let cli = Cli::try_parse_from([
            "cabinetos-cli",
            "icon",
            "ext:.txt",
            "--size",
            "48",
            "--out",
            "t.png",
        ])
        .unwrap();
        assert_eq!(
            cli.command,
            Command::Icon {
                key: "ext:.txt".to_owned(),
                size: 48,
                out: PathBuf::from("t.png")
            }
        );
        assert!(
            Cli::try_parse_from(["cabinetos-cli", "icon", "folder"]).is_err(),
            "--out is required"
        );
    }

    #[test]
    fn parses_file_commands() {
        let cli = Cli::try_parse_from(["cabinetos-cli", "open", "notes.txt"]).unwrap();
        assert_eq!(
            cli.command,
            Command::Open {
                path: "notes.txt".to_owned()
            }
        );
        assert!(Cli::try_parse_from(["cabinetos-cli", "open"]).is_err());
        let cli = Cli::try_parse_from(["cabinetos-cli", "edit", "build.cmd"]).unwrap();
        assert_eq!(
            cli.command,
            Command::Edit {
                path: "build.cmd".to_owned()
            }
        );
        assert!(Cli::try_parse_from(["cabinetos-cli", "edit"]).is_err());
        let cli = Cli::try_parse_from(["cabinetos-cli", "props", "a.txt", "photos"]).unwrap();
        assert_eq!(
            cli.command,
            Command::Props {
                paths: vec!["a.txt".to_owned(), "photos".to_owned()]
            }
        );
        assert!(Cli::try_parse_from(["cabinetos-cli", "props"]).is_err());
        let cli = Cli::try_parse_from(["cabinetos-cli", "measure", "photos", "a.txt"]).unwrap();
        assert_eq!(
            cli.command,
            Command::Measure {
                paths: vec!["photos".to_owned(), "a.txt".to_owned()]
            }
        );
        assert!(Cli::try_parse_from(["cabinetos-cli", "measure"]).is_err());
        let cli = Cli::try_parse_from([
            "cabinetos-cli",
            "match",
            ".",
            "*.txt;*.md|readme*",
            "--files-only",
            "--first-from",
            "12",
        ])
        .unwrap();
        assert_eq!(
            cli.command,
            Command::Match {
                path: ".".to_owned(),
                patterns: "*.txt;*.md|readme*".to_owned(),
                files_only: true,
                first_from: Some(12)
            }
        );
        assert!(Cli::try_parse_from(["cabinetos-cli", "match", "."]).is_err());
        let cli = Cli::try_parse_from(["cabinetos-cli", "mkdir", r"E:\new"]).unwrap();
        assert_eq!(
            cli.command,
            Command::Mkdir {
                path: r"E:\new".to_owned()
            }
        );
        let cli = Cli::try_parse_from(["cabinetos-cli", "mkfile", r"E:\new.txt"]).unwrap();
        assert_eq!(
            cli.command,
            Command::Mkfile {
                path: r"E:\new.txt".to_owned()
            }
        );
        assert!(Cli::try_parse_from(["cabinetos-cli", "mkfile"]).is_err());
        let cli = Cli::try_parse_from(["cabinetos-cli", "rename", "a.txt", "b 2.txt"]).unwrap();
        assert_eq!(
            cli.command,
            Command::Rename {
                path: "a.txt".to_owned(),
                new_name: "b 2.txt".to_owned()
            }
        );
        assert!(
            Cli::try_parse_from(["cabinetos-cli", "rename", "a.txt"]).is_err(),
            "a new name is required"
        );
    }

    #[test]
    fn parses_config_commands_and_keys() {
        let parse = |args: &[&str]| {
            let mut full = vec!["cabinetos-cli"];
            full.extend_from_slice(args);
            Cli::try_parse_from(full).map(|cli| cli.command)
        };
        assert_eq!(
            parse(&["config", "path"]).unwrap(),
            Command::Config {
                action: ConfigAction::Path
            }
        );
        assert_eq!(
            parse(&["config", "validate", r"D:\c.json"]).unwrap(),
            Command::Config {
                action: ConfigAction::Validate {
                    file: Some(PathBuf::from(r"D:\c.json"))
                }
            }
        );
        assert_eq!(
            parse(&["config", "validate"]).unwrap(),
            Command::Config {
                action: ConfigAction::Validate { file: None }
            }
        );
        assert_eq!(
            parse(&["config", "get", "ui.dualPane"]).unwrap(),
            Command::Config {
                action: ConfigAction::Get {
                    path: "ui.dualPane".to_owned()
                }
            }
        );
        assert_eq!(
            parse(&["config", "set", "ui.pinned", r#"["D:\\work"]"#]).unwrap(),
            Command::Config {
                action: ConfigAction::Set {
                    path: "ui.pinned".to_owned(),
                    value: r#"["D:\\work"]"#.to_owned()
                }
            }
        );
        assert!(
            parse(&["config", "set", "ui.dualPane"]).is_err(),
            "a value is required"
        );
        assert_eq!(
            parse(&["commands", "list", "--json"]).unwrap(),
            Command::Commands {
                action: CommandsAction::List { json: true }
            }
        );
        assert_eq!(
            parse(&["commands", "search", "dual"]).unwrap(),
            Command::Commands {
                action: CommandsAction::Search {
                    query: "dual".to_owned()
                }
            }
        );
        assert_eq!(
            parse(&["keys", "set", "view.toggleSidebar", "ctrl+k ctrl+b"]).unwrap(),
            Command::Keys {
                action: KeysAction::Set {
                    command: "view.toggleSidebar".to_owned(),
                    keys: "ctrl+k ctrl+b".to_owned()
                }
            }
        );
        assert_eq!(
            parse(&["keys", "set", "view.toggleSidebar", ""]).unwrap(),
            Command::Keys {
                action: KeysAction::Set {
                    command: "view.toggleSidebar".to_owned(),
                    keys: String::new()
                }
            }
        );
        assert_eq!(
            parse(&["keys", "reset", "view.toggleSidebar"]).unwrap(),
            Command::Keys {
                action: KeysAction::Reset {
                    command: "view.toggleSidebar".to_owned()
                }
            }
        );
        assert!(
            parse(&["keys", "set", "view.toggleSidebar"]).is_err(),
            "keys are required"
        );
        assert!(
            parse(&["commands", "search"]).is_err(),
            "a query is required"
        );
    }

    #[test]
    fn parses_copy_move_delete_and_job() {
        let parse = |args: &[&str]| {
            let mut full = vec!["cabinetos-cli"];
            full.extend_from_slice(args);
            Cli::try_parse_from(full).map(|cli| cli.command)
        };
        let Command::Copy(copy) = parse(&[
            "copy",
            "a",
            "b",
            r"E:\dst",
            "--on-conflict",
            "newer",
            "--verify",
            "--resolve",
            "skip",
            "--stats",
        ])
        .unwrap() else {
            panic!("expected copy")
        };
        assert_eq!(copy.paths, ["a", "b", r"E:\dst"]);
        assert_eq!(copy.on_conflict, OnConflictArg::Newer);
        assert!(copy.verify && copy.stats);
        assert_eq!(copy.resolve, Some(ResolveArg::Skip));
        assert!(
            parse(&["copy", "only-one"]).is_err(),
            "a source and a destination"
        );
        assert!(matches!(
            parse(&["move", "a", "b"]).unwrap(),
            Command::Move(_)
        ));
        assert_eq!(
            parse(&["delete", "x", "--permanent"]).unwrap(),
            Command::Delete {
                paths: vec!["x".to_owned()],
                permanent: true,
                resolve: None,
                stats: false,
            }
        );
        assert_eq!(parse(&["jobs"]).unwrap(), Command::Jobs);
        assert_eq!(
            parse(&["job", "resolve", "3", "9", "rename"]).unwrap(),
            Command::Job {
                action: JobCommand::Resolve {
                    job: 3,
                    conflict: 9,
                    resolution: ResolutionArg::Rename,
                }
            }
        );
        assert_eq!(
            parse(&["job", "cancel", "3"]).unwrap(),
            Command::Job {
                action: JobCommand::Cancel { id: 3 }
            }
        );
    }

    #[test]
    fn parses_plugins_commands_exec_and_events() {
        let parse = |args: &[&str]| {
            let mut full = vec!["cabinetos-cli"];
            full.extend_from_slice(args);
            Cli::try_parse_from(full).map(|cli| cli.command)
        };
        assert_eq!(
            parse(&["plugins", "list"]).unwrap(),
            Command::Plugins {
                action: PluginsAction::List { json: false }
            }
        );
        assert_eq!(
            parse(&["plugins", "reload", "crashy"]).unwrap(),
            Command::Plugins {
                action: PluginsAction::Reload {
                    id: "crashy".to_owned()
                }
            }
        );
        assert_eq!(
            parse(&["plugins", "disable", "hello"]).unwrap(),
            Command::Plugins {
                action: PluginsAction::Disable {
                    id: "hello".to_owned()
                }
            }
        );
        assert_eq!(
            parse(&["plugins", "grant", "reader", "cmd:register", "fs:read"]).unwrap(),
            Command::Plugins {
                action: PluginsAction::Grant {
                    id: "reader".to_owned(),
                    capabilities: vec!["cmd:register".to_owned(), "fs:read".to_owned()],
                }
            }
        );
        assert!(
            parse(&["plugins", "grant", "reader"]).is_err(),
            "a capability is required"
        );
        assert_eq!(
            parse(&["commands", "exec", "hello.say"]).unwrap(),
            Command::Commands {
                action: CommandsAction::Exec {
                    command: "hello.say".to_owned(),
                    args: None,
                }
            }
        );
        assert_eq!(
            parse(&["commands", "exec", "reader.size", r#"{"path":"C:\x"}"#]).unwrap(),
            Command::Commands {
                action: CommandsAction::Exec {
                    command: "reader.size".to_owned(),
                    args: Some(r#"{"path":"C:\x"}"#.to_owned()),
                }
            }
        );
        assert_eq!(
            parse(&["events", "watch"]).unwrap(),
            Command::Events {
                action: EventsAction::Watch
            }
        );
    }

    #[test]
    fn parses_search_and_index_status() {
        let parse = |args: &[&str]| {
            let mut full = vec!["cabinetos-cli"];
            full.extend_from_slice(args);
            Cli::try_parse_from(full).map(|cli| cli.command)
        };
        assert_eq!(
            parse(&["search", "budget"]).unwrap(),
            Command::Search {
                query: "budget".to_owned(),
                limit: 50,
                root: None,
            }
        );
        assert_eq!(
            parse(&["search", "foo", "--limit", "5", "--root", r"D:\work"]).unwrap(),
            Command::Search {
                query: "foo".to_owned(),
                limit: 5,
                root: Some(r"D:\work".to_owned()),
            }
        );
        assert!(parse(&["search"]).is_err(), "a query is required");
        assert_eq!(
            parse(&["index", "status"]).unwrap(),
            Command::Index {
                action: IndexAction::Status
            }
        );
    }

    #[test]
    fn parses_term_and_its_actions() {
        let parse = |args: &[&str]| {
            let mut full = vec!["cabinetos-cli"];
            full.extend_from_slice(args);
            Cli::try_parse_from(full).map(|cli| cli.command)
        };
        let term = |action: Option<TermAction>, profile: Option<&str>, cwd: Option<&str>| {
            Command::Term(TermArgs {
                action,
                profile: profile.map(str::to_owned),
                cwd: cwd.map(str::to_owned),
                pane: PaneArg::Left,
            })
        };
        assert_eq!(parse(&["term"]).unwrap(), term(None, None, None));
        assert_eq!(
            parse(&["term", "--profile", "pwsh", "--cwd", r"E:\"]).unwrap(),
            term(None, Some("pwsh"), Some(r"E:\"))
        );
        assert_eq!(
            parse(&["term", "--pane", "right"]).unwrap(),
            Command::Term(TermArgs {
                action: None,
                profile: None,
                cwd: None,
                pane: PaneArg::Right,
            })
        );
        assert!(parse(&["term", "--pane", "middle"]).is_err());
        assert_eq!(
            parse(&["term", "list"]).unwrap(),
            term(Some(TermAction::List), None, None)
        );
        assert_eq!(
            parse(&["term", "close", "3"]).unwrap(),
            term(Some(TermAction::Close { id: 3 }), None, None)
        );
        assert_eq!(
            parse(&["term", "mode", "3", "linked"]).unwrap(),
            term(
                Some(TermAction::Mode {
                    id: 3,
                    mode: ModeArg::Linked
                }),
                None,
                None
            )
        );
        assert!(
            parse(&["term", "--profile", "cmd", "list"]).is_err(),
            "an action takes no options"
        );
        assert!(parse(&["term", "close"]).is_err(), "an ID is required");
        assert!(parse(&["term", "mode", "3"]).is_err(), "a mode is required");
        assert!(parse(&["term", "mode", "3", "follow"]).is_err());
        assert!(
            parse(&["term", "cd", "3", r"D:\docs"]).is_err(),
            "the folder sync is gone"
        );
    }

    #[test]
    fn parses_themes() {
        let cli = Cli::try_parse_from(["cabinetos-cli", "themes", "list"]).unwrap();
        assert_eq!(
            cli.command,
            Command::Themes {
                action: ThemesAction::List
            }
        );
        let cli = Cli::try_parse_from(["cabinetos-cli", "themes", "show", "nord"]).unwrap();
        assert_eq!(
            cli.command,
            Command::Themes {
                action: ThemesAction::Show {
                    id: Some("nord".to_owned())
                }
            }
        );
        let cli = Cli::try_parse_from(["cabinetos-cli", "themes", "show"]).unwrap();
        assert_eq!(
            cli.command,
            Command::Themes {
                action: ThemesAction::Show { id: None }
            }
        );
    }

    #[test]
    fn parses_market() {
        let parse = |args: &[&str]| {
            let mut all = vec!["cabinetos-cli", "market"];
            all.extend_from_slice(args);
            match Cli::try_parse_from(all).unwrap().command {
                Command::Market { action } => action,
                other => panic!("expected market, got {other:?}"),
            }
        };
        assert_eq!(parse(&["refresh"]), MarketAction::Refresh);
        assert_eq!(
            parse(&["search", "nord", "--kind", "theme"]),
            MarketAction::Search {
                query: "nord".to_owned(),
                kind: Some(KindArg::Theme)
            }
        );
        assert_eq!(
            parse(&["install", "hello", "--version", "0.1.0"]),
            MarketAction::Install {
                id: "hello".to_owned(),
                version: Some("0.1.0".to_owned())
            }
        );
        assert_eq!(
            parse(&["uninstall", "hello"]),
            MarketAction::Uninstall {
                id: "hello".to_owned()
            }
        );
        assert_eq!(parse(&["tools"]), MarketAction::Tools);
        assert!(Cli::try_parse_from(["cabinetos-cli", "market", "install"]).is_err());
    }

    #[test]
    fn parses_update() {
        let parse = |args: &[&str]| {
            let mut all = vec!["cabinetos-cli", "update"];
            all.extend_from_slice(args);
            Cli::try_parse_from(all).unwrap().command
        };
        assert_eq!(
            parse(&[]),
            Command::Update {
                action: None,
                json: false
            }
        );
        assert_eq!(
            parse(&["check", "--json"]),
            Command::Update {
                action: Some(UpdateAction::Check),
                json: true
            }
        );
        assert_eq!(
            parse(&["--json", "rollback"]),
            Command::Update {
                action: Some(UpdateAction::Rollback),
                json: true
            }
        );
        for (word, action) in [
            ("status", UpdateAction::Status),
            ("download", UpdateAction::Download),
            ("apply", UpdateAction::Apply),
            ("snooze", UpdateAction::Snooze),
        ] {
            assert_eq!(
                parse(&[word]),
                Command::Update {
                    action: Some(action),
                    json: false
                }
            );
        }
        assert!(Cli::try_parse_from(["cabinetos-cli", "update", "now"]).is_err());
    }

    #[test]
    fn rejects_zero_pings_and_a_missing_command() {
        assert!(Cli::try_parse_from(["cabinetos-cli", "ping", "--count", "0"]).is_err());
        assert!(Cli::try_parse_from(["cabinetos-cli"]).is_err());
    }

    #[test]
    fn the_reference_is_the_help_of_the_named_commands_under_the_short_name() {
        let text = reference(&["ls", "rename", "nonsense"]);
        assert!(text.contains("Usage: cab ls [OPTIONS] <PATH>"), "{text}");
        assert!(
            !text.contains("--pipe") && !text.contains("--log-dir"),
            "{text}"
        );
        assert!(
            text.contains("Usage: cab rename [OPTIONS] <PATH> <NEW_NAME>"),
            "{text}"
        );
        assert!(text.contains("--long"), "{text}");
        assert!(!text.contains("nonsense"));
        assert!(!text.contains('\u{1b}'), "no colour codes");
        assert!(command_names().contains(&"ls".to_owned()));
        assert!(command_names().contains(&"undo".to_owned()));
    }
}
