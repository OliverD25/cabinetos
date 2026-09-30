//! `cabinetos-cli update`: the in-app update, from the command line
//! (`docs/ipc.md`, "Updates"): the state in words, or with `--json` the
//! core's reply.

use std::io::{IsTerminal, Write};
use std::time::Duration;

use anyhow::{Context, bail};
use cabinetos_cli_args::UpdateAction;
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{Envelope, Event, Request, Response, UpdatePhase, UpdateStatus};

use crate::state::when;
use crate::{binary_size, expect_welcome, failure, say, send, send_waiting};

/// How long a download may take before the command gives up waiting (the
/// core goes on).
const DOWNLOAD_WAIT: Duration = Duration::from_mins(30);

/// How long a swap may take: it copies the whole release.
const SWAP_WAIT: Duration = Duration::from_mins(10);

/// `update [status|check|download|apply|rollback|snooze] [--json]`.
pub(crate) async fn run(
    client: &mut PipeClient,
    action: Option<UpdateAction>,
    json: bool,
) -> anyhow::Result<()> {
    let action = action.unwrap_or(UpdateAction::Status);
    let request = match action {
        UpdateAction::Status => Request::UpdateStatus,
        UpdateAction::Check => Request::UpdateCheck,
        UpdateAction::Download => Request::UpdateDownload,
        UpdateAction::Apply => Request::UpdateApply,
        UpdateAction::Rollback => Request::UpdateRollback,
        UpdateAction::Snooze => Request::UpdateSnooze,
    };
    let subject = request.type_tag();
    let reply = match action {
        UpdateAction::Download => download(client, request, !json).await?,
        UpdateAction::Apply | UpdateAction::Rollback => {
            send_waiting(client, request, SWAP_WAIT).await?.body
        }
        _ => send(client, request).await?.body,
    };
    if json {
        let text = serde_json::to_string_pretty(&reply).context("the reply cannot be printed")?;
        say(format_args!("{text}"));
        return match reply {
            Response::UpdateState(_) => Ok(()),
            other => Err(failure(subject, &other)),
        };
    }
    let Response::UpdateState(status) = reply else {
        return Err(failure(subject, &reply));
    };
    for line in describe(&status, now_ms()) {
        if !say(format_args!("{line}")) {
            break;
        }
    }
    Ok(())
}

/// `update download`: sends the request and shows the download's progress
/// until the core answers.
async fn download(
    client: &mut PipeClient,
    request: Request,
    show: bool,
) -> anyhow::Result<Response> {
    let mut events = client
        .events()
        .context("the event stream was already taken")?;
    expect_welcome(client).await?;
    let terminal = show && std::io::stdout().is_terminal();
    let mut last = None;
    let reply = {
        let exchange = send_waiting(client, request, DOWNLOAD_WAIT);
        tokio::pin!(exchange);
        loop {
            tokio::select! {
                reply = &mut exchange => break reply?,
                event = events.recv() => {
                    let Some(Envelope { body, .. }) = event else {
                        bail!("the connection to the core ended");
                    };
                    if let Event::UpdateProgress { version, bytes, total, bytes_per_second } = body {
                        let line = progress_line(&version, bytes, total, bytes_per_second);
                        if terminal {
                            let mut out = std::io::stdout().lock();
                            let _ = write!(out, "\r{line}\x1b[K").and_then(|()| out.flush());
                        }
                        last = Some(line);
                    }
                }
            }
        }
    };
    match (terminal, last) {
        (true, Some(_)) => println!(),
        (false, Some(line)) if show => {
            say(format_args!("{line}"));
        }
        _ => {}
    }
    Ok(reply.body)
}

/// `CabinetOS 0.2.0: 12.50 MiB of 77.00 MiB, 4.20 MiB/s`.
fn progress_line(version: &str, bytes: u64, total: u64, bytes_per_second: u64) -> String {
    format!(
        "CabinetOS {version}: {} of {}, {}/s",
        binary_size(bytes),
        binary_size(total),
        binary_size(bytes_per_second)
    )
}

