//! `cabinetos-cli.exe`: a command-line client for the core's pipe. It lets the
//! core be tested with no UI: `ping`, `ls` (read from shared memory, as the
//! UI will), `describe` and `icon` (the shell's type names and icons),
//! `volume` and `volumes`, `open`, `edit`, `props`, `mkdir`, `mkfile` and `rename`,
//! folder sizes (`measure`), names by pattern (`match`), `shutdown`, the
//! configuration (`config`), the command registry (`commands`), the keymap
//! (`keys`), jobs (`copy`, `move`, `delete`, `jobs`, `job`), the Core
//! Plugins (`plugins`), the events the core sends (`events watch`), file
//! search (`search`, `index status`), the terminal sessions (`term`; `term
//! cwd` is what a shell's prompt hook runs at each prompt), the
//! colour themes (`themes`), the marketplace (`market`), what the window
//! shows (`state`), secrets in the Credential Manager (`secret`), in-app
//! updates (`update`), the live GUI context (`pane`, `selection`, and `copy`
//! or `move` with `--selection`: what a CabinetOS window shows, from a
//! shell in it), and the log folder (`log trace`, `log tail`: these read
//! files and need no core; `log bundle` asks the core for a zip).
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
mod gui;
mod jobs;
mod logs;
mod ls;
mod market;
mod measure;
mod patterns;
mod plugins;
mod search;
mod secret;
mod settings;
mod state;
mod term;
mod themes;
mod undo;
mod update;

use std::io::Write;
use std::process::ExitCode;
use std::time::{Duration, Instant};

use anyhow::{Context, anyhow, bail};
use cabinetos_cli_args::{
    Cli, Command, CommandsAction, ConfigAction, EventsAction, IndexAction, JobCommand, KeysAction,
    KindArg, LogAction, MarketAction, OnConflictArg, PIPE_ENV, PluginsAction, ResolutionArg,
    ResolveArg, SecretAction, SortArg, TermAction, TermArgs, ThemesAction, TransferArgs,
};
use cabinetos_diag::{Boundary, DiagConfig, span_for_action};
use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    Catalogue, ConflictPolicy, Envelope, ExtensionKind, JobAction, JobKind, JobOptions, JobRequest,
    Request, RequestId, Resolution, Response, SortKey, SortSpec, VolumeDetails,
};
use clap::Parser;
use tracing::Instrument;

/// How long to wait for a busy pipe.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(5);
/// How long `term cwd` waits for the pipe and then for the reply: a
/// shell's prompt waits for it. The GUI context commands wait this long for
/// the pipe too: typed in a shell, they fail fast when no core answers.
const HOOK_TIMEOUT: Duration = Duration::from_secs(1);
/// How long to wait for one reply. Listing a huge directory on a slow network
/// share may take a while.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(60);

/// `path` made absolute against this process's folder: the core has its own.
fn absolute(path: &str) -> anyhow::Result<String> {
    std::path::absolute(path)
        .map(|path| path.display().to_string())
        .with_context(|| format!("{path}: not a valid path"))
}

/// The options of a `copy` or `move` job.
fn transfer_options(arguments: &TransferArgs) -> JobOptions {
    JobOptions {
        on_conflict: conflict_policy(arguments.on_conflict),
        verify: arguments.verify,
        ..JobOptions::default()
    }
}

/// The job of a `copy` or `move` command with paths.
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
            options: transfer_options(arguments),
        },
        resolve: arguments.resolve.map(resolution_of_resolve),
        stats: arguments.stats,
    })
}

fn extension_kind(kind: KindArg) -> ExtensionKind {
    match kind {
        KindArg::Plugin => ExtensionKind::Plugin,
        KindArg::Theme => ExtensionKind::Theme,
        KindArg::Tool => ExtensionKind::Tool,
    }
}

