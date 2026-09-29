//! A test fixture for the `before-job` hook: any job whose destination
//! contains `forbidden` is stopped with a reason.

wit_bindgen::generate!({
    path: "../../../wit",
    world: "core-plugin",
});

use cabinetos::plugin::host::{self, LogLevel};

struct Vetoer;

impl Guest for Vetoer {
    fn info() -> PluginInfo {
        PluginInfo {
            id: "vetoer".to_owned(),
            name: "Vetoer".to_owned(),
            version: env!("CARGO_PKG_VERSION").to_owned(),
            api_version: "0.2.0".to_owned(),
        }
    }

    fn activate(_ctx: Activation) -> Result<(), String> {
        Ok(())
    }

    fn deactivate() {}

    fn on_command(id: String, _args: String) -> Result<String, String> {
        Err(format!("Vetoer has no command {id}"))
    }

    fn before_job(job: JobSummary) -> JobVerdict {
        match &job.destination {
            Some(destination) if destination.to_lowercase().contains("forbidden") => {
                host::log(LogLevel::Info, &format!("stopping job {}", job.job_id));
                JobVerdict::Deny(format!("{destination} is a forbidden destination"))
            }
            _ => JobVerdict::Allow,
        }
    }

    fn on_listing_opened(_path: String, _entry_count: u32) {}

    fn on_event(_name: String, _payload: String) {}
}

export!(Vetoer);
