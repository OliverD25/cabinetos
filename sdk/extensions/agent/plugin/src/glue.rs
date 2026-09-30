//! The WebAssembly side: the component's exports, and the [`Host`] trait
//! bound to the core's host functions and the sandbox's files. Built only
//! for `wasm32`.

wit_bindgen::generate!({
    path: "../../../wit",
    world: "core-plugin",
});

use std::cell::RefCell;
use std::io::Write;
use std::time::{SystemTime, UNIX_EPOCH};

use cabinetos::plugin::host as raw;
use serde_json::Value;

use crate::agent::Agent;
use crate::handler::{self, COMMANDS};
use crate::host::{DirEntry, Host, HttpRequest, HttpResponse, Level};
use crate::paths;

/// The plugin's own folder inside the sandbox.
const DATA: &str = "/data";

/// The core's host functions and the sandbox's files.
struct WasmHost;

fn millis(time: SystemTime) -> Option<u64> {
    time.duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|since| u64::try_from(since.as_millis()).ok())
}

impl Host for WasmHost {
    fn log(&self, level: Level, message: &str) {
        raw::log(
            match level {
                Level::Debug => raw::LogLevel::Debug,
                Level::Info => raw::LogLevel::Info,
                Level::Warn => raw::LogLevel::Warn,
                Level::Error => raw::LogLevel::Error,
            },
            message,
        );
    }

    fn config(&self, path: &str) -> Option<String> {
        raw::config_get(path)
    }

    fn emit(&self, name: &str, payload: &str) {
        raw::emit(name, payload);
    }

    fn http(&self, request: &HttpRequest) -> Result<HttpResponse, String> {
        let answer = raw::http_request(&raw::WebRequest {
            method: request.method.to_owned(),
            url: request.url.clone(),
            headers: request.headers.clone(),
            body: (!request.body.is_empty()).then(|| request.body.clone()),
            secret: request.secret.clone(),
            secret_header: request.secret_header.clone(),
            timeout_ms: request.timeout_ms,
        })?;
        Ok(HttpResponse {
            status: answer.status,
            body: answer.body,
        })
    }

    fn core_request(&self, request: &Value) -> Result<Value, String> {
        let reply = raw::core_request(&request.to_string())?;
        serde_json::from_str(&reply)
            .map_err(|error| format!("the core's reply is not JSON: {error}"))
    }

    fn read_dir(&self, path: &str) -> Result<Vec<DirEntry>, String> {
        let guest = paths::to_guest(path);
        let entries = std::fs::read_dir(&guest).map_err(|error| format!("{path}: {error}"))?;
        Ok(entries
            .filter_map(Result::ok)
            .map(|entry| {
                let metadata = entry.metadata().ok();
                DirEntry {
                    name: entry.file_name().to_string_lossy().into_owned(),
                    is_dir: metadata.as_ref().is_some_and(std::fs::Metadata::is_dir),
                    size: metadata.as_ref().map_or(0, std::fs::Metadata::len),
                    modified_ms: metadata
                        .and_then(|metadata| metadata.modified().ok())
                        .and_then(millis),
                }
            })
            .collect())
    }

    fn read_data(&self, name: &str) -> Result<Option<Vec<u8>>, String> {
        match std::fs::read(format!("{DATA}/{name}")) {
            Ok(bytes) => Ok(Some(bytes)),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(error) => Err(format!("{name}: {error}")),
        }
    }

    fn write_data(&self, name: &str, bytes: &[u8]) -> Result<(), String> {
        std::fs::write(format!("{DATA}/{name}"), bytes).map_err(|error| format!("{name}: {error}"))
    }

    fn append_data(&self, name: &str, bytes: &[u8]) -> Result<(), String> {
        std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(format!("{DATA}/{name}"))
            .and_then(|mut file| file.write_all(bytes))
            .map_err(|error| format!("{name}: {error}"))
    }

    fn list_data(&self) -> Result<Vec<String>, String> {
        let entries = std::fs::read_dir(DATA).map_err(|error| format!("{DATA}: {error}"))?;
        Ok(entries
            .filter_map(Result::ok)
            .map(|entry| entry.file_name().to_string_lossy().into_owned())
            .collect())
    }

    fn now_ms(&self) -> u64 {
        millis(SystemTime::now()).unwrap_or(0)
    }

    fn watch(&self, path: &str) -> Result<(), String> {
        raw::watch_folder(path)
    }

    fn unwatch(&self, path: &str) {
        raw::unwatch_folder(path);
    }
}

thread_local! {
    /// The plugin's state; one thread runs the instance.
    static AGENT: RefCell<Option<Agent>> = const { RefCell::new(None) };
}

struct AgentPlugin;

impl Guest for AgentPlugin {
    fn info() -> PluginInfo {
        PluginInfo {
            id: "agent".to_owned(),
            name: "CabinetOS Agent".to_owned(),
            version: env!("CARGO_PKG_VERSION").to_owned(),
            api_version: "0.2.0".to_owned(),
        }
    }

    fn activate(ctx: Activation) -> Result<(), String> {
        for command in COMMANDS {
            let keys: Vec<String> = command.keys.iter().map(|key| (*key).to_owned()).collect();
            raw::register_command(command.id, command.title, command.category, &keys);
        }
        let roots = ctx
            .read_roots
            .iter()
            .map(|root| paths::from_guest(root))
            .collect();
        let agent = Agent::new(&WasmHost, roots, ctx.watch_roots);
        AGENT.with(|slot| *slot.borrow_mut() = Some(agent));
        Ok(())
    }

    fn deactivate() {}

    fn on_command(id: String, args: String) -> Result<String, String> {
        AGENT.with(|slot| {
            let mut slot = slot.borrow_mut();
            let agent = slot.as_mut().ok_or("the agent has not started")?;
            handler::on_command(agent, &WasmHost, &id, &args)
        })
    }

    fn before_job(_job: JobSummary) -> JobVerdict {
        JobVerdict::Allow
    }

    fn on_listing_opened(_path: String, _entry_count: u32) {}

    fn on_event(name: String, payload: String) {
        AGENT.with(|slot| {
            if let Some(agent) = slot.borrow_mut().as_mut() {
                handler::on_event(agent, &WasmHost, &name, &payload);
            }
        });
    }
}

export!(AgentPlugin);
