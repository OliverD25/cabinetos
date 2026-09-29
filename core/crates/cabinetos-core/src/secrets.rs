//! The secret requests (`secret_set`, `secret_get`, `secret_delete`,
//! `secret_list`; `docs/ipc.md`, "Secrets"): named values in the Windows
//! Credential Manager. Only a pipe client (a window, the command line)
//! reaches them; a plugin never does. No value ever reaches the log.

use cabinetos_protocol::{ErrorCode, Request, Response, SecretText};
use cabinetos_secrets::{SecretError, SecretValue, Secrets};

/// Another target prefix than `CabinetOS/`, for tests, so they never touch
/// the user's own secrets.
pub(crate) const PREFIX_ENV: &str = "CABINETOS_SECRETS_PREFIX";

/// The secrets of this core: `CabinetOS/<name>`, or the prefix of
/// `CABINETOS_SECRETS_PREFIX`.
pub(crate) fn from_env() -> Secrets {
    match std::env::var(PREFIX_ENV) {
        Ok(prefix) if !prefix.trim().is_empty() => Secrets::new(prefix),
        _ => Secrets::default(),
    }
}

/// Answers one secret request. Blocking: it calls the Credential Manager.
pub(crate) fn answer(secrets: &Secrets, request: Request) -> Response {
    let result = match request {
        Request::SecretSet { name, value } => {
            secrets.set(&name, &SecretValue::new(value.0)).map(|()| {
                tracing::info!(secret = %name, "secret stored");
                Response::Ok
            })
        }
        Request::SecretGet { name } => secrets.get(&name).map(|value| {
            tracing::info!(secret = %name, "secret read by a client");
            Response::Secret {
                value: SecretText(value.expose().to_owned()),
            }
        }),
        Request::SecretDelete { name } => secrets.delete(&name).map(|()| {
            tracing::info!(secret = %name, "secret removed");
            Response::Ok
        }),
        Request::SecretList => secrets.list().map(|names| Response::SecretNames { names }),
        _ => {
            return Response::Error {
                code: ErrorCode::Internal,
                message: "not a secret request".to_owned(),
            };
        }
    };
    result.unwrap_or_else(|error| Response::Error {
        code: match error {
            SecretError::NotFound(_) => ErrorCode::NoSuchSecret,
            _ => ErrorCode::SecretError,
        },
        message: error.to_string(),
    })
}
