//! `cabinetos-cli search` and `index status`: file search through the core,
//! from the indexer's index or from the core's own walk (`docs/indexer.md`).

use std::fmt::Write;
use std::time::Duration;

use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{HitKind, IndexState, Request, Response, SearchSource, VolumeStatus};

use crate::{failure, say, send};

/// `search`: prints the hits, best first, then where they came from.
pub(crate) async fn search(
    client: &mut PipeClient,
    query: &str,
    limit: u32,
    root: Option<&str>,
) -> anyhow::Result<()> {
    let reply = send(
        client,
        Request::Search {
            query: query.to_owned(),
            limit,
            root: root.map(str::to_owned),
        },
    )
    .await?;
    let Response::FileSearchResults {
        hits,
        source,
        took_us,
        complete,
    } = reply.body
    else {
        return Err(failure(query, &reply.body));
    };
    for hit in &hits {
        let kind = match hit.kind {
            HitKind::Directory => 'd',
            HitKind::File => 'f',
        };
        if !say(format_args!("{kind} {}", hit.path)) {
            return Ok(());
        }
    }
    let source = match source {
        SearchSource::Index => "index",
        SearchSource::Walk => "walk",
    };
    say(format_args!(
        "{} hit(s); source: {source}; {:.1} ms; {}",
        hits.len(),
        Duration::from_micros(took_us).as_secs_f64() * 1000.0,
        if complete {
            "complete"
        } else {
            "incomplete (a limit stopped the search, or a volume is still being indexed)"
        }
    ));
    Ok(())
}

/// `index status`: whether an indexer answers, and each volume's state.
pub(crate) async fn status(client: &mut PipeClient) -> anyhow::Result<()> {
    let reply = send(client, Request::IndexStatus).await?;
    let Response::IndexStatus { available, volumes } = reply.body else {
        return Err(failure("index_status", &reply.body));
    };
    if !available {
        say(format_args!(
            "available: no (no indexer answers; search walks folders instead)"
        ));
        return Ok(());
    }
    say(format_args!("available: yes"));
    for volume in &volumes {
        if !say(format_args!("{}", volume_line(volume))) {
            break;
        }
    }
    Ok(())
}

/// `C: ready, 1234567 entries, built in 2900 ms, journal lag 0 bytes`.
fn volume_line(volume: &VolumeStatus) -> String {
    let state = match &volume.state {
        IndexState::Building => "building".to_owned(),
        IndexState::Ready => "ready".to_owned(),
        IndexState::Rebuilding => "rebuilding".to_owned(),
        IndexState::Failed { message } => format!("failed: {message}"),
    };
    let mut line = format!("{}: {state}, {} entries", volume.letter, volume.entries);
    if let Some(built) = volume.built_in_ms {
        let _ = write!(line, ", built in {built} ms");
    }
    if let Some(lag) = volume.journal_lag {
        let _ = write!(line, ", journal lag {lag} bytes");
    }
    line
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_volume_reads_as_one_line() {
        let mut volume = VolumeStatus {
            letter: 'C',
            state: IndexState::Ready,
            entries: 1_234_567,
            built_in_ms: Some(2900),
            journal_lag: Some(0),
        };
        assert_eq!(
            volume_line(&volume),
            "C: ready, 1234567 entries, built in 2900 ms, journal lag 0 bytes"
        );
        volume.state = IndexState::Failed {
            message: "D: has no change journal".to_owned(),
        };
        volume.built_in_ms = None;
        volume.journal_lag = None;
        assert_eq!(
            volume_line(&volume),
            "C: failed: D: has no change journal, 1234567 entries"
        );
    }
}
