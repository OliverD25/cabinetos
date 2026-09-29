//! A test fixture for capability gating. `reader.size {"path": ...}`
//! answers the size of a file, which works only under a folder granted by
//! `fs:read`. `reader.write {"path": ..., "text": ...}` tries to write a
//! file, which works only under a folder granted by `fs:write`, and this
//! plugin asks for none.

wit_bindgen::generate!({
    path: "../../../wit",
    world: "core-plugin",
});

use cabinetos::plugin::host::{self, LogLevel};

struct Reader;

/// The path of a Windows path inside the sandbox: `C:\a\b` is `/C:/a/b`.
fn guest_path(path: &str) -> String {
    format!("/{}", path.replace('\\', "/"))
}

fn path_argument(args: &str) -> Result<String, String> {
    let value: serde_json::Value =
        serde_json::from_str(args).map_err(|error| format!("args are not JSON: {error}"))?;
    value["path"]
        .as_str()
        .map(guest_path)
        .ok_or_else(|| "args need a \"path\"".to_owned())
}

impl Guest for Reader {
    fn info() -> PluginInfo {
        PluginInfo {
            id: "reader".to_owned(),
            name: "Reader".to_owned(),
            version: env!("CARGO_PKG_VERSION").to_owned(),
            api_version: "0.2.0".to_owned(),
        }
    }

    fn activate(ctx: Activation) -> Result<(), String> {
        host::register_command("reader.size", "File Size", "Reader", &[]);
        host::register_command("reader.write", "Write a File", "Reader", &[]);
        host::log(LogLevel::Info, &format!("Reader may read {:?}", ctx.read_roots));
        Ok(())
    }

    fn deactivate() {}

    fn on_command(id: String, args: String) -> Result<String, String> {
        let path = path_argument(&args)?;
        match id.as_str() {
            "reader.size" => {
                let metadata = std::fs::metadata(&path).map_err(|error| format!("{path}: {error}"))?;
                Ok(format!(r#"{{"size":{}}}"#, metadata.len()))
            }
            "reader.write" => {
                std::fs::write(&path, "written by Reader").map_err(|error| format!("{path}: {error}"))?;
                Ok(r#"{"written":true}"#.to_owned())
            }
            other => Err(format!("Reader has no command {other}")),
        }
    }

    fn before_job(_job: JobSummary) -> JobVerdict {
        JobVerdict::Allow
    }

    fn on_listing_opened(path: String, entry_count: u32) {
        host::log(LogLevel::Info, &format!("a pane opened {path} with {entry_count} entries"));
    }
}

export!(Reader);
