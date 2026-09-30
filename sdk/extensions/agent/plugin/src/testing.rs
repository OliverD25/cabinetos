//! A host for tests: files in memory, a core that answers what a test
//! scripts (and something sensible otherwise), a clock that stands still.

use std::cell::{Cell, RefCell};
use std::collections::{BTreeMap, VecDeque};

use serde_json::{Value, json};

use crate::host::{DirEntry, Host, HttpRequest, HttpResponse, Level};

/// Everything the plugin did to its host is written down here.
pub struct FakeHost {
    /// The plugin's own folder.
    pub files: RefCell<BTreeMap<String, Vec<u8>>>,
    /// Answers of `config-get`, by dotted path.
    pub config: RefCell<BTreeMap<String, String>>,
    /// Folders the plugin may read, by Windows path.
    pub folders: RefCell<BTreeMap<String, Vec<DirEntry>>>,
    /// Events the plugin sent: name and payload.
    pub emitted: RefCell<Vec<(String, Value)>>,
    /// Requests the plugin sent the core.
    pub requests: RefCell<Vec<Value>>,
    /// Scripted replies by request type, used in order; a type with none
    /// left gets the default reply.
    pub script: RefCell<BTreeMap<String, VecDeque<Result<Value, String>>>>,
    /// Web requests the plugin made, and the answers to give.
    pub http_calls: RefCell<Vec<HttpRequest>>,
    pub http_answers: RefCell<VecDeque<Result<HttpResponse, String>>>,
    pub now: Cell<u64>,
    pub watched: RefCell<Vec<String>>,
    /// Folders the core refuses to let the plugin watch.
    pub unwatchable: RefCell<Vec<String>>,
    pub logs: RefCell<Vec<(Level, String)>>,
    preview_number: Cell<u64>,
}

impl FakeHost {
    #[must_use]
    pub fn new() -> Self {
        Self {
            files: RefCell::default(),
            config: RefCell::default(),
            folders: RefCell::default(),
            emitted: RefCell::default(),
            requests: RefCell::default(),
            script: RefCell::default(),
            http_calls: RefCell::default(),
            http_answers: RefCell::default(),
            // 2026-09-30 01:02:03 UTC.
            now: Cell::new(1_790_730_123_000),
            watched: RefCell::default(),
            unwatchable: RefCell::default(),
            logs: RefCell::default(),
            preview_number: Cell::new(0),
        }
    }

    /// Sets what `config-get` answers for `path`.
    pub fn set_config(&self, path: &str, value: &Value) {
        self.config
            .borrow_mut()
            .insert(path.to_owned(), value.to_string());
    }

    /// Sets the agent's settings (`plugins.agent.settings`).
    pub fn set_settings(&self, settings: &Value) {
        self.set_config("plugins.agent.settings", settings);
    }

    /// Queues a reply for the next request of `kind`.
    pub fn script_reply(&self, kind: &str, reply: Result<Value, String>) {
        self.script
            .borrow_mut()
            .entry(kind.to_owned())
            .or_default()
            .push_back(reply);
    }

    /// The requests of one type the plugin sent.
    #[must_use]
    pub fn requests_of(&self, kind: &str) -> Vec<Value> {
        self.requests
            .borrow()
            .iter()
            .filter(|request| request["type"] == kind)
            .cloned()
            .collect()
    }

    /// The payloads of the events of one name.
    #[must_use]
    pub fn events_named(&self, name: &str) -> Vec<Value> {
        self.emitted
            .borrow()
            .iter()
            .filter(|(event, _)| event == name)
            .map(|(_, payload)| payload.clone())
            .collect()
    }

    /// Puts a file in the plugin's folder.
    pub fn put_file(&self, name: &str, text: &str) {
        self.files
            .borrow_mut()
            .insert(name.to_owned(), text.as_bytes().to_vec());
    }

    /// Lists a folder as the plugin may read it.
    pub fn put_folder(&self, path: &str, entries: Vec<DirEntry>) {
        self.folders.borrow_mut().insert(path.to_owned(), entries);
    }

