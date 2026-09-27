use serde::{Deserialize, Serialize};

use crate::RequestId;

/// One message on the control channel: a request ID plus the message body.
///
/// On the wire the body's fields sit next to `id` in one flat JSON object:
///
/// ```json
/// {"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"ping"}
/// {"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"pong","protocol_version":1,"core_version":"0.1.0"}
/// ```
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct Envelope<T> {
    /// Created by the sender of a request. The reply carries the same ID.
    pub id: RequestId,
    /// The message itself.
    #[serde(flatten)]
    pub body: T,
}

impl<T> Envelope<T> {
    /// Wraps `body` with the given ID.
    pub fn new(id: RequestId, body: T) -> Self {
        Self { id, body }
    }
}

/// A request from a client (the UI or the CLI) to the core.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Request {
    /// Asks whether the core is alive. The core answers `pong`.
    Ping,
    /// Asks the core to exit cleanly. The core answers `ok`, then exits.
    Shutdown,
}

impl Request {
    /// Every `type` tag a request can carry. A frame whose `type` is not in
    /// this list is answered with [`ErrorCode::UnknownRequest`].
    pub const TYPES: &'static [&'static str] = &["ping", "shutdown"];

    /// The `type` tag of this request on the wire.
    #[must_use]
    pub const fn type_tag(&self) -> &'static str {
        match self {
            Self::Ping => "ping",
            Self::Shutdown => "shutdown",
        }
    }
}

/// The core's reply to a [`Request`].
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Response {
    /// Reply to `ping`.
    Pong {
        /// The protocol version the core speaks (`PROTOCOL_VERSION`, now 1).
        protocol_version: u32,
        /// The core's version, for example `0.1.0`.
        core_version: String,
    },
    /// The request succeeded and returns no data.
    Ok,
    /// The request failed.
    Error {
        /// What kind of failure.
        code: ErrorCode,
        /// A human-readable explanation, for logs and error dialogs.
        message: String,
    },
}

/// Why a request failed.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum ErrorCode {
    /// The envelope was valid, but its `type` is not a request this core knows.
    UnknownRequest,
    /// The frame was not a valid envelope: bad JSON, a missing or malformed
    /// `id`, or fields that do not fit the request type.
    ProtocolError,
    /// The frame was longer than the 16 MiB limit. The core closes the
    /// connection after this reply, because large data belongs in shared
    /// memory.
    FrameTooLarge,
    /// The core failed while handling a valid request.
    Internal,
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    const ID: &str = "01J9ZQ4X7K3M5N8P2R6S0T1V4W";

    fn id() -> RequestId {
        ID.parse().unwrap()
    }

    #[test]
    fn ping_has_the_documented_wire_form() {
        let json = serde_json::to_string(&Envelope::new(id(), Request::Ping)).unwrap();
        assert_eq!(json, format!(r#"{{"id":"{ID}","type":"ping"}}"#));
    }

    #[test]
    fn pong_has_the_documented_wire_form() {
        let pong = Response::Pong {
            protocol_version: 1,
            core_version: "0.1.0".to_owned(),
        };
        let json = serde_json::to_string(&Envelope::new(id(), pong)).unwrap();
        assert_eq!(
            json,
            format!(r#"{{"id":"{ID}","type":"pong","protocol_version":1,"core_version":"0.1.0"}}"#)
        );
    }

    #[test]
    fn every_message_round_trips() {
        for request in [Request::Ping, Request::Shutdown] {
            let envelope = Envelope::new(id(), request);
            let json = serde_json::to_string(&envelope).unwrap();
            assert_eq!(
                serde_json::from_str::<Envelope<Request>>(&json).unwrap(),
                envelope
            );
        }
        for response in [
            Response::Pong {
                protocol_version: 1,
                core_version: "0.1.0".to_owned(),
            },
            Response::Ok,
            Response::Error {
                code: ErrorCode::FrameTooLarge,
                message: "too big".to_owned(),
            },
        ] {
            let envelope = Envelope::new(id(), response);
            let json = serde_json::to_string(&envelope).unwrap();
            assert_eq!(
                serde_json::from_str::<Envelope<Response>>(&json).unwrap(),
                envelope
            );
        }
    }

    #[test]
    fn error_codes_are_snake_case() {
        let codes = [
            (ErrorCode::UnknownRequest, "unknown_request"),
            (ErrorCode::ProtocolError, "protocol_error"),
            (ErrorCode::FrameTooLarge, "frame_too_large"),
            (ErrorCode::Internal, "internal"),
        ];
        for (code, text) in codes {
            assert_eq!(serde_json::to_value(code).unwrap(), json!(text));
        }
    }

    #[test]
    fn type_tags_match_the_wire() {
        let requests = [Request::Ping, Request::Shutdown];
        assert_eq!(Request::TYPES.len(), requests.len());
        for request in requests {
            let value = serde_json::to_value(Envelope::new(id(), request.clone())).unwrap();
            assert_eq!(value["type"], json!(request.type_tag()));
            assert!(Request::TYPES.contains(&request.type_tag()));
        }
    }

    #[test]
    fn rejects_unknown_types_and_missing_ids() {
        let unknown = json!({"id": ID, "type": "format_disk"});
        assert!(serde_json::from_value::<Envelope<Request>>(unknown).is_err());
        let no_id = json!({"type": "ping"});
        assert!(serde_json::from_value::<Envelope<Request>>(no_id).is_err());
        let bad_id = json!({"id": "nope", "type": "ping"});
        assert!(serde_json::from_value::<Envelope<Request>>(bad_id).is_err());
    }

    #[test]
    fn ignores_unknown_fields_for_forward_compatibility() {
        let extra = json!({"id": ID, "type": "ping", "added_later": true});
        let envelope: Envelope<Request> = serde_json::from_value(extra).unwrap();
        assert_eq!(envelope.body, Request::Ping);
    }
}
