//! Length-prefixed frames (ADR 0006): a 4-byte little-endian length, then that
//! many bytes of payload.
//!
//! A frame may be at most [`MAX_FRAME`] bytes. A longer announced length is a
//! protocol error that cannot be recovered from, because the reader cannot
//! find the next frame boundary without reading the whole oversized payload:
//! the connection must be closed. Large data belongs in shared memory.

use std::io;

use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

use crate::IpcError;

/// The largest payload a frame may carry: 16 MiB.
pub const MAX_FRAME: usize = 16 * 1024 * 1024;

/// Checks a payload length against [`MAX_FRAME`] and returns it as the
/// on-wire `u32`. Runs before any buffer is allocated.
fn check_len(len: usize) -> Result<u32, IpcError> {
    match u32::try_from(len) {
        Ok(wire_len) if len <= MAX_FRAME => Ok(wire_len),
        _ => Err(IpcError::FrameTooLarge {
            len,
            max: MAX_FRAME,
        }),
    }
}

/// Writes one frame and flushes it.
pub async fn write_frame<W>(writer: &mut W, payload: &[u8]) -> Result<(), IpcError>
where
    W: AsyncWrite + Unpin + ?Sized,
{
    let wire_len = check_len(payload.len())?;
    // One buffer, one write: small control messages should not cost two
    // system calls.
    let mut frame = Vec::with_capacity(4 + payload.len());
    frame.extend_from_slice(&wire_len.to_le_bytes());
    frame.extend_from_slice(payload);
    writer.write_all(&frame).await?;
    writer.flush().await?;
    Ok(())
}

/// Reads one frame.
///
/// Returns [`IpcError::Closed`] when the peer closed the connection cleanly
/// between frames, and [`IpcError::FrameTooLarge`] (before allocating
/// anything) when the announced length is over the limit.
pub async fn read_frame<R>(reader: &mut R) -> Result<Vec<u8>, IpcError>
where
    R: AsyncRead + Unpin + ?Sized,
{
    let mut prefix = [0u8; 4];
    let mut filled = 0;
    while filled < prefix.len() {
        match reader.read(&mut prefix[filled..]).await {
            Ok(0) if filled == 0 => return Err(IpcError::Closed),
            Ok(0) => return Err(io::Error::from(io::ErrorKind::UnexpectedEof).into()),
            Ok(read) => filled += read,
            // A named pipe reports a client that went away as a broken pipe.
            Err(error) if filled == 0 && error.kind() == io::ErrorKind::BrokenPipe => {
                return Err(IpcError::Closed);
            }
            Err(error) => return Err(error.into()),
        }
    }
    let len = usize::try_from(u32::from_le_bytes(prefix)).unwrap_or(usize::MAX);
    check_len(len)?;
    let mut payload = vec![0; len];
    reader.read_exact(&mut payload).await?;
    Ok(payload)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn round_trips_frames() {
        let mut wire = Vec::new();
        write_frame(&mut wire, b"{\"type\":\"ping\"}")
            .await
            .unwrap();
        write_frame(&mut wire, b"").await.unwrap();
        assert_eq!(&wire[..4], &15u32.to_le_bytes());

        let mut reader = wire.as_slice();
        assert_eq!(
            read_frame(&mut reader).await.unwrap(),
            b"{\"type\":\"ping\"}"
        );
        assert_eq!(read_frame(&mut reader).await.unwrap(), b"");
        assert!(matches!(
            read_frame(&mut reader).await,
            Err(IpcError::Closed)
        ));
    }

    #[test]
    fn the_limit_is_exactly_max_frame() {
        assert_eq!(check_len(MAX_FRAME).unwrap(), 16 * 1024 * 1024);
        assert!(matches!(
            check_len(MAX_FRAME + 1),
            Err(IpcError::FrameTooLarge { len, max }) if len == MAX_FRAME + 1 && max == MAX_FRAME
        ));
    }

    #[tokio::test]
    async fn rejects_an_oversized_length_before_reading_the_payload() {
        // Only the prefix is on the wire. If the length were not checked first,
        // the reader would allocate 16 MiB and then fail with UnexpectedEof.
        let prefix = u32::try_from(MAX_FRAME + 1).unwrap().to_le_bytes();
        let mut reader = prefix.as_slice();
        assert!(matches!(
            read_frame(&mut reader).await,
            Err(IpcError::FrameTooLarge { len, .. }) if len == MAX_FRAME + 1
        ));
    }

    #[tokio::test]
    async fn a_truncated_frame_is_an_error_not_a_clean_close() {
        let mut short_prefix: &[u8] = &[5, 0];
        assert!(matches!(
            read_frame(&mut short_prefix).await,
            Err(IpcError::Io(error)) if error.kind() == io::ErrorKind::UnexpectedEof
        ));

        let mut short_payload: &[u8] = &[5, 0, 0, 0, b'a', b'b'];
        assert!(matches!(
            read_frame(&mut short_payload).await,
            Err(IpcError::Io(error)) if error.kind() == io::ErrorKind::UnexpectedEof
        ));
    }
}
