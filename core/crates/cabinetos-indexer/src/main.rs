//! `cabinetos-indexer.exe`: indexes NTFS volumes and answers read-only
//! searches over `\\.\pipe\cabinetos-indexer` (`docs/indexer.md`).
//!
//! - `--console` runs in the terminal until Ctrl+C (needs an elevated
//!   terminal).
//! - `--service` is how the Windows service manager starts it.
//! - `--install` and `--uninstall` register and remove the service
//!   `cabinetos-indexer` (manual start; needs an elevated terminal).
//!
//! Serves Constitution Article 1 (real-time NT-level file indexing) and
//! Article 12 (its log, `indexer.<date>.jsonl`, carries each request's ID).

mod service;

use std::path::PathBuf;
use std::process::ExitCode;

use cabinetos_diag::{Boundary, DiagConfig, DiagGuard};
use cabinetos_index::{file_system, is_elevated, ntfs_volumes};
use cabinetos_indexer::PIPE_NAME;
use cabinetos_ipc::PipeName;
use clap::{ArgGroup, Parser};
use tokio_util::sync::CancellationToken;

/// Indexes NTFS volumes for CabinetOS search and answers read-only queries.
#[derive(Debug, Parser)]
#[command(name = "cabinetos-indexer", version)]
#[expect(
    clippy::struct_excessive_bools,
    reason = "four flags of which clap lets exactly one be set"
)]
#[command(group(
    ArgGroup::new("mode")
        .required(true)
        .args(["console", "service", "install", "uninstall"])
))]
struct Args {
    /// Run in this terminal until Ctrl+C. Needs an elevated terminal.
    #[arg(long)]
    console: bool,

    /// Run as the Windows service. The service manager starts it this way.
    #[arg(long)]
    service: bool,

    /// Register the Windows service `cabinetos-indexer` (manual start), with
    /// these --volumes and --log-dir. Needs an elevated terminal.
    #[arg(long)]
    install: bool,

    /// Stop and remove the Windows service. Needs an elevated terminal.
    #[arg(long)]
    uninstall: bool,

    /// The drive letters to index, such as C,D. Default: every NTFS volume
    /// with a drive letter on a fixed, removable or RAM disk.
    #[arg(long, value_delimiter = ',', value_parser = parse_letter, value_name = "LETTERS")]
    volumes: Vec<char>,

    /// Write the log here instead of %LOCALAPPDATA%\CabinetOS\logs (the
    /// service writes to %ProgramData%\CabinetOS\logs).
    #[arg(long, value_name = "PATH")]
    log_dir: Option<PathBuf>,

    /// Listen on this pipe instead of \\.\pipe\cabinetos-indexer. For tests.
    #[arg(long, hide = true, value_name = "NAME")]
    pipe: Option<String>,
}

/// `C`, `c`, `C:` or `C:\` → `C`.
fn parse_letter(text: &str) -> Result<char, String> {
    let trimmed = text
        .trim()
        .trim_end_matches(['\\', '/'])
        .trim_end_matches(':');
    let mut chars = trimmed.chars();
    match (chars.next(), chars.next()) {
        (Some(letter), None) if letter.is_ascii_alphabetic() => Ok(letter.to_ascii_uppercase()),
        _ => Err(format!("`{text}` is not a drive letter")),
    }
}

impl Args {
    fn pipe(&self) -> PipeName {
        PipeName::from_full(self.pipe.clone().unwrap_or_else(|| PIPE_NAME.to_owned()))
    }

    /// The volumes to index: the ones asked for, or every NTFS volume. A
    /// letter that is not NTFS is left out with a message.
    fn letters(&self) -> Vec<char> {
        if self.volumes.is_empty() {
            return ntfs_volumes();
        }
        self.volumes
            .iter()
            .copied()
            .filter(|&letter| match file_system(letter) {
                Some(system) if system == "NTFS" => true,
                Some(system) => {
                    eprintln!(
                        "cabinetos-indexer: {letter}: is {system}, not NTFS; it is not indexed"
                    );
                    false
                }
                None => {
                    eprintln!("cabinetos-indexer: there is no volume {letter}:");
                    false
                }
            })
            .collect()
    }
}

/// Starts diagnostics: process `indexer`, boundary `indexer`.
fn diagnostics(log_dir: Option<PathBuf>) -> Option<DiagGuard> {
    match cabinetos_diag::init(DiagConfig {
        process: "indexer",
        boundary: Boundary::Indexer,
        dir: log_dir,
        log_file: true,
    }) {
        Ok(guard) => Some(guard),
        Err(error) => {
            eprintln!("cabinetos-indexer: running without a log: {error}");
            None
        }
    }
}

