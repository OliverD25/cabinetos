//! The indexer as a Windows service: installing, removing, and the service
//! body. The service runs as `LocalSystem` (which may read the MFT), starts
//! only when asked (start type manual), and does exactly what `--console`
//! does until the service manager says stop.

use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock, PoisonError};
use std::time::{Duration, Instant};

use cabinetos_ipc::PipeName;
use tokio_util::sync::CancellationToken;
use windows_service::service::{
    ServiceAccess, ServiceControl, ServiceControlAccept, ServiceErrorControl, ServiceExitCode,
    ServiceInfo, ServiceStartType, ServiceState, ServiceStatus, ServiceType,
};
use windows_service::service_control_handler::{self, ServiceControlHandlerResult};
use windows_service::service_manager::{ServiceManager, ServiceManagerAccess};
use windows_service::{define_windows_service, service_dispatcher};

/// The service's name, for `sc start cabinetos-indexer`.
pub(crate) const SERVICE_NAME: &str = "cabinetos-indexer";
const DISPLAY_NAME: &str = "CabinetOS Indexer";
const DESCRIPTION: &str = "Keeps an index of the files on NTFS volumes for CabinetOS search. Read-only: it never changes a file.";

/// How long `--uninstall` waits for a running service to stop.
const STOP_WAIT: Duration = Duration::from_secs(30);