    fn default_reply(&self, request: &Value) -> Result<Value, String> {
        Ok(match request["type"].as_str().unwrap_or_default() {
            "get_window_state" => {
                json!({ "type": "error", "code": "no_window", "message": "no window has said what it shows" })
            }
            "preview_listing" => {
                let number = self.preview_number.get() + 1;
                self.preview_number.set(number);
                json!({
                    "type": "preview_opened",
                    "preview": format!("preview-{number}"),
                    "title": request["title"],
                    "listing": { "listing_id": number, "section_handle": 0, "section_size": 100,
                                 "entry_count": request["rows"].as_array().map_or(0, Vec::len),
                                 "generation": 1, "elapsed_us": 5 },
                })
            }
            "preview_apply" => json!({ "type": "jobs_started", "jobs": [11, 12] }),
            "preview_cancel" => json!({ "type": "ok" }),
            "undo_job" => json!({ "type": "undo_started", "job_id": 40, "undoes": 12, "left": [] }),
            "search" => json!({ "type": "file_search_results", "hits": [], "source": "walk",
                                "took_us": 10, "complete": true }),
            other => return Err(format!("the fake core has no reply for {other}")),
        })
    }
}

impl Default for FakeHost {
    fn default() -> Self {
        Self::new()
    }
}

impl Host for FakeHost {
    fn log(&self, level: Level, message: &str) {
        self.logs.borrow_mut().push((level, message.to_owned()));
    }

    fn config(&self, path: &str) -> Option<String> {
        self.config.borrow().get(path).cloned()
    }

    fn emit(&self, name: &str, payload: &str) {
        let payload = serde_json::from_str(payload).unwrap_or(Value::Null);
        self.emitted.borrow_mut().push((name.to_owned(), payload));
    }

    fn http(&self, request: &HttpRequest) -> Result<HttpResponse, String> {
        self.http_calls.borrow_mut().push(request.clone());
        self.http_answers
            .borrow_mut()
            .pop_front()
            .unwrap_or_else(|| Err("the fake web has no answer".to_owned()))
    }

    fn core_request(&self, request: &Value) -> Result<Value, String> {
        self.requests.borrow_mut().push(request.clone());
        let kind = request["type"].as_str().unwrap_or_default().to_owned();
        let scripted = self
            .script
            .borrow_mut()
            .get_mut(&kind)
            .and_then(VecDeque::pop_front);
        scripted.unwrap_or_else(|| self.default_reply(request))
    }

    fn read_dir(&self, path: &str) -> Result<Vec<DirEntry>, String> {
        self.folders
            .borrow()
            .get(path)
            .cloned()
            .ok_or_else(|| format!("{path}: no such folder"))
    }

    fn read_data(&self, name: &str) -> Result<Option<Vec<u8>>, String> {
        Ok(self.files.borrow().get(name).cloned())
    }

    fn write_data(&self, name: &str, bytes: &[u8]) -> Result<(), String> {
        self.files
            .borrow_mut()
            .insert(name.to_owned(), bytes.to_vec());
        Ok(())
    }

    fn append_data(&self, name: &str, bytes: &[u8]) -> Result<(), String> {
        self.files
            .borrow_mut()
            .entry(name.to_owned())
            .or_default()
            .extend_from_slice(bytes);
        Ok(())
    }

    fn list_data(&self) -> Result<Vec<String>, String> {
        Ok(self.files.borrow().keys().cloned().collect())
    }

    fn now_ms(&self) -> u64 {
        self.now.get()
    }

    fn watch(&self, path: &str) -> Result<(), String> {
        if self.unwatchable.borrow().iter().any(|known| known == path) {
            return Err(format!("{path} is outside the roots the plugin may watch"));
        }
        let mut watched = self.watched.borrow_mut();
        if !watched.iter().any(|known| known == path) {
            watched.push(path.to_owned());
        }
        Ok(())
    }

    fn unwatch(&self, path: &str) {
        self.watched.borrow_mut().retain(|known| known != path);
    }
}
