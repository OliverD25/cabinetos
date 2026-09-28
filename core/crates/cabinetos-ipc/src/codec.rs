//! JSON messages over frames (ADR 0006).

use serde::Serialize;
use serde::de::DeserializeOwned;
use tokio::io::{AsyncRead, AsyncWrite};

use crate::IpcError;
use crate::frame::{read_frame, write_frame};

/// Serializes `message` as JSON and writes it as one frame.
pub async fn send<W, T>(writer: &mut W, message: &T) -> Result<(), IpcError>
where
    W: AsyncWrite + Unpin + ?Sized,
    T: Serialize + ?Sized,
{
    let payload =
        serde_json::to_vec(message).map_err(|error| IpcError::Protocol(error.to_string()))?;
    write_frame(writer, &payload).await
}

/// Reads one frame and parses it as JSON. A frame that is not a valid `T` is
/// an [`IpcError::Protocol`].
pub async fn recv<R, T>(reader: &mut R) -> Result<T, IpcError>
where
    R: AsyncRead + Unpin + ?Sized,
    T: DeserializeOwned,
{
    let payload = read_frame(reader).await?;
    serde_json::from_slice(&payload).map_err(|error| IpcError::Protocol(error.to_string()))
}

#[cfg(test)]
mod tests {
    use cabinetos_protocol::{Envelope, Request, RequestId};

    use super::*;

    #[tokio::test]
    async fn round_trips_an_envelope() {
        let sent = Envelope::new(RequestId::new(), Request::Ping);
        let mut wire = Vec::new();
        send(&mut wire, &sent).await.unwrap();
        let received: Envelope<Request> = recv(&mut wire.as_slice()).await.unwrap();
        assert_eq!(received, sent);
    }

    #[tokio::test]
    async fn invalid_json_is_a_protocol_error() {
        let mut wire = Vec::new();
        crate::frame::write_frame(&mut wire, b"{not json")
            .await
            .unwrap();
        let result: Result<Envelope<Request>, _> = recv(&mut wire.as_slice()).await;
        assert!(matches!(result, Err(IpcError::Protocol(_))));
    }
}
