//! A test fixture for `fs:watch`. `watcher.watch {"path"}` and
//! `watcher.unwatch {"path"}` call the host's `watch-folder` and
//! `unwatch-folder`; `watch` answers with the roots the core gave it. Every
//! `on-event` it gets goes on unchanged through `emit`, so a test sees it
//! as a `plugin_event`.

wit_bindgen::generate!({
    path: "../../../wit",
    world: "core-plugin",
});

use std::sync::OnceLock;

use cabinetos::plugin::host;

/// The folders it may watch, as `activate` got them.
static ROOTS: OnceLock<Vec<String>> = OnceLock::new();

struct Watcher;

impl Guest for Watcher {
    fn info() -> PluginInfo {
        PluginInfo {
            id: "watcher".to_owned(),
            name: "Watcher".to_owned(),
            version: env!("CARGO_PKG_VERSION").to_owned(),
            api_version: "0.2.0".to_owned(),
        }
    }

    fn activate(ctx: Activation) -> Result<(), String> {
        let _ = ROOTS.set(ctx.watch_roots);
        host::register_command("watcher.watch", "Watch a Folder", "Watcher", &[]);
        host::register_command("watcher.unwatch", "Stop Watching a Folder", "Watcher", &[]);
        Ok(())
    }

    fn deactivate() {}

    fn on_command(id: String, args: String) -> Result<String, String> {
        let args: serde_json::Value =
            serde_json::from_str(&args).map_err(|error| format!("args are not JSON: {error}"))?;
        let path = args["path"].as_str().ok_or("args need a \"path\"")?;
        match id.as_str() {
            "watcher.watch" => {
                host::watch_folder(path)?;
                Ok(serde_json::json!({ "roots": ROOTS.get() }).to_string())
            }
            "watcher.unwatch" => {
                host::unwatch_folder(path);
                Ok("{}".to_owned())
            }
            other => Err(format!("Watcher has no command {other}")),
        }
    }

    fn before_job(_job: JobSummary) -> JobVerdict {
        JobVerdict::Allow
    }

    fn on_listing_opened(_path: String, _entry_count: u32) {}

    fn on_event(name: String, payload: String) {
        host::emit(&name, &payload);
    }
}

export!(Watcher);
