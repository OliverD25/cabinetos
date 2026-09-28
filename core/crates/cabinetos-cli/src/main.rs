//! `cabinetos-cli.exe`: a command-line client for the core's pipe. It lets the
//! core be tested with no UI: `ping`, `ls` (read from shared memory, as the
//! UI will), `volume`, `shutdown`, the configuration (`config`), the command
//! registry (`commands`), the keymap (`keys`), and jobs (`copy`, `move`,
//! `delete`, `jobs`, `job`).
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

mod jobs;
mod ls;
mod settings;

use std::io::Write;
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::{Duration, Instant};

use anyhow::{Context, anyhow, bail};
use cabinetos_diag::{Boundary, DiagConfig, span_for_request};
use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    ConflictPolicy, Envelope, JobAction, JobKind, JobOptions, JobRequest, Request, RequestId,
    Resolution, Response, SortKey, SortSpec, VolumeDetails,
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
    /// Show which volume and physical disk a path is on.
    Volume {
        /// Any path on the volume; it does not have to exist.
        path: String,
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
}

impl From<ResolveArg> for Resolution {
    fn from(argument: ResolveArg) -> Self {
        match argument {
            ResolveArg::Overwrite => Self::Overwrite,
            ResolveArg::Skip => Self::Skip,
            ResolveArg::Rename => Self::Rename { new_name: None },
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
}

impl From<SortArg> for SortKey {
    fn from(sort: SortArg) -> Self {
        match sort {
            SortArg::Name => Self::Name,
            SortArg::Size => Self::Size,
            SortArg::Modified => Self::Modified,
            SortArg::Kind => Self::Kind,
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
        Command::Ping { count } => {
            for _ in 0..*count {
                let started = Instant::now();
                let reply = send(&mut client, Request::Ping).await?;
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
        }
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
        Command::Volume { path } => {
            let reply = send(&mut client, Request::VolumeInfo { path: path.clone() }).await?;
            match reply.body {
                Response::VolumeInfo(details) => print_volume(&details),
                other => return Err(failure(path, &other)),
            }
        }
        Command::Config { .. } | Command::Commands { .. } | Command::Keys { .. } => {
            settings_command(&mut client, &cli.command).await?;
        }
        Command::Copy(_)
        | Command::Move(_)
        | Command::Delete { .. }
        | Command::Jobs
        | Command::Job { .. } => job_command(&mut client, &cli.command).await?,
    }
    Ok(())
}

/// The settings commands: `config`, `commands`, `keys`.
async fn settings_command(client: &mut PipeClient, command: &Command) -> anyhow::Result<()> {
    match command {
        Command::Config { action } => match action {
            ConfigAction::Path => settings::config_path(client).await?,
            ConfigAction::Show => settings::config_show(client).await?,
            ConfigAction::Validate { .. } => unreachable!("handled without a connection"),
        },
        Command::Commands { action } => match action {
            CommandsAction::List { json } => settings::commands_list(client, *json).await?,
            CommandsAction::Search { query } => {
                settings::commands_search(client, query).await?;
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

/// Prints one line to stdout. Returns `false` once stdout is closed (the
/// output was piped into `head`, say), so the caller can stop quietly
/// instead of panicking like `println!`.
pub(crate) fn say(line: std::fmt::Arguments<'_>) -> bool {
    let mut out = std::io::stdout().lock();
    writeln!(out, "{line}").and_then(|()| out.flush()).is_ok()
}

/// `1999433101312` → `1.82 TiB`.
#[expect(clippy::cast_precision_loss, reason = "a size rounded for display")]
fn binary_size(bytes: u64) -> String {
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
