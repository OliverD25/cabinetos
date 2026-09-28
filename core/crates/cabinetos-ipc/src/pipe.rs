//! The control-channel pipe: server, connections and client.

use std::fmt;
use std::time::Duration;

use cabinetos_protocol::{Envelope, Request, RequestId, Response};
use serde::Serialize;
use serde::de::DeserializeOwned;
use tokio::net::windows::named_pipe::{
    ClientOptions, NamedPipeClient, NamedPipeServer, ServerOptions,
};
use tokio::time::Instant;
use windows::Win32::Foundation::ERROR_PIPE_BUSY;

use crate::IpcError;
use crate::security::UserOnlySecurity;
use crate::{codec, frame};

/// Name prefix of every core pipe.
const PREFIX: &str = r"\\.\pipe\cabinetos-core-";

/// How long a client waits before retrying a busy pipe.
const BUSY_RETRY: Duration = Duration::from_millis(20);

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
    pub async fn accept(&mut self) -> Result<PipeConnection, IpcError> {
        self.next.connect().await?;
        let next = self
            .security
            .create_pipe(&server_options(false), self.name.as_str())?;
        let connected = std::mem::replace(&mut self.next, next);
        Ok(PipeConnection { pipe: connected })
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
}

/// A client of the core's pipe: the CLI now, the UI's model later.
#[derive(Debug)]
pub struct PipeClient {
    pipe: NamedPipeClient,
}

impl PipeClient {
    /// Connects to the pipe, retrying while every instance is busy
    /// (`ERROR_PIPE_BUSY`) until `timeout` has passed. A pipe that does not
    /// exist fails at once with an I/O error of kind `NotFound`.
    pub async fn connect(name: &PipeName, timeout: Duration) -> Result<Self, IpcError> {
        let deadline = Instant::now() + timeout;
        loop {
            match ClientOptions::new().open(name.as_str()) {
                Ok(pipe) => return Ok(Self { pipe }),
                Err(error) if is_pipe_busy(&error) => {
                    if Instant::now() >= deadline {
                        return Err(IpcError::Timeout(timeout));
                    }
                    tokio::time::sleep(BUSY_RETRY).await;
                }
                Err(error) => return Err(error.into()),
            }
        }
    }

    /// Sends `request` with a fresh [`RequestId`] and waits for the reply.
    pub async fn request(&mut self, request: Request) -> Result<Envelope<Response>, IpcError> {
        self.request_with_id(RequestId::new(), request).await
    }

    /// Sends `request` with the given ID and waits for the reply. A reply
    /// that carries a different ID is an [`IpcError::Protocol`].
    pub async fn request_with_id(
        &mut self,
        id: RequestId,
        request: Request,
    ) -> Result<Envelope<Response>, IpcError> {
        codec::send(&mut self.pipe, &Envelope::new(id.clone(), request)).await?;
        let reply: Envelope<Response> = codec::recv(&mut self.pipe).await?;
        if reply.id != id {
            return Err(IpcError::Protocol(format!(
                "reply carries ID {} but the request had ID {id}",
                reply.id
            )));
        }
        Ok(reply)
    }
}

fn is_pipe_busy(error: &std::io::Error) -> bool {
    error
        .raw_os_error()
        .is_some_and(|code| code.cast_unsigned() == ERROR_PIPE_BUSY.0)
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
