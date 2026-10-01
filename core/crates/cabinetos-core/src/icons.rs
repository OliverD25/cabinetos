//! Icons drawn before the window asks for them (the speed review, finding 9).
//!
//! The shell draws one icon at a time, and a new core has none drawn, so a
//! window that shows its first folder asks for six or seven kinds of icon and
//! waits for them one after the other. The core draws them ahead, on its
//! blocking threads and never on the request path: `folder` and `generic`
//! right after it starts, and after each `list_directory` reply the kinds of
//! that listing's entries that are not drawn yet. A `get_icon` that comes
//! meanwhile still works: it finds the PNG, or goes first for the lock
//! ([`Hydrator::draw_ahead`]).

use std::sync::Arc;
use std::time::Instant;

use cabinetos_fs::Hydrator;
use cabinetos_ipc::SharedSection;
use tokio_util::sync::CancellationToken;

/// What every window asks for first: a folder's icon and a file's without an
/// extension.
const START_KEYS: [&str; 2] = ["folder", "generic"];

/// Draws `folder` and `generic` now, on a blocking thread, while the window
/// builds itself.
pub(crate) fn at_start(hydrator: &Arc<Hydrator>, stop: &CancellationToken) {
    spawn(hydrator, stop, "core start", || {
        START_KEYS.map(str::to_owned).to_vec()
    });
}

/// Draws the icons of the kinds in `section`, a listing's entries, that are
/// not drawn yet, on a blocking thread. The caller has sent the listing's
/// reply already, and does not wait for this.
pub(crate) fn for_listing(
    hydrator: &Arc<Hydrator>,
    stop: &CancellationToken,
    section: Arc<SharedSection>,
) {
    spawn(hydrator, stop, "listing", move || {
        // Mapped on this thread, as `describe_entries` does.
        section
            .map_readonly()
            .ok()
            .and_then(|view| Hydrator::icon_keys_of(view.as_slice()).ok())
            .unwrap_or_default()
    });
}

/// Runs one batch on the blocking pool inside the current span, so that its
/// log line belongs to the action that caused it. Nobody waits for it.
fn spawn(
    hydrator: &Arc<Hydrator>,
    stop: &CancellationToken,
    why: &'static str,
    keys_of: impl FnOnce() -> Vec<String> + Send + 'static,
) {
    let hydrator = Arc::clone(hydrator);
    let stop = stop.clone();
    let span = tracing::Span::current();
    drop(tokio::task::spawn_blocking(move || {
        span.in_scope(|| {
            let started = Instant::now();
            let keys = keys_of();
            if keys.is_empty() {
                return;
            }
            let done = hydrator.draw_ahead(&keys, || stop.is_cancelled());
            // A batch that found everything drawn already says nothing.
            if done.drawn + done.failed > 0 {
                tracing::debug!(
                    why,
                    keys = %keys.join(" "),
                    drawn = done.drawn,
                    cached = done.cached,
                    failed = done.failed,
                    took_ms = started.elapsed().as_secs_f64() * 1000.0,
                    "icons drawn ahead"
                );
            }
        });
    }));
}
