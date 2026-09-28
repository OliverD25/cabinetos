//! `cabinetos-core.exe`: the headless core process. The CabinetOS window
//! starts it with a random pipe token and its own process ID; for development
//! it runs on its own with the `dev` pipe.
//!
//! Serves Constitution Article 1 (the core does all the work, off the UI
//! thread) and Article 12 (diagnostics start before anything else).
#![forbid(unsafe_code)]

use std::path::PathBuf;
use std::process::ExitCode;
use std::sync::atomic::{AtomicUsize, Ordering};

use cabinetos_core::{CoreConfig, WORKERS_ENV, diag_config, run, worker_threads};
use cabinetos_ipc::PipeName;
use clap::Parser;
use tokio_util::sync::CancellationToken;

/// The headless CabinetOS core. The CabinetOS window starts it; run it by
/// hand only for development.
#[derive(Debug, Parser)]
#[command(name = "cabinetos-core", version)]
struct Args {
    /// Pipe token: the core listens on \\.\pipe\cabinetos-core-<TOKEN>.
    #[arg(long, value_name = "TOKEN", default_value = "dev")]
    pipe: String,

    /// Exit when this process exits (the window that started the core).
    #[arg(long, value_name = "PID")]
    parent_pid: Option<u32>,

    /// Write logs and crash traces here instead of
    /// %LOCALAPPDATA%\CabinetOS\logs.
    #[arg(long, value_name = "PATH")]
    log_dir: Option<PathBuf>,

    /// Log one event, then panic: tests crash traces.
    #[arg(long, hide = true)]
    self_test_panic: bool,
}

fn main() -> ExitCode {
    let args = Args::parse();
    if args.self_test_panic {
        self_test_panic(args.log_dir);
    }

    // An invalid value falls back to the default; `run` logs a warning once
    // diagnostics are up.
    let (workers, _) = worker_threads(std::env::var(WORKERS_ENV).ok().as_deref());
    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .worker_threads(workers)
        .enable_all()
        .thread_name_fn(runtime_thread_name)
        .build()
    {
        Ok(runtime) => runtime,
        Err(error) => {
            eprintln!("cabinetos-core: cannot start the async runtime: {error}");
            return ExitCode::FAILURE;
        }
    };
    let config = CoreConfig {
        pipe: PipeName::new(&args.pipe),
        parent_pid: args.parent_pid,
        log_dir: args.log_dir,
    };
    match runtime.block_on(run(config, CancellationToken::new())) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("cabinetos-core: {error}");
            ExitCode::FAILURE
        }
    }
}

/// Numbered names, so each log line says which runtime thread wrote it. Tokio
/// uses this for async workers and for its blocking-thread pool alike.
fn runtime_thread_name() -> String {
    static NEXT: AtomicUsize = AtomicUsize::new(1);
    format!("core-rt-{}", NEXT.fetch_add(1, Ordering::Relaxed))
}

/// Proves the crash path end to end: a crash trace with the recent events, and
/// a log file flushed before the process dies.
fn self_test_panic(log_dir: Option<PathBuf>) -> ! {
    let _diag = cabinetos_diag::init(diag_config(log_dir))
        .expect("diagnostics must start for the self-test");
    tracing::info!("about to panic (self-test)");
    panic!("self-test panic");
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
    fn defaults_to_the_dev_pipe() {
        let args = Args::try_parse_from(["cabinetos-core"]).unwrap();
        assert_eq!(args.pipe, "dev");
        assert_eq!(args.parent_pid, None);
        assert!(!args.self_test_panic);
    }

    #[test]
    fn parses_every_option() {
        let args = Args::try_parse_from([
            "cabinetos-core",
            "--pipe",
            "abc",
            "--parent-pid",
            "42",
            "--log-dir",
            r"C:\logs",
            "--self-test-panic",
        ])
        .unwrap();
        assert_eq!(args.pipe, "abc");
        assert_eq!(args.parent_pid, Some(42));
        assert_eq!(args.log_dir, Some(PathBuf::from(r"C:\logs")));
        assert!(args.self_test_panic);
    }
}
