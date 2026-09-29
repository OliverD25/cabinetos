//! `cabinetos-cli.exe`: a command-line client for the core's pipe. It lets the
//! core be tested with no UI: `ping`, `ls` (read from shared memory, as the
//! UI will), `describe` and `icon` (the shell's type names and icons),
//! `volume` and `volumes`, `open`, `edit`, `props`, `mkdir`, `mkfile` and `rename`,
//! folder sizes (`measure`), names by pattern (`match`), `shutdown`, the
//! configuration (`config`), the command registry (`commands`), the keymap
//! (`keys`), jobs (`copy`, `move`, `delete`, `jobs`, `job`), the Core
//! Plugins (`plugins`), the events the core sends (`events watch`), file
//! search (`search`, `index status`), the terminal sessions (`term`), the
//! colour themes (`themes`), and the marketplace (`market`).
//!
//! It stands in for the UI, so its diagnostics use the `frontend` boundary.
//! Without `--log-dir` it writes no log file and reports only to stderr;
//! with `--log-dir` it writes `cli.<date>.jsonl`, where each request ID can be
//! matched with the same ID in the core's log.
//!
//! Serves Constitution Article 4 (Progressive Disclosure: programmable
//! interfaces for power users) and Article 12 (one request ID traced from the
//! client through the pipe into the core).
#![forbid(unsafe_code)]

mod describe;
mod jobs;
mod ls;
mod market;
mod measure;
mod patterns;
mod plugins;
mod search;
mod settings;
mod term;
mod themes;

use std::io::Write;
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::{Duration, Instant};

use anyhow::{Context, anyhow, bail};
use cabinetos_diag::{Boundary, DiagConfig, span_for_request};
use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    ConflictPolicy, Envelope, ExtensionKind, JobAction, JobKind, JobOptions, JobRequest, Request,
    RequestId, Resolution, Response, SortKey, SortSpec, VolumeDetails,
};
use clap::{Args, Parser, Subcommand, ValueEnum};
use tracing::Instrument;

/// How long to wait for a busy pipe.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(5);
/// How long to wait for one reply. Listing a huge directory on a slow network
/// share may take a while.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(60);

/// Talks to a running cabinetos-core over its named pipe.
#[derive(Debug, Parser)]
#[command(name = "cabinetos-cli", version)]
struct Cli {
    /// Pipe token of the core: \\.\pipe\cabinetos-core-<TOKEN>.
    #[arg(long, global = true, value_name = "TOKEN", default_value = "dev")]
    pipe: String,

    /// Also write this client's log (cli.<date>.jsonl) into PATH.
    #[arg(long, global = true, value_name = "PATH")]
    log_dir: Option<PathBuf>,

