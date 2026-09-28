//! The core's side of the plugin host (`cabinetos-plugins`,
//! `docs/plugins.md`): what the host may ask of the core, the gate every
//! job passes, and the task that keeps the plugins in step with the
//! `plugins` settings.

use std::path::PathBuf;
use std::sync::Arc;

use cabinetos_jobs::{JobGate, JobPreview, JobQueueManager};
use cabinetos_plugins::manifest::Capability;
use cabinetos_plugins::{HostConfig, HostServices, PluginCommand, PluginHost};
use cabinetos_protocol::Event;
use tokio::sync::watch;

use crate::CORE_VERSION;
use crate::events::EventHub;
use crate::settings::{Settings, Snapshot};

/// What the plugin host may ask of the core.
struct Bridge {
    settings: Arc<Settings>,
    events: Arc<EventHub>,
}

impl HostServices for Bridge {
    fn config_value(&self, path: &str) -> Option<String> {
        // What other plugins were granted is none of a plugin's business.
        if path == "plugins" || path.starts_with("plugins.") {
            return None;
        }
        let config = serde_json::to_value(&self.settings.snapshot().config).ok()?;
        let mut value = &config;
        for key in path.split('.') {
            value = value.get(key)?;
        }
        Some(value.to_string())
    }

    fn publish(&self, event: Event) {
        self.events.publish(event);
    }

    fn set_commands(&self, plugin_id: &str, plugin_name: &str, commands: &[PluginCommand]) {
        self.settings
            .set_plugin_commands(plugin_id, plugin_name, commands);
    }
}

/// Asks the plugins that hold `jobs:intercept` about every job.
struct Gate(Arc<PluginHost>);

impl JobGate for Gate {
    fn check(&self, job: &JobPreview<'_>) -> Result<(), String> {
        self.0.before_job(
            job.job_id,
            job.kind,
            job.sources,
            job.destination,
            job.files_total,
            job.bytes_total,
        )
    }
}

/// Starts the plugin host and puts it in front of the job engine. No plugin
/// is loaded yet: [`follow_settings`] does that. Without a host (wasmtime
/// cannot start on this machine) the core runs without plugins.
pub(crate) fn start(
    plugins_dir: PathBuf,
    data_dir: PathBuf,
    settings: &Arc<Settings>,
    events: &Arc<EventHub>,
    jobs: &JobQueueManager,
) -> Option<Arc<PluginHost>> {
    let bridge = Arc::new(Bridge {
        settings: Arc::clone(settings),
        events: Arc::clone(events),
    });
    tracing::info!(
        plugins_dir = %plugins_dir.display(),
        data_dir = %data_dir.display(),
        "starting the plugin host"
    );
    let config = HostConfig::new(plugins_dir, data_dir, CORE_VERSION);
    match PluginHost::new(config, bridge) {
        Ok(host) => {
            let host = Arc::new(host);
            jobs.set_gate(Arc::new(Gate(Arc::clone(&host))));
            Some(host)
        }
        Err(error) => {
            tracing::error!(%error, "cannot start the plugin host; the core runs without plugins");
            None
        }
    }
}

/// Loads the plugins, then keeps them in step with the `plugins` settings:
/// a grant or a switch saved in the file takes effect without a restart.
pub(crate) async fn follow_settings(
    host: Arc<PluginHost>,
    mut changes: watch::Receiver<Arc<Snapshot>>,
) {
    let mut applied = changes.borrow_and_update().config.plugins.clone();
    let first = applied.clone();
    let loading = Arc::clone(&host);
    // Reading the folders is disk work; compiling happens on each plugin's
    // own thread.
    let _ = tokio::task::spawn_blocking(move || loading.load_all(&first)).await;
    while changes.changed().await.is_ok() {
        let plugins = changes.borrow_and_update().config.plugins.clone();
        if plugins != applied {
            applied.clone_from(&plugins);
            let host = Arc::clone(&host);
            let _ = tokio::task::spawn_blocking(move || host.apply_settings(&plugins)).await;
        }
    }
}

/// Checks capability names before they are written into the settings.
pub(crate) fn check_grants(capabilities: &[String]) -> Result<(), String> {
    if capabilities.is_empty() {
        return Err("name at least one capability to grant".to_owned());
    }
    for name in capabilities {
        match Capability::parse(name) {
            None => {
                let known: Vec<&str> = Capability::ALL.iter().map(|known| known.name()).collect();
                return Err(format!(
                    "unknown capability `{name}`; known: {}",
                    known.join(", ")
                ));
            }
            Some(capability) if capability.never_granted() => {
                return Err(format!(
                    "{name} is never granted in this version of CabinetOS"
                ));
            }
            Some(_) => {}
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_known_grantable_capabilities_are_granted() {
        check_grants(&["fs:read".to_owned(), "cmd:register".to_owned()]).unwrap();
        assert!(check_grants(&[]).is_err());
        assert!(
            check_grants(&["fs:everything".to_owned()])
                .unwrap_err()
                .contains("unknown capability")
        );
        assert!(
            check_grants(&["net".to_owned()])
                .unwrap_err()
                .contains("never granted")
        );
    }
}
