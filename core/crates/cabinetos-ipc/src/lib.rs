//! Windows transport between the CabinetOS processes.
//!
//! - **Control channel:** a named pipe that only the current user can open
//!   ([`PipeServer`], [`PipeClient`]), carrying length-prefixed frames
//!   ([`frame`]) of JSON ([`codec`]), as ADR 0006 specifies.
//! - **Data channel:** page-file-backed shared-memory sections
//!   ([`SharedSection`]) whose handles are duplicated into the reading process.
//! - **Lifetime:** [`process::watch_process_exit`] tells the core when its
//!   parent (the UI) has gone.
//!
//! Serves Constitution Article 1 (Zero-Compromise Performance: asynchronous
//! pipe I/O never blocks a caller, and bulk data moves through shared memory
//! with no copy) and Article 12 (request IDs travel with every frame). Brief §4.
//!
//! Unsafe code is limited to the three modules that call Windows APIs
//! directly (`security`, `shm`, `process`). Every unsafe block states its
//! invariant in a `SAFETY:` comment.

pub mod codec;
pub mod frame;
mod pipe;
#[allow(unsafe_code)]
pub mod process;
#[allow(unsafe_code)]
mod security;
#[allow(unsafe_code)]
mod shm;

use std::time::Duration;

pub use frame::MAX_FRAME;
pub use pipe::{PipeClient, PipeConnection, PipeName, PipeServer};
pub use shm::{MappedView, RawHandleValue, SharedSection};

/// Everything that can go wrong on the transport.
#[derive(Debug, thiserror::Error)]
pub enum IpcError {
    // The two wrappers below are transparent: their message is the OS
    // message, so a caller that prints the whole cause chain shows it once.
    /// Reading, writing or opening the pipe failed.
    #[error(transparent)]
    Io(#[from] std::io::Error),
    /// A Windows API call failed.
    #[error(transparent)]
    Windows(#[from] windows::core::Error),
    /// A frame was longer than [`MAX_FRAME`]. The connection cannot be
    /// resynchronized after this and must be closed.
    #[error("frame of {len} bytes is larger than the {max}-byte limit")]
    FrameTooLarge {
        /// The length the frame announced.
        len: usize,
        /// The limit, [`MAX_FRAME`].
        max: usize,
    },
    /// The peer sent something that is not a valid message, or a reply did
    /// not match its request.
    #[error("protocol error: {0}")]
    Protocol(String),
    /// The peer closed the connection between two frames.
    #[error("the other side closed the connection")]
    Closed,
    /// The operation did not finish in time.
    #[error("timed out after {0:?}")]
    Timeout(Duration),
}
