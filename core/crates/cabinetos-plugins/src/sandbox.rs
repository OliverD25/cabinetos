//! What a plugin's WebAssembly instance may touch: its WASI context (clocks
//! and random numbers; folders only as its capabilities grant them; no
//! network, no environment variables, no arguments) and its memory limit.
//! Its stdout and stderr go into the core's log, one line at a time.

use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::sync::{Arc, Mutex, PoisonError};
use std::task::{Context, Poll};

use wasmtime::ResourceLimiter;
use wasmtime_wasi::cli::{IsTerminal, StdoutStream};
use wasmtime_wasi::p2::{OutputStream, Pollable, StreamResult};
use wasmtime_wasi::{FsPerms, WasiCtx, WasiCtxBuilder};

/// The guest path of a plugin's own folder.
pub(crate) const DATA_DIR: &str = "/data";

/// The longest line kept before it is logged anyway.
const MAX_LINE: usize = 16 * 1024;

/// The guest path of a host folder: `C:\a\b` is `/C:/a/b`.
pub(crate) fn guest_path(host: &Path) -> String {
    format!("/{}", host.display().to_string().replace('\\', "/"))
}

/// The folders a plugin's instance sees.
pub(crate) struct Mounts {
    pub(crate) data_dir: PathBuf,
    pub(crate) read: Vec<PathBuf>,
    pub(crate) write: Vec<PathBuf>,
}

/// Builds the WASI context of one instance.
pub(crate) fn context(
    mounts: &Mounts,
    stdout: LineLog,
    stderr: LineLog,
) -> Result<WasiCtx, String> {
    let mut builder = WasiCtxBuilder::new();
    builder
        .stdout(stdout)
        .stderr(stderr)
        // The instance has a thread of its own: file calls may block it.
        .allow_blocking_current_thread(true)
        .allow_tcp(false)
        .allow_udp(false)
        .allow_ip_name_lookup(false);
    builder
        .preopened_dir(&mounts.data_dir, DATA_DIR, FsPerms::ReadWrite)
        .map_err(|error| format!("cannot open {}: {error}", mounts.data_dir.display()))?;
    for root in &mounts.write {
        builder
            .preopened_dir(root, guest_path(root), FsPerms::ReadWrite)
            .map_err(|error| format!("cannot open {}: {error}", root.display()))?;
    }
    for root in mounts
        .read
        .iter()
        .filter(|root| !mounts.write.contains(root))
    {
        builder
            .preopened_dir(root, guest_path(root), FsPerms::ReadOnly)
            .map_err(|error| format!("cannot open {}: {error}", root.display()))?;
    }
    Ok(builder.build())
}

/// Where a plugin's output line goes.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Stream {
    Stdout,
    Stderr,
}

/// A plugin's stdout or stderr: each line becomes a log event (inside the
/// plugin's span, so it carries its `plugin_id`). The last stderr line is
/// kept for the crash message: a Rust panic prints its reason there, as
/// `thread '..' (<id>) panicked at <place>:` and then the message (older
/// Rust leaves out the thread's ID).
#[derive(Clone)]
pub(crate) struct LineLog {
    stream: Stream,
    pending: Arc<Mutex<Vec<u8>>>,
    last_error: Arc<Mutex<Option<String>>>,
    /// `panicked at <place>:` from the line before, waiting for its message.
    panic_at: Arc<Mutex<Option<String>>>,
}

impl LineLog {
    pub(crate) fn new(stream: Stream, last_error: Arc<Mutex<Option<String>>>) -> Self {
        Self {
            stream,
            pending: Arc::new(Mutex::new(Vec::new())),
            last_error,
            panic_at: Arc::new(Mutex::new(None)),
        }
    }

    fn take(&self, bytes: &[u8]) {
        let mut pending = self.pending.lock().unwrap_or_else(PoisonError::into_inner);
        pending.extend_from_slice(bytes);
        while let Some(end) = pending.iter().position(|byte| *byte == b'\n') {
            let line: Vec<u8> = pending.drain(..=end).collect();
            self.log(&line);
        }
        if pending.len() > MAX_LINE {
            let line = std::mem::take(&mut *pending);
            self.log(&line);
        }
    }

    fn flush_pending(&self) {
        let line =
            std::mem::take(&mut *self.pending.lock().unwrap_or_else(PoisonError::into_inner));
        if !line.is_empty() {
            self.log(&line);
        }
    }

    fn log(&self, line: &[u8]) {
        let text = String::from_utf8_lossy(line);
        let text = text.trim_end_matches(['\r', '\n']);
        if text.is_empty() {
            return;
        }
        match self.stream {
            Stream::Stdout => tracing::info!(stream = "stdout", "{text}"),
            Stream::Stderr => {
                tracing::warn!(stream = "stderr", "{text}");
                self.remember(text);
            }
        }
    }

    fn remember(&self, text: &str) {
        // Rust's hint about RUST_BACKTRACE says nothing about the crash.
        if text.starts_with("note: ") {
            return;
        }
        let mut panic_at = self.panic_at.lock().unwrap_or_else(PoisonError::into_inner);
        if let Some(place) = text
            .strip_prefix("thread '")
            .and_then(|rest| rest.find(" panicked at ").map(|start| &rest[start + 1..]))
        {
            *panic_at = Some(place.to_owned());
            return;
        }
        let said = match panic_at.take() {
            Some(place) => format!("{place} {text}"),
            None => text.to_owned(),
        };
        *self
            .last_error
            .lock()
            .unwrap_or_else(PoisonError::into_inner) = Some(said);
    }
}