fn conflict_policy(argument: OnConflictArg) -> ConflictPolicy {
    match argument {
        OnConflictArg::Ask => ConflictPolicy::Ask,
        OnConflictArg::Overwrite => ConflictPolicy::Overwrite,
        OnConflictArg::Skip => ConflictPolicy::Skip,
        OnConflictArg::Rename => ConflictPolicy::Rename,
        OnConflictArg::Newer => ConflictPolicy::OverwriteIfNewer,
    }
}

fn resolution_of_resolve(argument: ResolveArg) -> Resolution {
    match argument {
        ResolveArg::Overwrite => Resolution::Overwrite,
        ResolveArg::Skip => Resolution::Skip,
        ResolveArg::Rename => Resolution::Rename { new_name: None },
        ResolveArg::DeletePermanently => Resolution::DeletePermanently,
    }
}

fn resolution_of(argument: ResolutionArg) -> Resolution {
    match argument {
        ResolutionArg::Overwrite => Resolution::Overwrite,
        ResolutionArg::Skip => Resolution::Skip,
        ResolutionArg::Rename => Resolution::Rename { new_name: None },
        ResolutionArg::Retry => Resolution::Retry,
        ResolutionArg::DeletePermanently => Resolution::DeletePermanently,
        ResolutionArg::Cancel => Resolution::CancelJob,
    }
}

fn sort_key(sort: SortArg) -> SortKey {
    match sort {
        SortArg::Name => SortKey::Name,
        SortArg::Size => SortKey::Size,
        SortArg::Modified => SortKey::Modified,
        SortArg::Kind => SortKey::Kind,
        SortArg::Extension => SortKey::Extension,
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
            if error.is::<gui::NothingToUse>() {
                ExitCode::from(gui::NOTHING_TO_USE_EXIT)
            } else {
                ExitCode::FAILURE
            }
        }
    }
}

/// The commands that need no core: `config validate`, `log trace` and
/// `log tail`. `None` for every other command.
fn without_core(command: &Command) -> Option<anyhow::Result<()>> {
    match command {
        Command::Config {
            action: ConfigAction::Validate { file },
        } => Some(settings::config_validate(file.as_deref())),
        Command::Log { action } if !matches!(action, LogAction::Bundle { .. }) => {
            Some(log_command(action))
        }
        _ => None,
    }
}

/// The session `term cwd` asks about, found before connecting: without
/// one there is nothing to ask. `None` for every other command.
fn hook_session(command: &Command) -> anyhow::Result<Option<u64>> {
    match command {
        Command::Term(TermArgs {
            action: Some(TermAction::Cwd { session }),
            ..
        }) => term::session_or_env(*session).map(Some),
        _ => Ok(None),
    }
}

/// The commands that answer a person at a shell and must fail fast when no
/// core is there: the GUI context commands.
fn gui_context_command(command: &Command) -> bool {
    match command {
        Command::Pane { .. } | Command::Selection { .. } => true,
        Command::Copy(arguments) | Command::Move(arguments) => arguments.selection,
        _ => false,
    }
}

/// How long to wait for the pipe: a shell is waiting for `term cwd` and for
/// the GUI context commands.
fn connect_wait(command: &Command, hook_session: Option<u64>) -> Duration {
    if hook_session.is_some() || gui_context_command(command) {
        HOOK_TIMEOUT
    } else {
        CONNECT_TIMEOUT
    }
}

/// Connects to the core's pipe and starts the run's trace.
async fn connect(cli: &Cli, hook_session: Option<u64>) -> anyhow::Result<PipeClient> {
    let pipe = PipeName::new(&cli.pipe_token(std::env::var(PIPE_ENV).ok()));
    let wait = connect_wait(&cli.command, hook_session);
    let mut client = PipeClient::connect(&pipe, wait).await.with_context(|| {
        format!(
            "cannot connect to {pipe}; is cabinetos-core running with --pipe {}?",
            pipe.token()
        )
    })?;
    // One trace per run: every request of this run is one action.
    client.set_trace(Some(RequestId::new()));
    Ok(client)
}

