//! `cabinetos-cli market`: the marketplace index, and installing and
//! removing extensions (`docs/marketplace.md`).

use std::io::{IsTerminal, Write};
use std::time::Duration;

use anyhow::{Context, bail};
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{
    CapabilityLevel, Envelope, Event, ExtensionKind, MarketItem, Request, Response, ToolInfo,
};

use crate::{binary_size, expect_welcome, failure, say, send};

/// How long `install` waits for `install_finished` after the reply.
const FINISHED_WAIT: Duration = Duration::from_secs(1);

/// `market refresh` and `market search`: the items of the reply.
pub(crate) async fn list(client: &mut PipeClient, request: Request) -> anyhow::Result<()> {
    let subject = request.type_tag();
    let reply = send(client, request).await?;
    let Response::MarketplaceIndex { items, source, .. } = reply.body else {
        return Err(failure(subject, &reply.body));
    };
    let count = match items.len() {
        1 => "1 item".to_owned(),
        count => format!("{count} items"),
    };
    if !say(format_args!("{count} from {source}")) {
        return Ok(());
    }
    for item in &items {
        for line in describe(item) {
            if !say(format_args!("{line}")) {
                return Ok(());
            }
        }
    }
    Ok(())
}

/// The lines printed for one item.
fn describe(item: &MarketItem) -> Vec<String> {
    let verified = if item.author.verified {
        " (verified)"
    } else {
        ""
    };
    let installed = item
        .installed_version
        .as_ref()
        .map_or_else(String::new, |version| format!(", installed {version}"));
    let mut lines = vec![format!(
        "{:<18} {:<8} {:<6} {} by {}{verified}, {}{installed}",
        item.id,
        item.version,
        kind_name(item.kind),
        item.name,
        item.author.name,
        binary_size(item.size)
    )];
    lines.push(format!("  {}", item.description));
    if !item.capabilities.is_empty() {
        let asks: Vec<String> = item
            .capabilities
            .iter()
            .map(|capability| match capability.level {
                Some(level) => format!("{} ({})", capability.name, level_name(level)),
                None => capability.name.clone(),
            })
            .collect();
        lines.push(format!("  asks for: {}", asks.join(", ")));
    }
    lines
}

fn kind_name(kind: ExtensionKind) -> &'static str {
    match kind {
        ExtensionKind::Plugin => "plugin",
        ExtensionKind::Theme => "theme",
        ExtensionKind::Tool => "tool",
    }
}

fn level_name(level: CapabilityLevel) -> &'static str {
    match level {
        CapabilityLevel::Low => "low",
        CapabilityLevel::Medium => "medium",
        CapabilityLevel::High => "high",
    }
}

/// What `install` has heard about its extension.
struct Follow<'a> {
    id: &'a str,
    terminal: bool,
    /// The last progress line.
    progress: Option<String>,
    /// `install_finished`: whether it worked, and the core's message.
    finished: Option<(bool, String)>,
}

impl Follow<'_> {
    fn note(&mut self, event: Event) {
        match event {
            Event::InstallProgress {
                extension_id,
                bytes,
                total,
            } if extension_id == self.id => {
                let line = format!(
                    "{}: {} of {}",
                    self.id,
                    binary_size(bytes),
                    binary_size(total)
                );
                if self.terminal {
                    let mut out = std::io::stdout().lock();
                    let _ = write!(out, "\r{line}\x1b[K").and_then(|()| out.flush());
                }
                self.progress = Some(line);
            }
            Event::InstallFinished {
                extension_id,
                ok,
                message,
            } if extension_id == self.id => self.finished = Some((ok, message)),
            _ => {}
        }
    }
}

/// `market install`: sends the request and shows the download's progress
/// until the core answers, then what the core says it did.
pub(crate) async fn install(
    client: &mut PipeClient,
    id: &str,
    version: Option<&str>,
) -> anyhow::Result<()> {
    let mut events = client
        .events()
        .context("the event stream was already taken")?;
    expect_welcome(client).await?;
    let mut follow = Follow {
        id,
        terminal: std::io::stdout().is_terminal(),
        progress: None,
        finished: None,
    };
    let request = Request::InstallExtension {
        extension_id: id.to_owned(),
        version: version.map(str::to_owned),
    };
    let reply = {
        let exchange = send(client, request);
        tokio::pin!(exchange);
        loop {
            tokio::select! {
                reply = &mut exchange => break reply?,
                event = events.recv() => {
                    let Some(Envelope { body, .. }) = event else {
                        bail!("the connection to the core ended");
                    };
                    follow.note(body);
                }
            }
        }
    };
    // The core sends install_finished before its reply; the two travel on
    // separate paths, so it may come just after.
    let deadline = tokio::time::Instant::now() + FINISHED_WAIT;
    while follow.finished.is_none() {
        match tokio::time::timeout_at(deadline, events.recv()).await {
            Ok(Some(Envelope { body, .. })) => follow.note(body),
            _ => break,
        }
    }
    match (follow.terminal, follow.progress) {
        (true, Some(_)) => println!(),
        (false, Some(line)) => {
            say(format_args!("{line}"));
        }
        _ => {}
    }
    if reply.body != Response::Ok {
        return Err(failure(id, &reply.body));
    }
    let message = follow
        .finished
        .map_or_else(|| format!("installed {id}"), |(_, message)| message);
    say(format_args!("{message}"));
    Ok(())
}

/// `market uninstall`.
pub(crate) async fn uninstall(client: &mut PipeClient, id: &str) -> anyhow::Result<()> {
    let reply = send(
        client,
        Request::UninstallExtension {
            extension_id: id.to_owned(),
        },
    )
    .await?;
    if reply.body != Response::Ok {
        return Err(failure(id, &reply.body));
    }
    say(format_args!("uninstalled {id}"));
    Ok(())
}

/// `market tools`: every installed Tool Extension.
pub(crate) async fn tools(client: &mut PipeClient) -> anyhow::Result<()> {
    let reply = send(client, Request::ListTools).await?;
    let Response::Tools { tools } = reply.body else {
        return Err(failure("list_tools", &reply.body));
    };
    if tools.is_empty() {
        say(format_args!("no tools are installed"));
    }
    for tool in &tools {
        if !say(format_args!("{}", tool_line(tool))) {
            break;
        }
    }
    Ok(())
}

fn tool_line(tool: &ToolInfo) -> String {
    format!(
        "{:<18} {:<8} {} ({})",
        tool.id, tool.version, tool.name, tool.dir
    )
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    #[test]
    fn an_item_reads_as_a_few_lines() {
        let item: MarketItem = serde_json::from_value(json!({
            "id": "hello",
            "kind": "plugin",
            "name": "Hello",
            "author": {"name": "CabinetOS", "verified": true},
            "version": "0.1.0",
            "description": "Says hello.",
            "size": 2048,
            "download": {"url": "files/hello.zip", "sha256": "0".repeat(64)},
            "manifest": {},
            "capabilities": [
                {"name": "cmd:register", "reason": "Commands.", "level": "low"},
                {"name": "events:emit", "reason": "Events."}
            ],
            "minCoreVersion": "0.1.0",
            "license": "MIT",
            "installedVersion": "0.1.0"
        }))
        .unwrap();
        assert_eq!(
            describe(&item),
            [
                "hello              0.1.0    plugin Hello by CabinetOS (verified), 2.00 KiB, installed 0.1.0",
                "  Says hello.",
                "  asks for: cmd:register (low), events:emit",
            ]
        );
    }
}
