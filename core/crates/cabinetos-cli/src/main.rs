//! `cabinetos-cli.exe`: a command-line client for the core's pipe. It lets the
//! core be tested with no UI: `ping`, `ls` (read from shared memory, as the
//! UI will), `volume`, `shutdown`; `copy` in a later phase.
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

mod ls;

use std::io::Write;
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::{Duration, Instant};

use anyhow::{Context, anyhow, bail};
use cabinetos_diag::{Boundary, DiagConfig, span_for_request};
use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    Envelope, Request, RequestId, Response, SortKey, SortSpec, VolumeDetails,
};
use clap::{Parser, Subcommand, ValueEnum};
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
        /// What to sort by; directories always come first.
        #[arg(long, value_enum, default_value_t = SortArg::Name)]
        sort: SortArg,
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
                include_hidden: *hidden,
                sort: SortSpec {
                    key: (*sort).into(),
                    descending: *desc,
                },
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
                sort: SortArg::Modified,
                desc: true,
                watch: true,
            }
        );
        let plain = Cli::try_parse_from(["cabinetos-cli", "ls", "."]).unwrap();
        assert!(matches!(
            plain.command,
            Command::Ls {
                sort: SortArg::Name,
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
