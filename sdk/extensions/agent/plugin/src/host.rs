//! What the plugin needs from its host, as a trait. The WebAssembly build
//! implements it with the core's host functions and the sandbox's files
//! (`glue.rs`); the tests implement it with a fake, so the whole agent runs
//! as an ordinary Rust test.

use serde_json::Value;

/// How serious a log line is.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Level {
    Debug,
    Info,
    Warn,
    Error,
}

/// A web request, as `http-request` takes it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HttpRequest {
    pub method: &'static str,
    pub url: String,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
    /// A stored secret the core puts into `secret_header`; the plugin
    /// never sees its value.
    pub secret: Option<String>,
    pub secret_header: Option<String>,
    /// 0: the longest the core allows.
    pub timeout_ms: u32,
}

/// The answer: a server's error status is an answer too.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HttpResponse {
    pub status: u16,
    pub body: Vec<u8>,
}

/// One entry of a folder.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DirEntry {
    pub name: String,
    pub is_dir: bool,
    pub size: u64,
    /// Milliseconds since 1970 (UTC), when the file system says.
    pub modified_ms: Option<u64>,
}

/// The host of one plugin instance.
pub trait Host {
    /// Writes a line to the core's log, marked with the plugin's id.
    fn log(&self, level: Level, message: &str);
    /// A setting of `cabinetos.json` as JSON text, by dotted path.
    fn config(&self, path: &str) -> Option<String>;
    /// Sends an event to every client (`plugin_event`); `payload` is JSON.
    fn emit(&self, name: &str, payload: &str);
    /// Makes one web request through the core.
    fn http(&self, request: &HttpRequest) -> Result<HttpResponse, String>;
    /// Sends the core one request (`core-request`) and returns its reply.
    fn core_request(&self, request: &Value) -> Result<Value, String>;
    /// The entries of a folder, by its Windows path, under the folders the
    /// plugin may read.
    fn read_dir(&self, path: &str) -> Result<Vec<DirEntry>, String>;
    /// A file of the plugin's own folder; `None` when it is not there.
    fn read_data(&self, name: &str) -> Result<Option<Vec<u8>>, String>;
    /// Writes a file of the plugin's own folder, replacing it.
    fn write_data(&self, name: &str, bytes: &[u8]) -> Result<(), String>;
    /// Adds bytes to the end of a file of the plugin's own folder, which is
    /// made when it is missing.
    fn append_data(&self, name: &str, bytes: &[u8]) -> Result<(), String>;
    /// The names of the files in the plugin's own folder.
    fn list_data(&self) -> Result<Vec<String>, String>;
    /// Milliseconds since 1970 (UTC).
    fn now_ms(&self) -> u64;
    /// Starts hearing about changes in a folder (`watch-folder`).
    fn watch(&self, path: &str) -> Result<(), String>;
    /// Stops hearing about a folder.
    fn unwatch(&self, path: &str);
}