    #[command(subcommand)]
    command: Command,
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
enum Command {
    /// Send ping requests and print each reply with its round-trip time.
    Ping {
        /// How many pings to send, one after another.
        #[arg(long, default_value_t = 1, value_parser = clap::value_parser!(u32).range(1..))]
        count: u32,
    },
    /// Ask the core to exit cleanly.
    Shutdown,
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
    /// else a walk of one folder tree (at most 2 s and 20,000 entries).
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
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
enum MarketAction {
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
enum KindArg {
    Plugin,
    Theme,
    Tool,
}

impl From<KindArg> for ExtensionKind {
    fn from(kind: KindArg) -> Self {
        match kind {
            KindArg::Plugin => Self::Plugin,
            KindArg::Theme => Self::Theme,
            KindArg::Tool => Self::Tool,
        }
    }
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
enum ThemesAction {
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
struct TermArgs {
    #[command(subcommand)]
    action: Option<TermAction>,
    /// A profile from terminal.profiles; without it, terminal.defaultProfile.
    #[arg(long, value_name = "NAME")]
    profile: Option<String>,
    /// The folder the shell starts in; without it, this folder.
    #[arg(long, value_name = "PATH")]
    cwd: Option<String>,
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
enum TermAction {
    /// Print every session: its ID, profile, process ID, size, state,
    /// whether a client is attached, and its folder.
    List,
    /// Close a session: its shell gets a hang-up.
    Close {
        /// The session's ID.
        id: u64,
    },
    /// Type the shell's own change-directory command for PATH into a
    /// session, as the pane does when it changes folder.
    Cd {
        /// The session's ID.
        id: u64,
        /// The folder.
        path: String,
    },
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
enum IndexAction {
    /// Print whether an indexer answers, and each volume's state.
    Status,
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
enum PluginsAction {
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
enum EventsAction {
    /// Print each event as one JSON line, until Ctrl+C.
    Watch,
}

/// The paths and options of `copy` and `move`.
#[derive(Debug, PartialEq, Eq, Args)]
struct TransferArgs {
    /// The files and folders, then the folder they go into (created if it
    /// does not exist).
    #[arg(required = true, num_args = 2.., value_name = "PATH")]
    paths: Vec<String>,
    /// What to do when a file already exists at the destination.
    #[arg(long, value_enum, default_value = "ask")]
    on_conflict: OnConflictArg,
    /// Compare each copy with its source (sizes and sampled bytes).
    #[arg(long)]
    verify: bool,
    /// Answer every conflict this way instead of waiting for `job resolve`.
    #[arg(long, value_enum)]
    resolve: Option<ResolveArg>,
    /// At the end, print how many progress events arrived per second.
    #[arg(long)]
    stats: bool,
}

/// `--on-conflict`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
enum OnConflictArg {
    /// Set the file aside and report a conflict.
    Ask,
    Overwrite,
    Skip,
    /// Give the new file a free name: `name (2).ext`.
    Rename,
    /// Overwrite only when the source is newer.
    Newer,
}

impl From<OnConflictArg> for ConflictPolicy {
    fn from(argument: OnConflictArg) -> Self {
        match argument {
            OnConflictArg::Ask => Self::Ask,
            OnConflictArg::Overwrite => Self::Overwrite,
            OnConflictArg::Skip => Self::Skip,
            OnConflictArg::Rename => Self::Rename,
            OnConflictArg::Newer => Self::OverwriteIfNewer,
        }
    }
}

/// `--resolve`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
enum ResolveArg {
    Overwrite,
    Skip,
    Rename,
    /// For a delete: what the Recycle Bin cannot take is deleted for good.
    DeletePermanently,
}

impl From<ResolveArg> for Resolution {
    fn from(argument: ResolveArg) -> Self {
        match argument {
            ResolveArg::Overwrite => Self::Overwrite,
            ResolveArg::Skip => Self::Skip,
            ResolveArg::Rename => Self::Rename { new_name: None },
            ResolveArg::DeletePermanently => Self::DeletePermanently,
        }
    }
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
enum JobCommand {
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
enum ResolutionArg {
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

impl From<ResolutionArg> for Resolution {
    fn from(argument: ResolutionArg) -> Self {
        match argument {
            ResolutionArg::Overwrite => Self::Overwrite,
            ResolutionArg::Skip => Self::Skip,
            ResolutionArg::Rename => Self::Rename { new_name: None },
            ResolutionArg::Retry => Self::Retry,
            ResolutionArg::DeletePermanently => Self::DeletePermanently,
            ResolutionArg::Cancel => Self::CancelJob,
        }
    }
}

/// `path` made absolute against this process's folder: the core has its own.
fn absolute(path: &str) -> anyhow::Result<String> {
    std::path::absolute(path)
        .map(|path| path.display().to_string())
        .with_context(|| format!("{path}: not a valid path"))
}

/// The job of a `copy` or `move` command.
fn transfer_job(kind: JobKind, arguments: &TransferArgs) -> anyhow::Result<jobs::JobRun> {
    let (destination, sources) = arguments
        .paths
        .split_last()
        .context("a source and a destination are needed")?;
    Ok(jobs::JobRun {
        request: JobRequest {
            kind,
            sources: sources
                .iter()
                .map(|source| absolute(source))
                .collect::<anyhow::Result<_>>()?,
            destination: Some(absolute(destination)?),
            options: JobOptions {
                on_conflict: arguments.on_conflict.into(),
                verify: arguments.verify,
                ..JobOptions::default()
            },
        },
        resolve: arguments.resolve.map(Resolution::from),
        stats: arguments.stats,
    })
}

#[derive(Debug, PartialEq, Eq, Subcommand)]
enum ConfigAction {
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
enum CommandsAction {
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
enum KeysAction {
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
enum SortArg {
    Name,
    Size,
    Modified,
    Kind,
    Extension,
}

impl From<SortArg> for SortKey {
    fn from(sort: SortArg) -> Self {
        match sort {
            SortArg::Name => Self::Name,
            SortArg::Size => Self::Size,
            SortArg::Modified => Self::Modified,
            SortArg::Kind => Self::Kind,
            SortArg::Extension => Self::Extension,
        }
    }
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    let _diag = match cabinetos_diag::init(DiagConfig {
        process: "cli",
        boundary: Boundary::Frontend,
        dir: cli.log_dir.clone(),
        log_file: cli.log_dir.is_some(),
    }) {
        Ok(guard) => Some(guard),
        Err(error) => {
            eprintln!("cabinetos-cli: running without diagnostics: {error}");
            None
        }
    };

