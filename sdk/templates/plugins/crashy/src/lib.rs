//! A test fixture: `crashy.crash` panics. A panic in a plugin is a trap;
//! the host must log it, drop the plugin and keep serving.

wit_bindgen::generate!({
    path: "../../../wit",
    world: "core-plugin",
});

use cabinetos::plugin::host::{self, LogLevel};

struct Crashy;

impl Guest for Crashy {
    fn info() -> PluginInfo {
        PluginInfo {
            id: "crashy".to_owned(),
            name: "Crashy".to_owned(),
            version: env!("CARGO_PKG_VERSION").to_owned(),
            api_version: "0.1.0".to_owned(),
        }
    }

    fn activate(_ctx: Activation) -> Result<(), String> {
        host::register_command("crashy.crash", "Crash", "Crashy", &[]);
        Ok(())
    }

    fn deactivate() {}

    fn on_command(_id: String, _args: String) -> Result<String, String> {
        host::log(LogLevel::Warn, "about to crash, as asked");
        panic!("crashy was asked to crash");
    }

    fn before_job(_job: JobSummary) -> JobVerdict {
        JobVerdict::Allow
    }

    fn on_listing_opened(_path: String, _entry_count: u32) {}
}

export!(Crashy);
