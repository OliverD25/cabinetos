//! The indexer's pipe: its security as Windows stores it, and the protocol
//! over a real pipe instance with a stand-in index. Needs no Administrator
//! rights: a normal process may give its own pipe a medium label.

use std::sync::Arc;
use std::time::Duration;

use cabinetos_indexer::{IndexSource, PIPE_SDDL, SearchReply, serve};
use cabinetos_ipc::{PipeName, PipeServer, exchange};
use cabinetos_protocol::{
    Envelope, FileHit, HitKind, IndexState, IndexerErrorCode, IndexerRequest, IndexerResponse,
    RequestId, VolumeStatus,
};
use tokio_util::sync::CancellationToken;

fn random_pipe() -> PipeName {
    PipeName::from_full(format!(
        r"\\.\pipe\cabinetos-indexer-test-{:016x}",
        rand::random::<u64>()
    ))
}

/// The SDDL access mask of the ACE for `trustee` with the given type
/// (`A` allow, `D` deny), as written between `(` and `)`.
fn ace_rights<'a>(sddl: &'a str, kind: &str, trustee: &str) -> Option<&'a str> {
    sddl.split('(').find_map(|ace| {
        let fields: Vec<&str> = ace
            .trim_end_matches(|c| c != ')')
            .trim_end_matches(')')
            .split(';')
            .collect();
        (fields.len() == 6 && fields[0] == kind && fields[5] == trustee).then(|| fields[2])
    })
}

/// Rights in SDDL: a hex mask, or two-letter aliases.
fn mask(rights: &str) -> u32 {
    if let Some(hex) = rights.strip_prefix("0x") {
        return u32::from_str_radix(hex, 16).unwrap();
    }
    rights
        .as_bytes()
        .chunks(2)
        .map(|alias| match alias {
            b"FA" => 0x001F_01FF,
            b"FR" => 0x0012_0089,
            b"FW" => 0x0012_0116,
            b"FX" => 0x0012_00A0,
            b"GA" => 0x1000_0000,
            b"GR" => 0x8000_0000,
            b"GW" => 0x4000_0000,
            other => panic!("unexpected right {}", String::from_utf8_lossy(other)),
        })
        .fold(0, |all, right| all | right)
}

#[tokio::test]
async fn the_pipe_carries_the_medium_label_and_admits_the_interactive_user() {
    let server = PipeServer::bind_with_sddl(&random_pipe(), PIPE_SDDL).unwrap();
    let stored = server.stored_security().unwrap();
    println!("stored: {stored}");

    // The label: medium integrity, no write up. Without it the pipe of an
    // elevated indexer would be high, and the normal core could not write.
    let label = stored
        .split("S:")
        .nth(1)
        .unwrap_or_else(|| panic!("no label in {stored}"));
    assert!(label.contains("(ML;;NW;;;ME)"), "{stored}");

    let dacl = stored.split("S:").next().unwrap();
    assert!(dacl.starts_with("D:P"), "the DACL is protected: {stored}");
    let full = 0x001F_01FF;
    assert_eq!(
        mask(ace_rights(dacl, "D", "NU").unwrap()),
        full,
        "network logons denied: {stored}"
    );
    assert_eq!(mask(ace_rights(dacl, "A", "SY").unwrap()), full, "{stored}");
    assert_eq!(mask(ace_rights(dacl, "A", "BA").unwrap()), full, "{stored}");
    let user = mask(ace_rights(dacl, "A", "IU").unwrap());
    let read_write = 0x0012_0089 | 0x0012_0116;
    assert_eq!(
        user, read_write,
        "the interactive user may read and write, no more: {stored}"
    );
}

struct Fake;

impl IndexSource for Fake {
    fn status(&self) -> Vec<VolumeStatus> {
        vec![VolumeStatus {
            letter: 'E',
            state: IndexState::Ready,
            entries: 42,
            built_in_ms: Some(7),
            journal_lag: Some(0),
        }]
    }

    fn search(
        &self,
        query: &str,
        limit: usize,
        _root: Option<&str>,
    ) -> Result<SearchReply, (IndexerErrorCode, String)> {
        Ok(SearchReply {
            hits: vec![FileHit {
                path: format!(r"E:\{query}.txt"),
                kind: HitKind::File,
                frn: Some(7),
            }]
            .into_iter()
            .take(limit)
            .collect(),
            complete: true,
        })
    }
}

async fn ask(pipe: &PipeName, request: IndexerRequest) -> Envelope<IndexerResponse> {
    let id = RequestId::new();
    let reply: Envelope<IndexerResponse> = exchange(
        pipe,
        &Envelope::new(id.clone(), request),
        Duration::from_secs(5),
    )
    .await
    .unwrap();
    assert_eq!(reply.id, id, "the reply carries the request's ID");
    reply
}

#[tokio::test]
async fn requests_are_answered_over_the_pipe() {
    let pipe = random_pipe();
    let server = PipeServer::bind_with_sddl(&pipe, PIPE_SDDL).unwrap();
    let shutdown = CancellationToken::new();
    let serving = tokio::spawn(serve(server, Arc::new(Fake), shutdown.clone()));

    assert!(matches!(
        ask(&pipe, IndexerRequest::Ping).await.body,
        IndexerResponse::Pong { .. }
    ));
    let IndexerResponse::IndexStatus { volumes } =
        ask(&pipe, IndexerRequest::IndexStatus).await.body
    else {
        panic!("expected index_status");
    };
    assert_eq!(volumes[0].letter, 'E');
    let IndexerResponse::FileSearchResults { hits, complete, .. } = ask(
        &pipe,
        IndexerRequest::Search {
            query: "notes".to_owned(),
            limit: 10,
            root: None,
        },
    )
    .await
    .body
    else {
        panic!("expected hits");
    };
    assert_eq!(hits[0].path, r"E:\notes.txt");
    assert!(complete);

    // Anything but the three read-only requests is refused.
    let refused: Envelope<IndexerResponse> = exchange(
        &pipe,
        &serde_json::json!({"id": "01J9ZQ4X7K3M5N8P2R6S0T1V4W", "type": "shutdown"}),
        Duration::from_secs(5),
    )
    .await
    .unwrap();
    assert!(matches!(
        refused.body,
        IndexerResponse::Error {
            code: IndexerErrorCode::UnknownRequest,
            ..
        }
    ));

    shutdown.cancel();
    serving.await.unwrap();
    let gone = exchange::<_, Envelope<IndexerResponse>>(
        &pipe,
        &Envelope::new(RequestId::new(), IndexerRequest::Ping),
        Duration::from_millis(500),
    )
    .await;
    assert!(gone.is_err(), "the pipe is gone after shutdown");
}

#[tokio::test]
async fn a_second_indexer_cannot_take_the_pipe() {
    let pipe = random_pipe();
    let _first = PipeServer::bind_with_sddl(&pipe, PIPE_SDDL).unwrap();
    assert!(PipeServer::bind_with_sddl(&pipe, PIPE_SDDL).is_err());
}
