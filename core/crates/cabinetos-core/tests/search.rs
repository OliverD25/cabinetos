//! Search end to end with the real `cabinetos-core.exe`: without an indexer
//! (a bounded walk), and with a stand-in indexer serving the real indexer
//! protocol on a pipe of its own. Needs no Administrator rights. Everything
//! the tests write lives under `%TEMP%\cabinetos-index-test\` and is removed.

use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::sync::Arc;
use std::time::{Duration, Instant};

use cabinetos_indexer::{IndexSource, PIPE_SDDL, SearchReply, serve};
use cabinetos_ipc::{PipeClient, PipeName, PipeServer};
use cabinetos_protocol::{
    ErrorCode, FileHit, HitKind, IndexState, IndexerErrorCode, Request, Response, SearchSource,
    VolumeStatus,
};
use tempfile::TempDir;
use tokio_util::sync::CancellationToken;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);

fn scratch(prefix: &str) -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-index-test");
    std::fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix(prefix)
        .tempdir_in(root)
        .unwrap()
}

/// A tree to walk: two matching files, one in a subfolder.
fn tree() -> TempDir {
    let dir = scratch("tree");
    std::fs::create_dir(dir.path().join("inner")).unwrap();
    std::fs::write(dir.path().join("Budget-2026.xlsx"), "x").unwrap();
    std::fs::write(dir.path().join("inner").join("old-budget.xlsx"), "x").unwrap();
    std::fs::write(dir.path().join("inner").join("notes.txt"), "x").unwrap();
    dir
}

struct Core {
    child: Child,
    pipe: PipeName,
    _dir: TempDir,
}

impl Drop for Core {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn start_core(indexer_pipe: &PipeName) -> Core {
    let dir = scratch("core");
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(dir.path().join("cabinetos.json"))
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
        .env("CABINETOS_PLUGINS_DIR", dir.path().join("plugins"))
        .env(
            "CABINETOS_PLUGINS_DATA_DIR",
            dir.path().join("plugins-data"),
        )
        .env("CABINETOS_THEMES_DIR", dir.path().join("themes"))
        .env("CABINETOS_INDEXER_PIPE", indexer_pipe.as_str())
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_LOG_STDERR")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    Core {
        child,
        pipe,
        _dir: dir,
    }
}

async fn connect(pipe: &PipeName) -> PipeClient {
    let deadline = Instant::now() + STARTUP_DEADLINE;
    loop {
        match PipeClient::connect(pipe, Duration::from_secs(1)).await {
            Ok(client) => return client,
            Err(_) if Instant::now() < deadline => {
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            Err(error) => panic!("the core's pipe did not appear: {error}"),
        }
    }
}

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
}

fn search(query: &str, root: Option<&Path>) -> Request {
    Request::Search {
        query: query.to_owned(),
        limit: 20,
        root: root.map(|root| root.display().to_string()),
    }
}

fn indexer_pipe() -> PipeName {
    PipeName::from_full(format!(
        r"\\.\pipe\cabinetos-indexer-test-{:016x}",
        rand::random::<u64>()
    ))
}

fn names(hits: &[FileHit]) -> Vec<&str> {
    hits.iter()
        .map(|hit| hit.path.rsplit('\\').next().unwrap())
        .collect()
}