impl IsTerminal for LineLog {
    fn is_terminal(&self) -> bool {
        false
    }
}

impl StdoutStream for LineLog {
    fn p2_stream(&self) -> Box<dyn OutputStream> {
        Box::new(self.clone())
    }

    fn async_stream(&self) -> Box<dyn tokio::io::AsyncWrite + Send + Sync> {
        Box::new(self.clone())
    }
}

impl OutputStream for LineLog {
    fn write(&mut self, bytes: bytes::Bytes) -> StreamResult<()> {
        self.take(&bytes);
        Ok(())
    }

    fn flush(&mut self) -> StreamResult<()> {
        Ok(())
    }

    fn check_write(&mut self) -> StreamResult<usize> {
        Ok(MAX_LINE)
    }
}

#[wasmtime_wasi::async_trait]
impl Pollable for LineLog {
    async fn ready(&mut self) {}
}

impl tokio::io::AsyncWrite for LineLog {
    fn poll_write(
        self: Pin<&mut Self>,
        _: &mut Context<'_>,
        bytes: &[u8],
    ) -> Poll<std::io::Result<usize>> {
        self.take(bytes);
        Poll::Ready(Ok(bytes.len()))
    }

    fn poll_flush(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        Poll::Ready(Ok(()))
    }

    fn poll_shutdown(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        self.flush_pending();
        Poll::Ready(Ok(()))
    }
}

impl Drop for LineLog {
    fn drop(&mut self) {
        // The last owner logs what is left of an unfinished line.
        if Arc::strong_count(&self.pending) == 1 {
            self.flush_pending();
        }
    }
}

/// A plugin's resource limits: linear memory in bytes, and how many core
/// instances, tables and memories its component may create.
pub(crate) struct Limiter {
    pub(crate) memory_bytes: usize,
}

/// Core instances, tables and memories one component may create: a Rust
/// component has a few of each (its module and the canonical ABI glue).
const MAX_PARTS: usize = 32;

/// The most elements one table may have.
const MAX_TABLE_ELEMENTS: usize = 100_000;

impl ResourceLimiter for Limiter {
    fn memory_growing(
        &mut self,
        _current: usize,
        desired: usize,
        _maximum: Option<usize>,
    ) -> wasmtime::Result<bool> {
        if desired > self.memory_bytes {
            // An error, not `false`: the trap names the limit, which a
            // failed `memory.grow` and the guest's own abort would not.
            return Err(wasmtime::format_err!(
                "the plugin asked for {} MiB of memory; it may use {} MiB",
                desired.div_ceil(1024 * 1024),
                self.memory_bytes / (1024 * 1024)
            ));
        }
        Ok(true)
    }

    fn table_growing(
        &mut self,
        _current: usize,
        desired: usize,
        _maximum: Option<usize>,
    ) -> wasmtime::Result<bool> {
        Ok(desired <= MAX_TABLE_ELEMENTS)
    }

    fn instances(&self) -> usize {
        MAX_PARTS
    }

    fn tables(&self) -> usize {
        MAX_PARTS
    }

    fn memories(&self) -> usize {
        MAX_PARTS
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guest_paths_use_forward_slashes_under_a_root() {
        assert_eq!(
            guest_path(Path::new(r"C:\Users\me\root")),
            "/C:/Users/me/root"
        );
    }

    #[test]
    fn output_is_split_into_lines_and_stderr_is_remembered() {
        let last = Arc::new(Mutex::new(None));
        let log = LineLog::new(Stream::Stderr, Arc::clone(&last));
        log.take(b"first\nsecond ");
        assert_eq!(last.lock().unwrap().as_deref(), Some("first"));
        log.take(b"half\n");
        assert_eq!(last.lock().unwrap().as_deref(), Some("second half"));
        log.take(b"no newline");
        drop(log);
        assert_eq!(last.lock().unwrap().as_deref(), Some("no newline"));
    }

    #[test]
    fn a_panic_is_remembered_with_its_place() {
        // Rust 1.98 prints the thread's ID; older versions do not.
        for header in [
            "thread '<unnamed>' (1) panicked at crashy\\src\\lib.rs:32:9:",
            "thread '<unnamed>' panicked at crashy\\src\\lib.rs:32:9:",
        ] {
            let last = Arc::new(Mutex::new(None));
            let log = LineLog::new(Stream::Stderr, Arc::clone(&last));
            log.take(format!("{header}\ncrashy was asked to crash\n").as_bytes());
            log.take(
                b"note: run with `RUST_BACKTRACE=1` environment variable to display a backtrace\n",
            );
            assert_eq!(
                last.lock().unwrap().as_deref(),
                Some("panicked at crashy\\src\\lib.rs:32:9: crashy was asked to crash"),
                "{header}"
            );
        }
    }

    #[test]
    fn memory_beyond_the_limit_is_refused_with_a_reason() {
        let mut limiter = Limiter {
            memory_bytes: 256 * 1024 * 1024,
        };
        assert!(limiter.memory_growing(0, 1024 * 1024, None).unwrap());
        let refused = limiter
            .memory_growing(0, 1024 * 1024 * 1024, None)
            .unwrap_err();
        assert!(refused.to_string().contains("1024 MiB"), "{refused}");
    }
}