fn needs_elevation(what: &str) -> ExitCode {
    eprintln!(
        "cabinetos-indexer: {what} needs Administrator rights; run it from an elevated terminal (Run as administrator)"
    );
    ExitCode::from(2)
}

fn main() -> ExitCode {
    let args = Args::parse();
    if args.install {
        if !is_elevated() {
            return needs_elevation("--install");
        }
        return report(
            service::install(&args.volumes, args.log_dir.as_deref()),
            "installed the service cabinetos-indexer (start: manual); start it with: sc start cabinetos-indexer",
        );
    }
    if args.uninstall {
        if !is_elevated() {
            return needs_elevation("--uninstall");
        }
        return report(
            service::uninstall(),
            "removed the service cabinetos-indexer",
        );
    }
    if args.service {
        let _diag = diagnostics(args.log_dir.clone());
        return match service::run(args.letters(), args.pipe()) {
            Ok(()) => ExitCode::SUCCESS,
            Err(error) => {
                tracing::error!(%error, "the service could not run");
                eprintln!("cabinetos-indexer: {error}");
                ExitCode::FAILURE
            }
        };
    }
    console(&args)
}

fn report(result: Result<(), service::ServiceError>, done: &str) -> ExitCode {
    match result {
        Ok(()) => {
            println!("cabinetos-indexer: {done}");
            ExitCode::SUCCESS
        }
        Err(error) => {
            eprintln!("cabinetos-indexer: {error}");
            ExitCode::FAILURE
        }
    }
}

/// `--console`: runs until Ctrl+C.
fn console(args: &Args) -> ExitCode {
    if !is_elevated() {
        return needs_elevation("--console");
    }
    let _diag = diagnostics(args.log_dir.clone());
    let letters = args.letters();
    if letters.is_empty() {
        eprintln!("cabinetos-indexer: no NTFS volume to index");
        return ExitCode::FAILURE;
    }
    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .thread_name("indexer-rt")
        .build()
    {
        Ok(runtime) => runtime,
        Err(error) => {
            eprintln!("cabinetos-indexer: cannot start the async runtime: {error}");
            return ExitCode::FAILURE;
        }
    };
    let pipe = args.pipe();
    println!(
        "cabinetos-indexer: indexing {} and serving {pipe} (Ctrl+C to stop)",
        letters
            .iter()
            .map(|letter| format!("{letter}:"))
            .collect::<Vec<_>>()
            .join(" ")
    );
    let shutdown = CancellationToken::new();
    let stop = shutdown.clone();
    let result = runtime.block_on(async move {
        tokio::spawn(async move {
            if tokio::signal::ctrl_c().await.is_ok() {
                tracing::info!("Ctrl+C received; stopping");
                stop.cancel();
            }
        });
        cabinetos_indexer::run(&letters, &pipe, shutdown).await
    });
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            tracing::error!(%error, "the indexer could not run");
            eprintln!("cabinetos-indexer: {error}");
            ExitCode::FAILURE
        }
    }
}

#[cfg(test)]
mod tests {
    use clap::CommandFactory;

    use super::*;

    #[test]
    fn the_command_line_definition_is_valid() {
        Args::command().debug_assert();
    }

    #[test]
    fn exactly_one_mode_is_required() {
        assert!(Args::try_parse_from(["cabinetos-indexer"]).is_err());
        assert!(Args::try_parse_from(["cabinetos-indexer", "--console", "--install"]).is_err());
        let args =
            Args::try_parse_from(["cabinetos-indexer", "--console", "--volumes", "c,D:,e:\\"])
                .unwrap();
        assert!(args.console);
        assert_eq!(args.volumes, ['C', 'D', 'E']);
        assert_eq!(args.pipe().as_str(), PIPE_NAME);
        assert!(
            Args::try_parse_from(["cabinetos-indexer", "--console", "--volumes", "CD"]).is_err()
        );
        let args =
            Args::try_parse_from(["cabinetos-indexer", "--install", "--pipe", r"\\.\pipe\x"])
                .unwrap();
        assert_eq!(args.pipe().as_str(), r"\\.\pipe\x");
    }

    #[test]
    fn drive_letters_parse_in_any_usual_form() {
        assert_eq!(parse_letter("c"), Ok('C'));
        assert_eq!(parse_letter("D:"), Ok('D'));
        assert_eq!(parse_letter(r"e:\"), Ok('E'));
        assert!(parse_letter("").is_err());
        assert!(parse_letter("1").is_err());
        assert!(parse_letter("CD").is_err());
    }
}