    let result = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .context("cannot start the async runtime")
        .and_then(|runtime| runtime.block_on(execute(&cli)));
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            // Without a log file the stderr layer would print this a second
            // time, in the verbose log format.
            if cli.log_dir.is_some() {
                tracing::error!(error = format!("{error:#}"), "command failed");
            }
            eprintln!("cabinetos-cli: {error:#}");
            ExitCode::FAILURE
        }
    }
}

async fn execute(cli: &Cli) -> anyhow::Result<()> {
    if let Command::Config {
        action: ConfigAction::Validate { file },
    } = &cli.command
    {
        return settings::config_validate(file.as_deref());
    }
    let pipe = PipeName::new(&cli.pipe);
    let mut client = PipeClient::connect(&pipe, CONNECT_TIMEOUT)
        .await
        .with_context(|| {
            format!(
                "cannot connect to {pipe}; is cabinetos-core running with --pipe {}?",
                pipe.token()
            )
        })?;

    match &cli.command {
        Command::Ping { count } => ping(&mut client, *count).await?,
        Command::Shutdown => {
            let reply = send(&mut client, Request::Shutdown).await?;
            if reply.body != Response::Ok {
                bail!("expected ok, got {:?} (id={})", reply.body, reply.id);
            }
            say(format_args!("shutdown acknowledged id={}", reply.id));
        }
        Command::Ls {
            path,
            long,
            hidden,
            sort,
            desc,
            watch,
        } => {
            let args = ls::LsArgs {
                path: path.clone(),
                long: *long,
                // Absent options let the core use its configured defaults.
                include_hidden: hidden.then_some(true),
                sort: (sort.is_some() || *desc).then(|| SortSpec {
                    key: sort.unwrap_or(SortArg::Name).into(),
                    descending: *desc,
                }),
                watch: *watch,
            };
            ls::ls(&mut client, args).await?;
        }
        Command::Volume { .. } | Command::Volumes => {
            volume_command(&mut client, &cli.command).await?;
        }
        Command::Props { .. } | Command::Measure { .. } | Command::Match { .. } => {
            commander_command(&mut client, &cli.command).await?;
        }
        Command::Open { .. }
        | Command::Edit { .. }
        | Command::Mkdir { .. }
        | Command::Mkfile { .. }
        | Command::Rename { .. } => {
            file_command(&mut client, &cli.command).await?;
        }
        Command::Describe { path, from, count } => {
            describe::describe(&mut client, &absolute(path)?, *from, *count).await?;
        }
        Command::Icon { key, size, out } => describe::icon(&mut client, key, *size, out).await?,
        Command::Config { .. } | Command::Commands { .. } | Command::Keys { .. } => {
            settings_command(&mut client, &cli.command).await?;
        }
        Command::Copy(_)
        | Command::Move(_)
        | Command::Delete { .. }
        | Command::Jobs
        | Command::Job { .. } => job_command(&mut client, &cli.command).await?,
        Command::Plugins { action } => plugins_command(&mut client, action).await?,
        Command::Events {
            action: EventsAction::Watch,
        } => plugins::watch(&mut client).await?,
        Command::Search { query, limit, root } => {
            let root = root.as_deref().map(absolute).transpose()?;
            search::search(&mut client, query, *limit, root.as_deref()).await?;
        }
        Command::Index {
            action: IndexAction::Status,
        } => search::status(&mut client).await?,
        Command::Term(arguments) => term_command(&mut client, arguments).await?,
        Command::Themes { .. } | Command::Market { .. } => {
            extension_command(&mut client, &cli.command).await?;
        }
    }
    Ok(())
}

/// `ping`: `count` pings, one after another, each printed with its round
/// trip.
async fn ping(client: &mut PipeClient, count: u32) -> anyhow::Result<()> {
    for _ in 0..count {
        let started = Instant::now();
        let reply = send(client, Request::Ping).await?;
        let rtt = started.elapsed();
        let Response::Pong {
            protocol_version,
            core_version,
        } = reply.body
        else {
            bail!("expected pong, got {:?} (id={})", reply.body, reply.id);
        };
        if !say(format_args!(
            "pong id={} protocol={protocol_version} core={core_version} rtt={:.2}ms",
            reply.id,
            rtt.as_secs_f64() * 1000.0
        )) {
            break;
        }
    }
    Ok(())
}

/// The volume commands: `volume` and `volumes`.
async fn volume_command(client: &mut PipeClient, command: &Command) -> anyhow::Result<()> {
    match command {
        Command::Volume { path } => {
            let reply = send(client, Request::VolumeInfo { path: path.clone() }).await?;
            match reply.body {
                Response::VolumeInfo(details) => print_volume(&details),
                other => return Err(failure(path, &other)),
            }
        }
        Command::Volumes => {
            let reply = send(client, Request::ListVolumes).await?;
            match reply.body {
                Response::Volumes { volumes } => print_volumes(&volumes),
                other => return Err(failure("list_volumes", &other)),
            }
        }
        _ => unreachable!("only volume commands come here"),
    }
    Ok(())
}

/// The file commands: `open`, `edit`, `mkdir`, `mkfile`, `rename`.
async fn file_command(client: &mut PipeClient, command: &Command) -> anyhow::Result<()> {
    let (path, request) = match command {
        Command::Open { path } => {
            let path = absolute(path)?;
            (path.clone(), Request::OpenPath { path })
        }
        Command::Edit { path } => {
            let path = absolute(path)?;
            (path.clone(), Request::EditPath { path })
        }
        Command::Mkdir { path } => {
            let path = absolute(path)?;
            (path.clone(), Request::CreateDirectory { path })
        }
        Command::Mkfile { path } => {
            let path = absolute(path)?;
            (path.clone(), Request::CreateFile { path })
        }
        Command::Rename { path, new_name } => {
            let path = absolute(path)?;
            let request = Request::Rename {
                path: path.clone(),
                new_name: new_name.clone(),
            };
            (path, request)
        }
        _ => unreachable!("only file commands come here"),
    };
    let reply = send(client, request).await?;
    if reply.body != Response::Ok {
        return Err(failure(&path, &reply.body));
    }
    match command {
        Command::Open { .. } => say(format_args!("opened {path}")),
        Command::Edit { .. } => say(format_args!("opened {path} for editing")),
        Command::Mkdir { .. } | Command::Mkfile { .. } => say(format_args!("created {path}")),
        Command::Rename { new_name, .. } => say(format_args!("renamed {path} to {new_name}")),
        _ => true,
    };
    Ok(())
}

/// Total Commander's commands of sub-phase 11a that are more than one
/// request: `props`, `measure`, `match`.
async fn commander_command(client: &mut PipeClient, command: &Command) -> anyhow::Result<()> {
    match command {
        Command::Props { paths } => props(client, paths).await,
        Command::Measure { paths } => {
            let paths = paths
                .iter()
                .map(|path| absolute(path))
                .collect::<anyhow::Result<_>>()?;
            measure::measure(client, paths).await
        }
        Command::Match {
            path,
            patterns,
            files_only,
            first_from,
        } => {
            let search = patterns::Search {
                patterns: patterns.clone(),
                files_only: *files_only,
                first_from: *first_from,
            };
            patterns::run(client, &absolute(path)?, &search).await
        }
        _ => unreachable!("only the commands of sub-phase 11a come here"),
    }
}

/// `props`: Windows' property sheet of `paths`, shown by the core.
async fn props(client: &mut PipeClient, paths: &[String]) -> anyhow::Result<()> {
    let paths: Vec<String> = paths
        .iter()
        .map(|path| absolute(path))
        .collect::<anyhow::Result<_>>()?;
    let subject = match paths.as_slice() {
        [path] => path.clone(),
        _ => format!("{} items", paths.len()),
    };
    let reply = send(client, Request::ShowProperties { paths }).await?;
    if reply.body != Response::Ok {
        return Err(failure(&subject, &reply.body));
    }
    say(format_args!("showing the properties of {subject}"));
    Ok(())
}

/// The terminal commands: `term`, `term list|close|cd`.
async fn term_command(client: &mut PipeClient, arguments: &TermArgs) -> anyhow::Result<()> {
    match &arguments.action {
        None => {
            let cwd = absolute(arguments.cwd.as_deref().unwrap_or("."))?;
            term::run(client, arguments.profile.clone(), cwd).await
        }
        Some(TermAction::List) => term::list(client).await,
        Some(TermAction::Close { id }) => {
            let request = Request::TerminalClose { session_id: *id };
            term::change(client, request, format_args!("session {id} closed")).await
        }
        Some(TermAction::Cd { id, path }) => {
            let path = absolute(path)?;
            let request = Request::TerminalSyncCwd {
                session_id: *id,
                path: path.clone(),
            };
            term::change(client, request, format_args!("session {id}: cd {path}")).await
        }
    }
}

/// The extension commands: `themes list|show`, `market
/// refresh|search|install|uninstall|tools`.
async fn extension_command(client: &mut PipeClient, command: &Command) -> anyhow::Result<()> {
    match command {
        Command::Themes { action } => match action {
            ThemesAction::List => themes::list(client).await,
            ThemesAction::Show { id } => themes::show(client, id.as_deref()).await,
        },
        Command::Market { action } => match action {
            MarketAction::Refresh => market::list(client, Request::MarketplaceRefresh).await,
            MarketAction::Search { query, kind } => {
                let request = Request::MarketplaceSearch {
                    query: query.clone(),
                    kind: kind.map(ExtensionKind::from),
                };
                market::list(client, request).await
            }
            MarketAction::Install { id, version } => {
                market::install(client, id, version.as_deref()).await
            }
            MarketAction::Uninstall { id } => market::uninstall(client, id).await,
            MarketAction::Tools => market::tools(client).await,
        },
        _ => unreachable!("only extension commands come here"),
    }
}

/// The plugin commands: `plugins list|reload|enable|disable|grant`.
async fn plugins_command(client: &mut PipeClient, action: &PluginsAction) -> anyhow::Result<()> {
    match action {
        PluginsAction::List { json } => plugins::list(client, *json).await,
        PluginsAction::Reload { id } => {
            let request = Request::ReloadPlugin {
                plugin_id: id.clone(),
            };
            plugins::change(client, id, request).await
        }
        PluginsAction::Enable { id } | PluginsAction::Disable { id } => {
            let request = Request::SetPluginEnabled {
                plugin_id: id.clone(),
                enabled: matches!(action, PluginsAction::Enable { .. }),
            };
            plugins::change(client, id, request).await
        }
        PluginsAction::Grant { id, capabilities } => {
            let request = Request::GrantCapabilities {
                plugin_id: id.clone(),
                capabilities: capabilities.clone(),
            };
            plugins::change(client, id, request).await
        }
    }
}

/// The settings commands: `config`, `commands`, `keys`.
async fn settings_command(client: &mut PipeClient, command: &Command) -> anyhow::Result<()> {
    match command {
        Command::Config { action } => match action {
            ConfigAction::Path => settings::config_path(client).await?,
            ConfigAction::Show => settings::config_show(client).await?,
            ConfigAction::Get { path } => settings::config_get(client, path).await?,
            ConfigAction::Set { path, value } => settings::config_set(client, path, value).await?,
            ConfigAction::Validate { .. } => unreachable!("handled without a connection"),
        },
        Command::Commands { action } => match action {
            CommandsAction::List { json } => settings::commands_list(client, *json).await?,
            CommandsAction::Search { query } => {
                settings::commands_search(client, query).await?;
            }
            CommandsAction::Exec { command, args } => {
                plugins::exec(client, command, args.as_deref()).await?;
            }
        },
        Command::Keys { action } => match action {
            KeysAction::List => settings::keys_list(client).await?,
            KeysAction::Set { command, keys } => {
                settings::keys_change(client, command, Some(keys)).await?;
            }
            KeysAction::Reset { command } => {
                settings::keys_change(client, command, None).await?;
            }
            KeysAction::Watch => settings::keys_watch(client).await?,
        },
        _ => unreachable!("only settings commands come here"),
    }
    Ok(())
}

/// The job commands: `copy`, `move`, `delete`, `jobs`, `job`.
async fn job_command(client: &mut PipeClient, command: &Command) -> anyhow::Result<()> {
    match command {
        Command::Copy(arguments) => {
            jobs::run(client, transfer_job(JobKind::Copy, arguments)?).await?;
        }
        Command::Move(arguments) => {
            jobs::run(client, transfer_job(JobKind::Move, arguments)?).await?;
        }
        Command::Delete {
            paths,
            permanent,
            resolve,
            stats,
        } => {
            let job = jobs::JobRun {
                request: JobRequest {
                    kind: JobKind::Delete {
                        permanent: *permanent,
                    },
                    sources: paths
                        .iter()
                        .map(|path| absolute(path))
                        .collect::<anyhow::Result<_>>()?,
                    destination: None,
                    options: JobOptions::default(),
                },
                resolve: resolve.map(Resolution::from),
                stats: *stats,
            };
            jobs::run(client, job).await?;
        }
        Command::Jobs => jobs::list(client).await?,
        Command::Job { action } => match action {
            JobCommand::Pause { id } => jobs::control(client, *id, JobAction::Pause).await?,
            JobCommand::Resume { id } => jobs::control(client, *id, JobAction::Resume).await?,
            JobCommand::Cancel { id } => jobs::control(client, *id, JobAction::Cancel).await?,
            JobCommand::Resolve {
                job,
                conflict,
                resolution,
            } => jobs::resolve(client, *job, *conflict, (*resolution).into()).await?,
        },
        _ => unreachable!("only job commands come here"),
    }
    Ok(())
}

/// Sends `hello` and checks for `welcome`.
pub(crate) async fn expect_welcome(client: &mut PipeClient) -> anyhow::Result<()> {
    let reply = send(
        client,
        Request::Hello {
            client_pid: std::process::id(),
            client_name: "cabinetos-cli".to_owned(),
        },
    )
    .await?;
    match reply.body {
        Response::Welcome { .. } => Ok(()),
        other => Err(failure("hello", &other)),
    }
}

/// An error for a reply that is not what the request expects.
pub(crate) fn failure(subject: &str, reply: &Response) -> anyhow::Error {
    match reply {
        Response::Error { code, message } => {
            let code = serde_json::to_value(code)
                .ok()
                .and_then(|value| value.as_str().map(str::to_owned))
                .unwrap_or_default();
            if message.starts_with(subject) {
                anyhow!("{message} ({code})")
            } else {
                anyhow!("{subject}: {message} ({code})")
            }
        }
        other => anyhow!("{subject}: unexpected reply {}", other.type_tag()),
    }
}

/// The volume fields, one per line.
fn print_volume(details: &VolumeDetails) {
    let unknown = || "unknown".to_owned();
    let mut lines = vec![
        format!(
            "drive_letter: {}",
            details.drive_letter.map_or_else(unknown, String::from)
        ),
        format!("volume_guid_path: {}", details.volume_guid_path),
        format!("filesystem: {}", details.filesystem),
        format!("label: {}", details.label),
        format!(
            "total_bytes: {} ({})",
            details.total_bytes,
            binary_size(details.total_bytes)
        ),
        format!(
            "free_bytes: {} ({})",
            details.free_bytes,
            binary_size(details.free_bytes)
        ),
    ];
    match &details.disk {
        Some(disk) => lines.extend([
            format!("disk.device_number: {}", disk.device_number),
            format!("disk.bus_type: {}", disk.bus_type),
            format!(
                "disk.seek_penalty: {}",
                disk.seek_penalty
                    .map_or_else(unknown, |slow| slow.to_string())
            ),
            format!(
                "disk.media_type: {}",
                disk.media_type.clone().unwrap_or_else(unknown)
            ),
        ]),
        None => lines.push("disk: unknown".to_owned()),
    }
    for line in lines {
        if !say(format_args!("{line}")) {
            break;
        }
    }
}

/// One line per volume, under a header.
fn print_volumes(volumes: &[VolumeDetails]) {
    if volumes.is_empty() {
        say(format_args!("no volume answered"));
        return;
    }
    let mut lines = vec![format!(
        "{:<6} {:<10} {:>11} {:>11}  {:<7} {:<6} {:<5} label",
        "drive", "filesystem", "size", "free", "bus", "media", "disk"
    )];
    for volume in volumes {
        let unknown = || "?".to_owned();
        let letter = volume
            .drive_letter
            .map_or_else(unknown, |letter| format!("{letter}:"));
        let (bus, media, disk) = volume.disk.as_ref().map_or_else(
            || ("-".to_owned(), "-".to_owned(), "-".to_owned()),
            |disk| {
                (
                    disk.bus_type.clone(),
                    disk.media_type.clone().unwrap_or_else(unknown),
                    disk.device_number.to_string(),
                )
            },
        );
        lines.push(format!(
            "{letter:<6} {:<10} {:>11} {:>11}  {bus:<7} {media:<6} {disk:<5} {}",
            volume.filesystem,
            binary_size(volume.total_bytes),
            binary_size(volume.free_bytes),
            volume.label
        ));
    }
    for line in lines {
        if !say(format_args!("{}", line.trim_end())) {
            break;
        }
    }
}

/// Prints one line to stdout. Returns `false` once stdout is closed (the
/// output was piped into `head`, say), so the caller can stop quietly
/// instead of panicking like `println!`.
pub(crate) fn say(line: std::fmt::Arguments<'_>) -> bool {
    let mut out = std::io::stdout().lock();
    writeln!(out, "{line}").and_then(|()| out.flush()).is_ok()
}

/// `1999433101312` → `1.82 TiB`.
#[expect(clippy::cast_precision_loss, reason = "a size rounded for display")]
pub(crate) fn binary_size(bytes: u64) -> String {
    let units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
    let mut value = bytes as f64;
    let mut unit = 0;
    while value >= 1024.0 && unit < units.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    format!("{value:.2} {}", units[unit])
}

/// Sends one request inside its request span and waits for the reply with its
/// ID.
pub(crate) async fn send(
    client: &mut PipeClient,
    request: Request,
) -> anyhow::Result<Envelope<Response>> {
    let id = RequestId::new();
    let kind = request.type_tag();
    let exchange = async {
        tracing::debug!(request = kind, "sending request");
        let started = Instant::now();
        let reply =
            tokio::time::timeout(REQUEST_TIMEOUT, client.request_with_id(id.clone(), request))
                .await
                .with_context(|| {
                    format!("no reply to {kind} within {REQUEST_TIMEOUT:?} (id={id})")
                })?
                .with_context(|| format!("{kind} failed (id={id})"))?;
        tracing::info!(
            request = kind,
            rtt_us = u64::try_from(started.elapsed().as_micros()).unwrap_or(u64::MAX),
            "reply received"
        );
        Ok(reply)
    };
    exchange.instrument(span_for_request(&id)).await
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
        let job = transfer_job(JobKind::Copy, &copy).unwrap();
        assert_eq!(job.request.destination.as_deref(), Some(r"E:\dst"));
        assert_eq!(job.request.sources.len(), 2);
        assert!(
            job.request
                .sources
                .iter()
                .all(|source| std::path::Path::new(source).is_absolute())
        );
        assert_eq!(
            job.request.options.on_conflict,
            ConflictPolicy::OverwriteIfNewer
        );
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
            })
        };
        assert_eq!(parse(&["term"]).unwrap(), term(None, None, None));
        assert_eq!(
            parse(&["term", "--profile", "pwsh", "--cwd", r"E:\"]).unwrap(),
            term(None, Some("pwsh"), Some(r"E:\"))
        );
        assert_eq!(
            parse(&["term", "list"]).unwrap(),
            term(Some(TermAction::List), None, None)
        );
        assert_eq!(
            parse(&["term", "close", "3"]).unwrap(),
            term(Some(TermAction::Close { id: 3 }), None, None)
        );
        assert_eq!(
            parse(&["term", "cd", "3", r"D:\docs"]).unwrap(),
            term(
                Some(TermAction::Cd {
                    id: 3,
                    path: r"D:\docs".to_owned()
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
        assert!(parse(&["term", "cd", "3"]).is_err(), "a path is required");
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
    fn formats_binary_sizes() {
        assert_eq!(binary_size(512), "512.00 B");
        assert_eq!(binary_size(1536), "1.50 KiB");
        assert_eq!(binary_size(1_999_433_101_312), "1.82 TiB");
    }

    #[test]
    fn rejects_zero_pings_and_a_missing_command() {
        assert!(Cli::try_parse_from(["cabinetos-cli", "ping", "--count", "0"]).is_err());
        assert!(Cli::try_parse_from(["cabinetos-cli"]).is_err());
    }
}
