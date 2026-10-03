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

    /// Read and watch this configuration file instead of
    /// %APPDATA%\CabinetOS\cabinetos.json (or the `CABINETOS_CONFIG`
    /// variable). It is created with the defaults if it does not exist.
    #[arg(long, value_name = "PATH")]
    config: Option<PathBuf>,

    /// Load the Core Plugins from this folder instead of
    /// %LOCALAPPDATA%\CabinetOS\plugins (or the `CABINETOS_PLUGINS_DIR`
    /// variable): one subfolder per plugin, with plugin.json and plugin.wasm.
    #[arg(long, value_name = "PATH")]
    plugins_dir: Option<PathBuf>,

    /// Keep each plugin's own folder under this folder instead of
    /// %LOCALAPPDATA%\CabinetOS\plugins-data (or the
    /// `CABINETOS_PLUGINS_DATA_DIR` variable).
    #[arg(long, value_name = "PATH")]
    plugins_data_dir: Option<PathBuf>,

    /// Read the colour themes from this folder instead of
    /// %LOCALAPPDATA%\CabinetOS\themes (or the `CABINETOS_THEMES_DIR`
    /// variable): one <id>.json per theme. The shipped themes that are
    /// missing are written there at the start.
    #[arg(long, value_name = "PATH")]
    themes_dir: Option<PathBuf>,

    /// Install Tool Extensions into this folder instead of
    /// %LOCALAPPDATA%\CabinetOS\tools, where the window reads them. For
    /// tests; unlike the window, the core reads no `CABINETOS_TOOLS_DIR`.
    #[arg(long, value_name = "PATH")]
    tools_dir: Option<PathBuf>,

    /// List the Tool Extensions of this folder first for Quick View: the
    /// window's folder of tools in development (its --tools-dir). A tool
    /// here wins over an installed one with the same ID.
    #[arg(long, value_name = "PATH")]
    dev_tools_dir: Option<PathBuf>,

    /// Keep the marketplace's own files (the index cache, downloads while
    /// they run, the record of installs) in this folder instead of
    /// %LOCALAPPDATA%\CabinetOS\marketplace (or the
    /// `CABINETOS_MARKETPLACE_DIR` variable).
    #[arg(long, value_name = "PATH")]
    marketplace_dir: Option<PathBuf>,

    /// Keep the updater's own files (its state, the release downloaded for
    /// the next swap) in this folder instead of
    /// %LOCALAPPDATA%\CabinetOS\update (or the `CABINETOS_UPDATE_DIR`
    /// variable).
    #[arg(long, value_name = "PATH")]
    update_dir: Option<PathBuf>,

    /// Log one event, then panic: tests crash traces.
    #[arg(long, hide = true)]
    self_test_panic: bool,

    /// Run normally, and panic one thread of the core a second after the
    /// start: tests that a panic on any thread stops the core.
    #[arg(long, hide = true)]
    self_test_thread_panic: bool,
}

fn main() -> ExitCode {
    let args = Args::parse();
    if args.self_test_panic {
        self_test_panic(args.log_dir);
    }
    if args.self_test_thread_panic {
        self_test_thread_panic();
    }

    // An invalid value falls back to the default; `run` logs a warning once
    // diagnostics are up.
    let (workers, _) = worker_threads(std::env::var(WORKERS_ENV).ok().as_deref());
    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .worker_threads(workers)
        .enable_all()
        .thread_name_fn(runtime_thread_name)
        // The async workers read and answer the pipe: in heavy mode they
        // never wait for the log writer (only workers park; the blocking
        // threads, which do the work, may wait).
        .on_thread_park(cabinetos_diag::never_wait_for_heavy_log)
        .on_thread_unpark(cabinetos_diag::never_wait_for_heavy_log)
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
        config_path: args.config,
        plugins_dir: args.plugins_dir,
        plugins_data_dir: args.plugins_data_dir,
        themes_dir: args.themes_dir,
        tools_dir: args.tools_dir,
        dev_tools_dir: args.dev_tools_dir,
        marketplace_dir: args.marketplace_dir,
        update_dir: args.update_dir,
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

/// A thread of the core's own that panics while the core serves: what a
/// bug in a directory watcher or a plugin's thread would do.
fn self_test_thread_panic() {
    let spawned = std::thread::Builder::new()
        .name("self-test".to_owned())
        .spawn(|| {
            std::thread::sleep(std::time::Duration::from_secs(1));
            tracing::info!("about to panic on a thread of the core (self-test)");
            panic!("self-test panic on a thread");
        });
    if let Err(error) = spawned {
        eprintln!("cabinetos-core: cannot start the self-test thread: {error}");
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
            "--config",
            r"D:\c.json",
            "--plugins-dir",
            r"E:\plugins",
            "--plugins-data-dir",
            r"E:\plugins-data",
            "--themes-dir",
            r"E:\themes",
            "--tools-dir",
            r"E:\tools",
            "--dev-tools-dir",
            r"E:\dev-tools",
            "--marketplace-dir",
            r"E:\marketplace",
            "--update-dir",
            r"E:\update",
            "--self-test-panic",
            "--self-test-thread-panic",
        ])
        .unwrap();
        assert_eq!(args.pipe, "abc");
        assert_eq!(args.parent_pid, Some(42));
        assert_eq!(args.log_dir, Some(PathBuf::from(r"C:\logs")));
        assert_eq!(args.config, Some(PathBuf::from(r"D:\c.json")));
        assert_eq!(args.plugins_dir, Some(PathBuf::from(r"E:\plugins")));
        assert_eq!(
            args.plugins_data_dir,
            Some(PathBuf::from(r"E:\plugins-data"))
        );
        assert_eq!(args.themes_dir, Some(PathBuf::from(r"E:\themes")));
        assert_eq!(args.tools_dir, Some(PathBuf::from(r"E:\tools")));
        assert_eq!(args.dev_tools_dir, Some(PathBuf::from(r"E:\dev-tools")));
        assert_eq!(args.marketplace_dir, Some(PathBuf::from(r"E:\marketplace")));
        assert_eq!(args.update_dir, Some(PathBuf::from(r"E:\update")));
        assert!(args.self_test_panic);
        assert!(args.self_test_thread_panic);
    }
}