/// Why a service operation failed.
#[derive(Debug, thiserror::Error)]
pub(crate) enum ServiceError {
    /// The service manager refused.
    #[error("{what}: {source}")]
    Windows {
        what: &'static str,
        #[source]
        source: windows_service::Error,
    },
    /// This program's own path is unknown.
    #[error("cannot find this program's path: {0}")]
    Path(std::io::Error),
    /// The async runtime would not start.
    #[error("cannot start the async runtime: {0}")]
    Runtime(std::io::Error),
    /// The indexer itself failed.
    #[error(transparent)]
    Run(#[from] cabinetos_indexer::RunError),
    /// A running service did not stop in time.
    #[error("the service did not stop within {} s", STOP_WAIT.as_secs())]
    StopTimeout,
}

fn failed(what: &'static str) -> impl FnOnce(windows_service::Error) -> ServiceError {
    move |source| ServiceError::Windows { what, source }
}

/// What the service body runs; set before the dispatcher starts.
struct Plan {
    letters: Vec<char>,
    pipe: PipeName,
}

static PLAN: OnceLock<Plan> = OnceLock::new();
static OUTCOME: Mutex<Option<Result<(), ServiceError>>> = Mutex::new(None);

/// Runs as the service until the service manager stops it. Only works when
/// the service manager started this process.
pub(crate) fn run(letters: Vec<char>, pipe: PipeName) -> Result<(), ServiceError> {
    let _ = PLAN.set(Plan { letters, pipe });
    service_dispatcher::start(SERVICE_NAME, ffi_service_main).map_err(failed(
        "cannot run as a service; the service manager starts it this way (sc start cabinetos-indexer)",
    ))?;
    OUTCOME
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
        .take()
        .unwrap_or(Ok(()))
}

// The service manager calls this entry point with raw arguments; the macro
// turns them into strings (the command line was already parsed in `main`).
define_windows_service!(ffi_service_main, service_main);

fn service_main(_arguments: Vec<OsString>) {
    let result = serve();
    if let Err(error) = &result {
        tracing::error!(%error, "the service failed");
    }
    *OUTCOME.lock().unwrap_or_else(PoisonError::into_inner) = Some(result);
}

fn serve() -> Result<(), ServiceError> {
    let plan = PLAN
        .get()
        .expect("the plan is set before the dispatcher starts");
    let shutdown = CancellationToken::new();
    let stop = shutdown.clone();
    let status = service_control_handler::register(SERVICE_NAME, move |control| match control {
        ServiceControl::Stop | ServiceControl::Shutdown => {
            tracing::info!("the service manager asked the indexer to stop");
            stop.cancel();
            ServiceControlHandlerResult::NoError
        }
        ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
        _ => ServiceControlHandlerResult::NotImplemented,
    })
    .map_err(failed("cannot register with the service manager"))?;
    let report = |state, accepted, code| {
        status.set_service_status(ServiceStatus {
            service_type: ServiceType::OWN_PROCESS,
            current_state: state,
            controls_accepted: accepted,
            exit_code: ServiceExitCode::Win32(code),
            checkpoint: 0,
            wait_hint: Duration::default(),
            process_id: None,
        })
    };
    report(
        ServiceState::Running,
        ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN,
        0,
    )
    .map_err(failed("cannot report the service as running"))?;
    let result = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .thread_name("indexer-rt")
        .build()
        .map_err(ServiceError::Runtime)
        .and_then(|runtime| {
            runtime
                .block_on(cabinetos_indexer::run(&plan.letters, &plan.pipe, shutdown))
                .map_err(ServiceError::from)
        });
    let _ = report(
        ServiceState::Stopped,
        ServiceControlAccept::empty(),
        u32::from(result.is_err()),
    );
    result
}

/// `%ProgramData%\CabinetOS\logs`: `LocalSystem` has no user profile of
/// interest, and an administrator finds the log there.
fn service_log_dir() -> PathBuf {
    std::env::var_os("ProgramData")
        .map_or_else(|| PathBuf::from(r"C:\ProgramData"), PathBuf::from)
        .join("CabinetOS")
        .join("logs")
}

/// The command line the service manager starts the service with.
fn service_arguments(volumes: &[char], log_dir: &Path) -> Vec<OsString> {
    let mut arguments = vec![
        OsString::from("--service"),
        OsString::from("--log-dir"),
        log_dir.as_os_str().to_owned(),
    ];
    if !volumes.is_empty() {
        arguments.push(OsString::from("--volumes"));
        arguments.push(OsString::from(
            volumes
                .iter()
                .map(char::to_string)
                .collect::<Vec<_>>()
                .join(","),
        ));
    }
    arguments
}

/// Registers the service: this program, `--service`, start type manual,
/// account `LocalSystem`.
pub(crate) fn install(volumes: &[char], log_dir: Option<&Path>) -> Result<(), ServiceError> {
    let manager = ServiceManager::local_computer(
        None::<&str>,
        ServiceManagerAccess::CONNECT | ServiceManagerAccess::CREATE_SERVICE,
    )
    .map_err(failed("cannot open the service manager"))?;
    let executable = std::env::current_exe().map_err(ServiceError::Path)?;
    let log_dir = log_dir.map_or_else(service_log_dir, Path::to_path_buf);
    let info = ServiceInfo {
        name: OsString::from(SERVICE_NAME),
        display_name: OsString::from(DISPLAY_NAME),
        service_type: ServiceType::OWN_PROCESS,
        start_type: ServiceStartType::OnDemand,
        error_control: ServiceErrorControl::Normal,
        executable_path: executable,
        launch_arguments: service_arguments(volumes, &log_dir),
        dependencies: Vec::new(),
        account_name: None,
        account_password: None,
    };
    let service = manager
        .create_service(&info, ServiceAccess::CHANGE_CONFIG)
        .map_err(failed("cannot create the service"))?;
    service
        .set_description(DESCRIPTION)
        .map_err(failed("cannot describe the service"))?;
    Ok(())
}

/// Stops the service if it runs, then removes it.
pub(crate) fn uninstall() -> Result<(), ServiceError> {
    let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
        .map_err(failed("cannot open the service manager"))?;
    let service = manager
        .open_service(
            SERVICE_NAME,
            ServiceAccess::QUERY_STATUS | ServiceAccess::STOP | ServiceAccess::DELETE,
        )
        .map_err(failed("cannot open the service (is it installed?)"))?;
    let state = |service: &windows_service::service::Service| {
        service
            .query_status()
            .map(|status| status.current_state)
            .map_err(failed("cannot query the service"))
    };
    if state(&service)? != ServiceState::Stopped {
        service.stop().map_err(failed("cannot stop the service"))?;
        let deadline = Instant::now() + STOP_WAIT;
        while state(&service)? != ServiceState::Stopped {
            if Instant::now() >= deadline {
                return Err(ServiceError::StopTimeout);
            }
            std::thread::sleep(Duration::from_millis(200));
        }
    }
    service
        .delete()
        .map_err(failed("cannot delete the service"))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_service_starts_with_its_mode_log_and_volumes() {
        let arguments = service_arguments(&['C', 'D'], Path::new(r"C:\ProgramData\CabinetOS\logs"));
        assert_eq!(
            arguments,
            [
                "--service",
                "--log-dir",
                r"C:\ProgramData\CabinetOS\logs",
                "--volumes",
                "C,D"
            ]
        );
        assert_eq!(
            service_arguments(&[], Path::new(r"D:\logs")),
            ["--service", "--log-dir", r"D:\logs"]
        );
        assert!(service_log_dir().ends_with(r"CabinetOS\logs"));
    }
}
