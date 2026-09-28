//! `cabinetos-cli plugins`, `commands exec` and `events watch`: the Core
//! Plugins as the running core sees them (`docs/plugins.md`).

use std::time::{Duration, Instant};

use anyhow::{Context, bail};
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{CapabilityLevel, Envelope, PluginInfo, PluginState, Request, Response};
use serde_json::Value;

use crate::{expect_welcome, failure, say, send};

/// How long `reload`, `enable` and `grant` wait for the plugin to settle:
/// compiling a component takes a moment.
const SETTLE_TIMEOUT: Duration = Duration::from_secs(30);

/// `plugins list`: every installed plugin, its state, its capabilities and
/// its commands; or the raw list as JSON.
pub(crate) async fn list(client: &mut PipeClient, json: bool) -> anyhow::Result<()> {
    let plugins = list_plugins(client).await?;
    if json {
        say(format_args!("{}", serde_json::to_string_pretty(&plugins)?));
        return Ok(());
    }
    if plugins.is_empty() {
        say(format_args!("no plugins are installed"));
        return Ok(());
    }
    for plugin in &plugins {
        for line in describe(plugin) {
            if !say(format_args!("{line}")) {
                return Ok(());
            }
        }
    }
    Ok(())
}

/// The lines `plugins list` prints for one plugin.
fn describe(plugin: &PluginInfo) -> Vec<String> {
    let mut lines = vec![format!(
        "{} {} ({}): {}",
        plugin.id,
        plugin.version,
        plugin.name,
        state_text(&plugin.state)
    )];
    for capability in &plugin.capabilities {
        let level = match capability.level {
            CapabilityLevel::Low => "low",
            CapabilityLevel::Medium => "medium",
            CapabilityLevel::High => "high",
        };
        let granted = if capability.granted {
            "granted"
        } else {
            "NOT granted"
        };
        let roots = if capability.roots.is_empty() {
            String::new()
        } else {
            format!(" {}", capability.roots.join(", "))
        };
        lines.push(format!(
            "  {}{roots} [{level}, {granted}]: {}",
            capability.name, capability.reason
        ));
    }
    if !plugin.commands.is_empty() {
        lines.push(format!("  commands: {}", plugin.commands.join(", ")));
    }
    lines
}

fn state_text(state: &PluginState) -> String {
    match state {
        PluginState::Loading => "loading".to_owned(),
        PluginState::Active => "active".to_owned(),
        PluginState::Disabled => "disabled".to_owned(),
        PluginState::NeedsReview { missing } => {
            format!("needs_review (grant {})", missing.join(" "))
        }
        PluginState::Failed { message } => format!("failed: {message}"),
        PluginState::Crashed { message, .. } => format!("crashed: {message}"),
    }
}

async fn list_plugins(client: &mut PipeClient) -> anyhow::Result<Vec<PluginInfo>> {
    let reply = send(client, Request::ListPlugins).await?;
    match reply.body {
        Response::Plugins { plugins } => Ok(plugins),
        other => Err(failure("list_plugins", &other)),
    }
}

/// `plugins reload`, `enable`, `disable` and `grant`: sends the request,
/// then waits until the plugin has left `loading` and prints its state.
pub(crate) async fn change(
    client: &mut PipeClient,
    plugin_id: &str,
    request: Request,
) -> anyhow::Result<()> {
    let reply = send(client, request).await?;
    if reply.body != Response::Ok {
        return Err(failure(plugin_id, &reply.body));
    }
    let deadline = Instant::now() + SETTLE_TIMEOUT;
    loop {
        let plugins = list_plugins(client).await?;
        let plugin = plugins
            .iter()
            .find(|plugin| plugin.id == plugin_id)
            .with_context(|| format!("{plugin_id} is no longer installed"))?;
        if plugin.state != PluginState::Loading || Instant::now() > deadline {
            say(format_args!("{plugin_id}: {}", state_text(&plugin.state)));
            return Ok(());
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

/// `commands exec`: runs a command and prints its JSON result.
pub(crate) async fn exec(
    client: &mut PipeClient,
    command: &str,
    args: Option<&str>,
) -> anyhow::Result<()> {
    let args = match args {
        Some(text) => serde_json::from_str(text)
            .with_context(|| format!("the arguments are not JSON: {text}"))?,
        None => Value::Null,
    };
    let started = Instant::now();
    let reply = send(
        client,
        Request::ExecuteCommand {
            command: command.to_owned(),
            args,
        },
    )
    .await?;
    let elapsed = started.elapsed();
    match reply.body {
        Response::CommandResult { result } => {
            say(format_args!("{}", serde_json::to_string_pretty(&result)?));
            Ok(())
        }
        Response::CommandRouted { target } => {
            say(format_args!(
                "{command} is run by the {}; the core handed it back",
                serde_json::to_value(target)?.as_str().unwrap_or("client")
            ));
            Ok(())
        }
        other => {
            Err(failure(command, &other).context(format!("after {:.1} s", elapsed.as_secs_f64())))
        }
    }
}

/// `events watch`: prints every event the core sends, one JSON object per
/// line, until Ctrl+C.
pub(crate) async fn watch(client: &mut PipeClient) -> anyhow::Result<()> {
    let mut events = client
        .events()
        .context("the event stream was already taken")?;
    expect_welcome(client).await?;
    if !say(format_args!("watching events (Ctrl+C to stop)")) {
        return Ok(());
    }
    loop {
        tokio::select! {
            event = events.recv() => {
                let Some(Envelope { body: event, .. }) = event else {
                    bail!("the connection to the core ended");
                };
                if !say(format_args!("{}", serde_json::to_string(&event)?)) {
                    break;
                }
            }
            _ = tokio::signal::ctrl_c() => break,
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use cabinetos_protocol::CapabilityInfo;

    use super::*;

    #[test]
    fn a_plugin_reads_as_a_few_lines() {
        let plugin = PluginInfo {
            id: "reader".to_owned(),
            name: "Reader".to_owned(),
            version: "0.1.0".to_owned(),
            author: "CabinetOS tests".to_owned(),
            description: "Reads.".to_owned(),
            state: PluginState::NeedsReview {
                missing: vec!["fs:read".to_owned()],
            },
            capabilities: vec![
                CapabilityInfo {
                    name: "cmd:register".to_owned(),
                    level: CapabilityLevel::Low,
                    granted: true,
                    reason: "Adds commands.".to_owned(),
                    roots: Vec::new(),
                },
                CapabilityInfo {
                    name: "fs:read".to_owned(),
                    level: CapabilityLevel::Medium,
                    granted: false,
                    reason: "Reads files.".to_owned(),
                    roots: vec![r"%TEMP%\x".to_owned()],
                },
            ],
            commands: Vec::new(),
        };
        assert_eq!(
            describe(&plugin),
            [
                "reader 0.1.0 (Reader): needs_review (grant fs:read)",
                "  cmd:register [low, granted]: Adds commands.",
                r"  fs:read %TEMP%\x [medium, NOT granted]: Reads files.",
            ]
        );
        assert_eq!(
            state_text(&PluginState::Crashed {
                message: "wasm trap: interrupt".to_owned(),
                at_ms: 1
            }),
            "crashed: wasm trap: interrupt"
        );
    }
}