async fn execute(cli: &Cli) -> anyhow::Result<()> {
    if let Some(result) = without_core(&cli.command) {
        return result;
    }
    let hook_session = hook_session(&cli.command)?;
    let mut client = connect(cli, hook_session).await?;

    match &cli.command {
        Command::Ping { count } => ping(&mut client, *count).await?,
        Command::Shutdown => {
            let reply = send(&mut client, Request::Shutdown).await?;
            if reply.body != Response::Ok {
                bail!("expected ok, got {:?} (id={})", reply.body, reply.id);
            }
            say(format_args!("shutdown acknowledged id={}", reply.id));
        }
        Command::Log { action } => logs::bundle(&mut client, action.minutes()).await?,
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
                    key: sort_key(sort.unwrap_or(SortArg::Name)),
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
        Command::Term(_) if let Some(session_id) = hook_session => {
            term::cwd(&mut client, session_id, HOOK_TIMEOUT).await?;
        }
        Command::Term(arguments) => term_command(&mut client, arguments).await?,
        Command::Themes { .. } | Command::Market { .. } => {
            extension_command(&mut client, &cli.command).await?;
        }
        Command::State {
            json,
            client: named,
        } => {
            state::state(&mut client, *json, named.as_deref()).await?;
        }
        Command::Pane { .. } | Command::Selection { .. } => {
            gui::command(&mut client, &cli.command).await?;
        }
        Command::Secret { action } => secret_command(&mut client, action).await?,
        Command::Undo { job, .. } => undo::undo(&mut client, *job).await?,
        Command::Update { action, json } => update::run(&mut client, *action, *json).await?,
    }
    Ok(())
}

/// The `log` commands, which read the log folder and need no core.
fn log_command(action: &LogAction) -> anyhow::Result<()> {
    match action {
        LogAction::Trace { id, dir, json } => {
            if id.parse::<RequestId>().is_err() {
                bail!("{id} is not a trace or request ID: those are ULIDs, 26 characters");
            }
            logs::trace(&cabinetos_diag::log_dir(dir.clone()), id, *json)
        }
        LogAction::Tail {
            process,
            heavy,
            lines,
            follow,
            json,
            dir,
        } => logs::tail(
            &cabinetos_diag::log_dir(dir.clone()),
            &logs::Tail {
                process: process.clone(),
                heavy: *heavy,
                lines: *lines,
                follow: *follow,
                json: *json,
            },
        ),
        LogAction::Bundle { .. } => unreachable!("log bundle needs the core"),
    }
}