/// The state in words: one line for where the updater is, then what else
/// it knows.
fn describe(status: &UpdateStatus, now_ms: u64) -> Vec<String> {
    let head = format!(
        "CabinetOS {}, {} channel",
        status.current,
        status.channel.name()
    );
    let latest = status.latest.as_ref();
    let newest = latest.map_or("?", |latest| latest.version.as_str());
    let first = match status.state {
        UpdatePhase::NotUpdatable => format!(
            "CabinetOS {} does not update itself: {}",
            status.current,
            status.reason.as_deref().unwrap_or("this install cannot")
        ),
        UpdatePhase::Unchecked => {
            format!("{head}: not checked yet; `cabinetos-cli update check` checks now")
        }
        UpdatePhase::UpToDate => format!("{head}: up to date"),
        UpdatePhase::Checking => format!("{head}: checking now"),
        UpdatePhase::Available => {
            format!("{head}: {newest} is available; `cabinetos-cli update download` downloads it")
        }
        UpdatePhase::Downloading => format!("{head}: downloading {newest}"),
        UpdatePhase::Downloaded => {
            format!("{head}: {newest} is downloaded; `cabinetos-cli update apply` puts it in place")
        }
        UpdatePhase::Applying => format!("{head}: moving the files now"),
        UpdatePhase::Ready => format!(
            "{head}: {} is in place; restart CabinetOS to run it",
            status.installed.as_deref().unwrap_or(newest)
        ),
        UpdatePhase::Failed => format!(
            "{head}: the last step failed: {}",
            status.message.as_deref().unwrap_or("no reason given")
        ),
    };
    let mut lines = vec![first];
    let waiting = matches!(
        status.state,
        UpdatePhase::Available | UpdatePhase::Downloading | UpdatePhase::Downloaded
    );
    if let Some(latest) = latest.filter(|_| waiting) {
        let mut needs = Vec::new();
        if let Some(dotnet) = &latest.requires.dotnet {
            needs.push(format!(".NET {dotnet}"));
        }
        if let Some(runtime) = &latest.requires.windows_app_runtime {
            needs.push(format!("the Windows App Runtime {runtime}"));
        }
        let needs = if needs.is_empty() {
            String::new()
        } else {
            format!("; it needs {}", needs.join(" and "))
        };
        lines.push(format!(
            "  {} published {}, {}{needs}",
            latest.version,
            latest.published,
            binary_size(latest.zip.size)
        ));
        if let Some(url) = &status.notes_url {
            lines.push(format!("  release notes: {url}"));
        }
    }
    if let Some(previous) = &status.previous {
        lines.push(format!(
            "  kept for a rollback: {previous} (`cabinetos-cli update rollback`)"
        ));
    }
    if let Some(until) = status.snoozed_until_ms.filter(|until| *until > now_ms) {
        lines.push(format!(
            "  Later: the window asks again after {}",
            when(until)
        ));
    }
    if let Some(checked) = status.checked_at_ms {
        lines.push(format!("  last check: {}", when(checked)));
    }
    if let Some(dir) = &status.install_dir {
        lines.push(format!("  install folder: {dir}"));
    }
    lines
}

fn now_ms() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |since| {
            u64::try_from(since.as_millis()).unwrap_or(u64::MAX)
        })
}

#[cfg(test)]
mod tests {
    use cabinetos_protocol::{
        UpdateChannel, UpdateNotes, UpdateRelease, UpdateRequires, UpdateZip,
    };

    use super::*;

    fn status(state: UpdatePhase) -> UpdateStatus {
        UpdateStatus {
            state,
            reason: None,
            message: None,
            current: "0.1.0".to_owned(),
            channel: UpdateChannel::Stable,
            latest: Some(UpdateRelease {
                schema_version: 1,
                channel: UpdateChannel::Stable,
                version: "0.2.0".to_owned(),
                published: "2026-10-01".to_owned(),
                zip: UpdateZip {
                    url: "files/a.zip".to_owned(),
                    sha256: "0".repeat(64),
                    size: 80 * 1024 * 1024,
                },
                notes: UpdateNotes {
                    url: "notes-0.2.0.md".to_owned(),
                },
                requires: UpdateRequires {
                    windows_app_runtime: Some("2.5".to_owned()),
                    dotnet: Some("10.0".to_owned()),
                },
            }),
            notes_url: Some("https://example.org/notes-0.2.0.md".to_owned()),
            notes: None,
            checked_at_ms: None,
            snoozed_until_ms: None,
            previous: None,
            installed: None,
            install_dir: None,
        }
    }

    #[test]
    fn the_state_reads_as_words() {
        assert_eq!(
            describe(&status(UpdatePhase::Downloaded), 0),
            [
                "CabinetOS 0.1.0, stable channel: 0.2.0 is downloaded; `cabinetos-cli update apply` puts it in place",
                "  0.2.0 published 2026-10-01, 80.00 MiB; it needs .NET 10.0 and the Windows App Runtime 2.5",
                "  release notes: https://example.org/notes-0.2.0.md",
            ]
        );
        let mut ready = status(UpdatePhase::Ready);
        ready.installed = Some("0.2.0".to_owned());
        ready.previous = Some("0.1.0".to_owned());
        assert_eq!(
            describe(&ready, 0),
            [
                "CabinetOS 0.1.0, stable channel: 0.2.0 is in place; restart CabinetOS to run it",
                "  kept for a rollback: 0.1.0 (`cabinetos-cli update rollback`)",
            ]
        );
        let mut blocked = status(UpdatePhase::NotUpdatable);
        blocked.reason = Some("a development build".to_owned());
        assert_eq!(
            describe(&blocked, 0)[0],
            "CabinetOS 0.1.0 does not update itself: a development build"
        );
        let mut snoozed = status(UpdatePhase::UpToDate);
        snoozed.snoozed_until_ms = Some(1_000);
        assert_eq!(describe(&snoozed, 2_000).len(), 1, "a snooze that is over");
        assert_eq!(describe(&snoozed, 500).len(), 2);
        assert_eq!(
            progress_line("0.2.0", 1024 * 1024, 4 * 1024 * 1024, 512 * 1024),
            "CabinetOS 0.2.0: 1.00 MiB of 4.00 MiB, 512.00 KiB/s"
        );
    }
}
