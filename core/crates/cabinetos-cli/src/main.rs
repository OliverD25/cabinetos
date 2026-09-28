//! `cabinetos-cli.exe`: a command-line client for the core's pipe. It lets the
//! core be tested with no UI: `ping` now, `ls` and `copy` in later phases.
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

use std::path::PathBuf;
use std::process::ExitCode;
use std::time::{Duration, Instant};

use anyhow::{Context, bail};
use cabinetos_diag::{Boundary, DiagConfig, span_for_request};
use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{Envelope, Request, RequestId, Response};
use clap::{Parser, Subcommand};
use tracing::Instrument;

/// How long to wait for a busy pipe.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(5);
/// How long to wait for one reply.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(5);

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

    match cli.command {
        Command::Ping { count } => {
            for _ in 0..count {
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
                println!(
                    "pong id={} protocol={protocol_version} core={core_version} rtt={:.2}ms",
                    reply.id,
                    rtt.as_secs_f64() * 1000.0
                );
            }
        }
        Command::Shutdown => {
            let reply = send(&mut client, Request::Shutdown).await?;
            if reply.body != Response::Ok {
                bail!("expected ok, got {:?} (id={})", reply.body, reply.id);
            }
            println!("shutdown acknowledged id={}", reply.id);
        }
    }
    Ok(())
}

/// Sends one request inside its request span and waits for the reply. The
/// client rejects a reply whose ID differs from the request's.
async fn send(client: &mut PipeClient, request: Request) -> anyhow::Result<Envelope<Response>> {
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
    fn rejects_zero_pings_and_a_missing_command() {
        assert!(Cli::try_parse_from(["cabinetos-cli", "ping", "--count", "0"]).is_err());
        assert!(Cli::try_parse_from(["cabinetos-cli"]).is_err());
    }
}
