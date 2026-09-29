//! `secret set|get|delete|list`: named secrets in the Windows Credential
//! Manager, through the core (`docs/ipc.md`, "Secrets").

use std::io::Read;

use anyhow::Context;
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{Request, Response, SecretText};

use crate::{failure, say, send};

/// `secret set <name> [--value <value>]`: without `--value`, the value is
/// standard input, less one line ending at its end, so
/// `echo sk-… | cabinetos-cli secret set anthropic` stores `sk-…`.
pub(crate) async fn set(
    client: &mut PipeClient,
    name: &str,
    value: Option<&str>,
) -> anyhow::Result<()> {
    let value = if let Some(value) = value {
        value.to_owned()
    } else {
        let mut text = String::new();
        std::io::stdin()
            .read_to_string(&mut text)
            .context("cannot read the value from standard input")?;
        without_line_ending(&text).to_owned()
    };
    let reply = send(
        client,
        Request::SecretSet {
            name: name.to_owned(),
            value: SecretText(value),
        },
    )
    .await?;
    match reply.body {
        Response::Ok => {
            say(format_args!("stored {name}"));
            Ok(())
        }
        other => Err(failure("secret set", &other)),
    }
}

/// `secret get <name>`: prints the value alone.
pub(crate) async fn get(client: &mut PipeClient, name: &str) -> anyhow::Result<()> {
    let reply = send(
        client,
        Request::SecretGet {
            name: name.to_owned(),
        },
    )
    .await?;
    match reply.body {
        Response::Secret { value } => {
            say(format_args!("{}", value.0));
            Ok(())
        }
        other => Err(failure("secret get", &other)),
    }
}

/// `secret delete <name>`.
pub(crate) async fn delete(client: &mut PipeClient, name: &str) -> anyhow::Result<()> {
    let reply = send(
        client,
        Request::SecretDelete {
            name: name.to_owned(),
        },
    )
    .await?;
    match reply.body {
        Response::Ok => {
            say(format_args!("removed {name}"));
            Ok(())
        }
        other => Err(failure("secret delete", &other)),
    }
}

/// `secret list`: one name per line.
pub(crate) async fn list(client: &mut PipeClient) -> anyhow::Result<()> {
    let reply = send(client, Request::SecretList).await?;
    match reply.body {
        Response::SecretNames { names } => {
            if names.is_empty() {
                say(format_args!("no secrets"));
            }
            for name in names {
                if !say(format_args!("{name}")) {
                    break;
                }
            }
            Ok(())
        }
        other => Err(failure("secret list", &other)),
    }
}

/// `text` without one `\n` or `\r\n` at its end.
fn without_line_ending(text: &str) -> &str {
    text.strip_suffix("\r\n")
        .or_else(|| text.strip_suffix('\n'))
        .unwrap_or(text)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn one_line_ending_is_dropped() {
        assert_eq!(without_line_ending("sk-1\r\n"), "sk-1");
        assert_eq!(without_line_ending("sk-1\n"), "sk-1");
        assert_eq!(without_line_ending("sk-1\n\n"), "sk-1\n");
        assert_eq!(without_line_ending("sk-1"), "sk-1");
    }
}