/// `secret set|get|delete|list`.
async fn secret_command(client: &mut PipeClient, action: &SecretAction) -> anyhow::Result<()> {
    match action {
        SecretAction::Set { name, value } => secret::set(client, name, value.as_deref()).await,
        SecretAction::Get { name } => secret::get(client, name).await,
        SecretAction::Delete { name } => secret::delete(client, name).await,
        SecretAction::List => secret::list(client).await,
    }
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

/// The terminal commands: `term`, `term list|close|type|mode` (`term cwd`
/// is answered before, with its short timeouts).
async fn term_command(client: &mut PipeClient, arguments: &TermArgs) -> anyhow::Result<()> {
    match &arguments.action {
        None => {
            let cwd = absolute(arguments.cwd.as_deref().unwrap_or("."))?;
            let pane = term::pane(arguments.pane);
            term::run(client, arguments.profile.clone(), cwd, pane).await
        }
        Some(TermAction::List) => term::list(client).await,
        Some(TermAction::Close { id }) => {
            let request = Request::TerminalClose { session_id: *id };
            term::change(client, request, format_args!("session {id} closed")).await
        }
        Some(TermAction::Mode { id, mode }) => {
            let mode = term::mode(*mode);
            let request = Request::TerminalSetMode {
                session_id: *id,
                mode,
            };
            let word = term::mode_word(mode);
            term::change(client, request, format_args!("session {id}: {word}")).await
        }
        Some(TermAction::Type { id, paths }) => {
            let paths: Vec<String> = paths
                .iter()
                .map(|path| absolute(path))
                .collect::<anyhow::Result<_>>()?;
            let count = paths.len();
            let request = Request::TerminalTypePaths {
                session_id: *id,
                paths,
            };
            term::change(
                client,
                request,
                format_args!("session {id}: typed {count} paths"),
            )
            .await
        }
        Some(TermAction::Cwd { .. }) => unreachable!("term cwd is answered in execute"),
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
            MarketAction::Refresh { themes } => {
                let request = Request::MarketplaceRefresh {
                    catalogue: themes.then_some(Catalogue::Themes),
                };
                market::list(client, request).await
            }
            MarketAction::Search {
                query,
                kind,
                themes,
            } => {
                // A theme is in the themes catalogue only: `--kind theme`
                // asks for it without the second flag.
                let in_themes = *themes || *kind == Some(KindArg::Theme);
                let request = Request::MarketplaceSearch {
                    query: query.clone(),
                    kind: kind.map(extension_kind),
                    catalogue: in_themes.then_some(Catalogue::Themes),
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
        Command::Copy(arguments) if arguments.selection => {
            gui::transfer(client, JobKind::Copy, arguments).await?;
        }
        Command::Move(arguments) if arguments.selection => {
            gui::transfer(client, JobKind::Move, arguments).await?;
        }
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
                resolve: resolve.map(resolution_of_resolve),
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
            } => jobs::resolve(client, *job, *conflict, resolution_of(*resolution)).await?,
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
    send_waiting(client, request, REQUEST_TIMEOUT).await
}

/// [`send`], waiting up to `timeout` for the reply: a download may take
/// minutes.
pub(crate) async fn send_waiting(
    client: &mut PipeClient,
    request: Request,
    timeout: Duration,
) -> anyhow::Result<Envelope<Response>> {
    let id = RequestId::new();
    let trace = client.trace().cloned().unwrap_or_else(|| id.clone());
    let kind = request.type_tag();
    let exchange = async {
        tracing::debug!(request = kind, "sending request");
        let started = Instant::now();
        let reply = tokio::time::timeout(timeout, client.request_with_id(id.clone(), request))
            .await
            .with_context(|| format!("no reply to {kind} within {timeout:?} (id={id})"))?
            .with_context(|| format!("{kind} failed (id={id})"))?;
        tracing::info!(
            request = kind,
            rtt_us = u64::try_from(started.elapsed().as_micros()).unwrap_or(u64::MAX),
            "reply received"
        );
        Ok(reply)
    };
    exchange.instrument(span_for_action(&id, &trace)).await
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_binary_sizes() {
        assert_eq!(binary_size(512), "512.00 B");
        assert_eq!(binary_size(1536), "1.50 KiB");
        assert_eq!(binary_size(1_999_433_101_312), "1.82 TiB");
    }

    #[test]
    fn only_the_gui_context_commands_wait_one_second_for_the_pipe() {
        let command = |args: &[&str]| {
            let mut full = vec!["cabinetos-cli"];
            full.extend_from_slice(args);
            Cli::try_parse_from(full).unwrap().command
        };
        for fast in [
            &["pane"][..],
            &["pane", "--json"],
            &["selection"],
            &["copy", "--selection", "--dest", "opposite_pane"],
            &["move", "--selection", "--dest", r"E:\x"],
        ] {
            assert!(gui_context_command(&command(fast)), "{fast:?}");
        }
        for slow in [&["ping"][..], &["copy", "a", "b"], &["state"], &["jobs"]] {
            assert!(!gui_context_command(&command(slow)), "{slow:?}");
        }
    }

    #[test]
    fn a_copy_command_becomes_a_job_with_absolute_paths_and_its_conflict_policy() {
        let cli = Cli::try_parse_from([
            "cabinetos-cli",
            "copy",
            "a",
            "b",
            r"E:\dst",
            "--on-conflict",
            "newer",
            "--resolve",
            "skip",
        ])
        .unwrap();
        let Command::Copy(copy) = cli.command else {
            panic!("expected copy")
        };
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
        assert_eq!(job.resolve, Some(Resolution::Skip));
    }
}