#[tokio::test]
async fn without_an_indexer_search_walks_the_folder() {
    let core = start_core(&indexer_pipe());
    let mut client = connect(&core.pipe).await;
    let tree = tree();

    let reply = ask(&mut client, search("budget", Some(tree.path()))).await;
    let Response::FileSearchResults {
        hits,
        source,
        complete,
        took_us,
    } = reply
    else {
        panic!("expected file_search_results, got {reply:?}");
    };
    assert_eq!(source, SearchSource::Walk);
    assert!(complete, "a small tree is walked completely");
    assert_eq!(names(&hits), ["Budget-2026.xlsx", "old-budget.xlsx"]);
    assert_eq!(hits[0].kind, HitKind::File);
    assert!(took_us < 2_000_000, "{took_us}");

    assert_eq!(
        ask(&mut client, Request::IndexStatus).await,
        Response::IndexStatus {
            available: false,
            volumes: Vec::new()
        }
    );

    // Without a root, the walk starts at the folder this connection listed
    // last.
    let welcome = client.hello("search-test").await.unwrap().body;
    assert!(matches!(welcome, Response::Welcome { .. }));
    let listed = ask(
        &mut client,
        Request::ListDirectory {
            path: tree.path().join("inner").display().to_string(),
            include_hidden: None,
            sort: None,
            watch: false,
        },
    )
    .await;
    let Response::ListingOpened { section_handle, .. } = listed else {
        panic!("expected listing_opened, got {listed:?}");
    };
    drop(client.take_section(section_handle));
    let Response::FileSearchResults { hits, source, .. } =
        ask(&mut client, search("budget", None)).await
    else {
        panic!("expected hits");
    };
    assert_eq!(source, SearchSource::Walk);
    assert_eq!(names(&hits), ["old-budget.xlsx"]);

    let Response::Error { code, .. } = ask(&mut client, search("  ", None)).await else {
        panic!("an empty query is refused");
    };
    assert_eq!(code, ErrorCode::ProtocolError);
    let Response::Error { code, .. } =
        ask(&mut client, search("x", Some(&tree.path().join("gone")))).await
    else {
        panic!("a missing root is refused");
    };
    assert_eq!(code, ErrorCode::NotFound);
}

/// Answers like the indexer, from a fixed list.
struct Stand;

impl IndexSource for Stand {
    fn status(&self) -> Vec<VolumeStatus> {
        vec![VolumeStatus {
            letter: 'C',
            state: IndexState::Ready,
            entries: 1_000_000,
            built_in_ms: Some(2500),
            journal_lag: Some(0),
        }]
    }

    fn search(
        &self,
        query: &str,
        _limit: usize,
        root: Option<&str>,
    ) -> Result<SearchReply, (IndexerErrorCode, String)> {
        if root.is_some() {
            return Err((IndexerErrorCode::NotIndexed, "not indexed".to_owned()));
        }
        Ok(SearchReply {
            hits: vec![FileHit {
                path: format!(r"C:\indexed\{query}.txt"),
                kind: HitKind::File,
                frn: Some(0x0001_0000_0000_1234),
            }],
            complete: true,
        })
    }
}

#[tokio::test]
async fn an_indexer_that_answers_is_used_and_its_absence_is_walked_around() {
    let pipe = indexer_pipe();
    let server = PipeServer::bind_with_sddl(&pipe, PIPE_SDDL).unwrap();
    let stop = CancellationToken::new();
    let serving = tokio::spawn(serve(server, Arc::new(Stand), stop.clone()));
    let core = start_core(&pipe);
    let mut client = connect(&core.pipe).await;

    let Response::FileSearchResults {
        hits,
        source,
        complete,
        ..
    } = ask(&mut client, search("budget", None)).await
    else {
        panic!("expected hits");
    };
    assert_eq!(source, SearchSource::Index);
    assert!(complete);
    assert_eq!(hits[0].path, r"C:\indexed\budget.txt");
    assert_eq!(hits[0].frn, Some(0x0001_0000_0000_1234));

    let Response::IndexStatus { available, volumes } = ask(&mut client, Request::IndexStatus).await
    else {
        panic!("expected index_status");
    };
    assert!(available);
    assert_eq!(volumes[0].entries, 1_000_000);

    // The indexer cannot answer for this root: the core walks instead.
    let tree = tree();
    let Response::FileSearchResults { hits, source, .. } =
        ask(&mut client, search("budget", Some(tree.path()))).await
    else {
        panic!("expected hits");
    };
    assert_eq!(source, SearchSource::Walk);
    assert_eq!(hits.len(), 2);

    // The indexer stops: searches go on, by walking.
    stop.cancel();
    serving.await.unwrap();
    let Response::FileSearchResults { source, .. } =
        ask(&mut client, search("budget", Some(tree.path()))).await
    else {
        panic!("expected hits");
    };
    assert_eq!(source, SearchSource::Walk);
    assert_eq!(
        ask(&mut client, Request::IndexStatus).await,
        Response::IndexStatus {
            available: false,
            volumes: Vec::new()
        }
    );
}
