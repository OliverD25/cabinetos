//! The control-channel pipe: its name, the server and its connections.

use std::fmt;

use serde::Serialize;
use serde::de::DeserializeOwned;
use tokio::io::{ReadHalf, WriteHalf};
use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};

use crate::IpcError;
use crate::security::UserOnlySecurity;
use crate::{codec, frame, process};

/// Name prefix of every core pipe.
const PREFIX: &str = r"\\.\pipe\cabinetos-core-";

/// The full name of a core pipe: `\\.\pipe\cabinetos-core-<token>`.
///
/// The UI picks a random token, passes it to the core on the command line and
/// connects to it; nobody else knows the name (PLAN §3, "Security").
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct PipeName(String);

impl PipeName {
    /// The pipe for `token`.
    #[must_use]
    pub fn new(token: &str) -> Self {
        Self(format!("{PREFIX}{token}"))
    }

    /// A pipe with a random 16-hex-digit token.
    #[must_use]
    pub fn random() -> Self {
        Self::new(&format!("{:016x}", rand::random::<u64>()))
    }

    /// The fixed development pipe, token `dev`.
    #[must_use]
    pub fn dev() -> Self {
        Self::new("dev")
    }

    /// The full name, for Windows APIs.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }

    /// The token part of the name.
    #[must_use]
    pub fn token(&self) -> &str {
        &self.0[PREFIX.len()..]
    }
}

impl fmt::Display for PipeName {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

fn server_options(first_instance: bool) -> ServerOptions {
    let mut options = ServerOptions::new();
    // First instance: fail if the name already exists, so no other process
    // can own the pipe the UI is about to trust. Remote clients: no access
    // over the network (PIPE_REJECT_REMOTE_CLIENTS).
    options
        .first_pipe_instance(first_instance)
        .reject_remote_clients(true);
    options
}

/// The core's end of the control channel. It accepts any number of clients,
/// one pipe instance each; only the current user can connect.
#[derive(Debug)]
pub struct PipeServer {
    name: PipeName,
    security: UserOnlySecurity,
    /// The instance the next client will connect to.
    next: NamedPipeServer,
}

impl PipeServer {
    /// Creates the pipe. Fails if the name is already in use. Must run inside
    /// a Tokio runtime.
    pub fn bind(name: &PipeName) -> Result<Self, IpcError> {
        let security = UserOnlySecurity::for_current_user()?;
        let next = security.create_pipe(&server_options(true), name.as_str())?;
        Ok(Self {
            name: name.clone(),
            security,
            next,
        })
    }

    /// The pipe's name.
    #[must_use]
    pub fn name(&self) -> &PipeName {
        &self.name
    }

    /// Waits for the next client. As soon as one connects, a fresh instance
    /// is created for the client after it, so a new client never finds the
    /// pipe missing. Cancel-safe: dropping the future loses no client.
    ///
    /// If connecting fails (for example, a client left before the connection
    /// completed), the instance is replaced as well, so the next call starts
    /// clean and the error concerns only that one client.
    pub async fn accept(&mut self) -> Result<PipeConnection, IpcError> {
        let connected = self.next.connect().await;
        let fresh = self
            .security
            .create_pipe(&server_options(false), self.name.as_str())?;
        let instance = std::mem::replace(&mut self.next, fresh);
        connected?;
        Ok(PipeConnection { pipe: instance })
    }
}

/// One connected client, seen from the server.
#[derive(Debug)]
pub struct PipeConnection {
    pipe: NamedPipeServer,
}

impl PipeConnection {
    /// Reads the next frame; [`IpcError::Closed`] when the client has gone.
    pub async fn read_frame(&mut self) -> Result<Vec<u8>, IpcError> {
        frame::read_frame(&mut self.pipe).await
    }

    /// Writes one frame.
    pub async fn write_frame(&mut self, payload: &[u8]) -> Result<(), IpcError> {
        frame::write_frame(&mut self.pipe, payload).await
    }

    /// Sends one JSON message.
    pub async fn send<T: Serialize + ?Sized>(&mut self, message: &T) -> Result<(), IpcError> {
        codec::send(&mut self.pipe, message).await
    }

    /// Receives one JSON message.
    pub async fn recv<T: DeserializeOwned>(&mut self) -> Result<T, IpcError> {
        codec::recv(&mut self.pipe).await
    }

    /// The ID of the client's process, as Windows knows it (not as the
    /// client claims it).
    pub fn client_process_id(&self) -> Result<u32, IpcError> {
        process::pipe_client_process_id(&self.pipe)
    }

    /// Splits the connection so one task can read requests while another
    /// writes replies and events.
    #[must_use]
    pub fn into_split(self) -> (PipeReader, PipeWriter) {
        let (reader, writer) = tokio::io::split(self.pipe);
        (PipeReader { inner: reader }, PipeWriter { inner: writer })
    }
}

/// The reading half of a [`PipeConnection`].
#[derive(Debug)]
pub struct PipeReader {
    inner: ReadHalf<NamedPipeServer>,
}

impl PipeReader {
    /// Reads the next frame; [`IpcError::Closed`] when the client has gone.
    /// Not cancel-safe: a frame read halfway is lost if the future is dropped.
    pub async fn read_frame(&mut self) -> Result<Vec<u8>, IpcError> {
        frame::read_frame(&mut self.inner).await
    }
}

/// The writing half of a [`PipeConnection`].
#[derive(Debug)]
pub struct PipeWriter {
    inner: WriteHalf<NamedPipeServer>,
}

impl PipeWriter {
    /// Writes one frame.
    pub async fn write_frame(&mut self, payload: &[u8]) -> Result<(), IpcError> {
        frame::write_frame(&mut self.inner, payload).await
    }

    /// Sends one JSON message.
    pub async fn send<T: Serialize + ?Sized>(&mut self, message: &T) -> Result<(), IpcError> {
        codec::send(&mut self.inner, message).await
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_follow_the_documented_pattern() {
        assert_eq!(
            PipeName::new("abc").as_str(),
            r"\\.\pipe\cabinetos-core-abc"
        );
        assert_eq!(PipeName::dev().token(), "dev");
        let random = PipeName::random();
        assert_eq!(random.token().len(), 16);
        assert!(random.token().chars().all(|c| c.is_ascii_hexdigit()));
        assert_ne!(random, PipeName::random());
    }
}
