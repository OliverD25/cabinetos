//! The marketplace client end to end, without a core: indexes built on the
//! fly in `%TEMP%\cabinetos-core-test\`, from the committed `hello` fixture
//! plugin (and the Agent extension's two items, from the repository's own
//! files), and a web server on 127.0.0.1 for the web path. Nothing here
//! reaches the network.

use std::collections::BTreeMap;
use std::fs;
use std::io::{Cursor, Read, Write};
use std::net::TcpListener;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use cabinetos_market::{Dirs, Index, Market, Source};
use cabinetos_protocol::{Catalogue, ErrorCode, ThemeKind};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tempfile::TempDir;

fn scratch() -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-core-test");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("market")
        .tempdir_in(root)
        .unwrap()
}

fn fixture(file: &str) -> Vec<u8> {
    fs::read(
        Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../../sdk/fixtures/plugins/hello")
            .join(file),
    )
    .unwrap()
}

fn hello_manifest() -> Value {
    serde_json::from_slice(&fixture("plugin.json")).unwrap()
}

fn sha256(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .fold(String::new(), |mut text, byte| {
            let _ = std::fmt::Write::write_fmt(&mut text, format_args!("{byte:02x}"));
            text
        })
}

fn zip(files: &[(&str, &[u8])]) -> Vec<u8> {
    let mut writer = zip::ZipWriter::new(Cursor::new(Vec::new()));
    let options = zip::write::SimpleFileOptions::default()
        .compression_method(zip::CompressionMethod::Deflated);
    for (name, bytes) in files {
        writer.start_file(*name, options).unwrap();
        writer.write_all(bytes).unwrap();
    }
    writer.finish().unwrap().into_inner()
}

/// A folder with an index and its downloads, and the folders a core would
/// install into.
struct Setup {
    dir: TempDir,
    items: Vec<Value>,
}

impl Setup {
    fn new() -> Self {
        let dir = scratch();
        fs::create_dir_all(dir.path().join("index").join("files")).unwrap();
        Self {
            dir,
            items: Vec::new(),
        }
    }

    fn dirs(&self) -> Dirs {
        Dirs {
            plugins: self.dir.path().join("plugins"),
            themes: self.dir.path().join("themes"),
            tools: self.dir.path().join("tools"),
            market: self.dir.path().join("marketplace"),
        }
    }

    fn index_dir(&self) -> PathBuf {
        self.dir.path().join("index")
    }

    /// Adds an item whose download is `bytes`, written next to the index.
    fn offer(&mut self, kind: &str, id: &str, version: &str, bytes: &[u8], extra: &Value) {
        let file = format!("files/{id}-{version}.bin");
        fs::write(self.index_dir().join(&file), bytes).unwrap();
        let mut item = json!({
            "id": id,
            "kind": kind,
            "name": id,
            "author": {"name": "Tester", "verified": false},
            "version": version,
            "description": "A test item.",
            "size": bytes.len(),
            "download": {"url": file, "sha256": sha256(bytes)},
            "manifest": {"id": id},
            "minCoreVersion": "0.1.0",
            "license": "MIT"
        });
        for (key, value) in extra.as_object().unwrap() {
            item[key] = value.clone();
        }
        self.items.push(item);
    }

    fn offer_hello(&mut self, bytes: &[u8]) {
        let manifest = hello_manifest();
        let capabilities = manifest["capabilities"].clone();
        self.offer(
            "plugin",
            "hello",
            "0.1.0",
            bytes,
            &json!({"manifest": manifest, "capabilities": capabilities}),
        );
    }

    fn write_index(&self) {
        let index =
            json!({"schemaVersion": 1, "generatedAt": "2026-09-28T00:00:00Z", "items": self.items});
        fs::write(
            self.index_dir().join("index.json"),
            serde_json::to_string_pretty(&index).unwrap(),
        )
        .unwrap();
    }

    fn market(&self) -> Market {
        Market::new(self.dirs(), "0.1.0")
    }
}

/// Checks a staged plugin the way the core's plugin host would, for what
/// these tests need.
fn check_plugin(dir: &Path) -> Result<(), String> {
    let manifest: Value = serde_json::from_slice(
        &fs::read(dir.join("plugin.json")).map_err(|error| error.to_string())?,
    )
    .map_err(|error| error.to_string())?;
    let folder = dir.file_name().unwrap().to_string_lossy();
    if manifest["id"] == *folder {
        Ok(())
    } else {
        Err(format!("the id must be the folder's name, {folder}"))
    }
}

fn files_under(dir: &Path) -> Vec<String> {
    let mut found = Vec::new();
    if let Ok(entries) = fs::read_dir(dir) {
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                found.extend(files_under(&path));
            } else {
                found.push(path.display().to_string());
            }
        }
    }
    found
}

#[test]
fn a_plugin_installs_from_a_local_index_and_uninstalls_exactly_its_files() {
    let mut setup = Setup::new();
    let package = zip(&[
        ("plugin.json", &fixture("plugin.json")),
        ("plugin.wasm", &fixture("plugin.wasm")),
    ]);
    setup.offer_hello(&package);
    setup.write_index();
    let market = setup.market();
    let index = market
        .fetch(&Source::Local(setup.index_dir()), false)
        .unwrap();
    assert_eq!(index.items.len(), 1);
    assert!(index.source.ends_with("index.json"), "{}", index.source);
    let item = market.choose(&index, "hello", None).unwrap();

    let mut reports = Vec::new();
    let installed = market
        .install(
            &index,
            item,
            false,
            &check_plugin,
            &mut |bytes, total, done| {
                reports.push((bytes, total, done));
            },
        )
        .unwrap();
    assert_eq!(installed.files, ["hello/plugin.json", "hello/plugin.wasm"]);
    assert_eq!(installed.sha256, sha256(&package));
    let size = package.len() as u64;
    assert_eq!(reports.last(), Some(&(size, size, true)));
    let plugins = setup.dirs().plugins;
    assert_eq!(
        fs::read(plugins.join("hello").join("plugin.wasm")).unwrap(),
        fixture("plugin.wasm")
    );
    assert!(market.installed().contains_key("hello"));
    let market_dir = setup.dirs().market;
    assert!(files_under(&market_dir.join("downloads")).is_empty());
    assert!(!market_dir.join("staging").join("hello").exists());

    // A file the user added, and the plugin's own data folder, stay.
    fs::write(plugins.join("hello").join("notes.txt"), "mine").unwrap();
    let data = setup.dir.path().join("plugins-data").join("hello");
    fs::create_dir_all(&data).unwrap();
    fs::write(data.join("state.json"), "{}").unwrap();
    let removed = market.uninstall("hello", |_| Ok(())).unwrap();
    assert_eq!(removed.files, installed.files);
    assert!(!plugins.join("hello").join("plugin.json").exists());
    assert!(!plugins.join("hello").join("plugin.wasm").exists());
    assert!(plugins.join("hello").join("notes.txt").is_file());
    assert!(data.join("state.json").is_file());
    assert!(market.installed().is_empty());

    let again = market.uninstall("hello", |_| Ok(())).unwrap_err();
    assert_eq!(again.code, ErrorCode::NoSuchExtension);
}

#[test]
fn a_wrong_hash_installs_nothing_and_leaves_no_files() {
    let mut setup = Setup::new();
    let package = zip(&[
        ("plugin.json", &fixture("plugin.json")),
        ("plugin.wasm", &fixture("plugin.wasm")),
    ]);
    setup.offer_hello(&package);
    setup.items[0]["download"]["sha256"] = json!("0".repeat(64));
    setup.write_index();
    let market = setup.market();
    let index = market
        .fetch(&Source::Local(setup.index_dir()), false)
        .unwrap();
    let item = market.choose(&index, "hello", None).unwrap();
    let error = market
        .install(&index, item, false, &check_plugin, &mut |_, _, _| {})
        .unwrap_err();
    assert_eq!(error.code, ErrorCode::HashMismatch);
    assert!(error.message.contains("nothing was installed"), "{error}");
    assert!(!setup.dirs().plugins.join("hello").exists());
    assert!(files_under(&setup.dirs().market.join("downloads")).is_empty());
    assert!(!setup.dirs().market.join("staging").join("hello").exists());
    assert!(market.installed().is_empty());
}

#[test]
fn a_bare_component_gets_its_manifest_from_the_index() {
    let mut setup = Setup::new();
    setup.offer_hello(&fixture("plugin.wasm"));
    setup.write_index();
    let market = setup.market();
    let index = market
        .fetch(&Source::Local(setup.index_dir().join("index.json")), false)
        .unwrap();
    let item = market.choose(&index, "hello", Some("0.1.0")).unwrap();
    market
        .install(&index, item, false, &check_plugin, &mut |_, _, _| {})
        .unwrap();
    let written: Value = serde_json::from_slice(
        &fs::read(setup.dirs().plugins.join("hello").join("plugin.json")).unwrap(),
    )
    .unwrap();
    assert_eq!(written, hello_manifest());
}

#[test]
fn a_plugin_that_does_not_match_its_listing_is_refused() {
    let mut setup = Setup::new();
    let package = zip(&[
        ("plugin.json", &fixture("plugin.json")),
        ("plugin.wasm", &fixture("plugin.wasm")),
    ]);
    setup.offer_hello(&package);
    // The index hides a capability the plugin asks for.
    setup.items[0]["capabilities"] = json!([{"name": "cmd:register", "reason": "Commands."}]);
    setup.write_index();
    let market = setup.market();
    let index = market
        .fetch(&Source::Local(setup.index_dir()), false)
        .unwrap();
    let item = market.choose(&index, "hello", None).unwrap();
    let error = market
        .install(&index, item, false, &check_plugin, &mut |_, _, _| {})
        .unwrap_err();
    assert!(
        error.message.contains("asks for cmd:register, events:emit"),
        "{error}"
    );
    assert!(!setup.dirs().plugins.join("hello").exists());

    // A zip without its component.
    let mut setup = Setup::new();
    setup.offer_hello(&zip(&[("plugin.json", &fixture("plugin.json"))]));
    setup.write_index();
    let market = setup.market();
    let index = market
        .fetch(&Source::Local(setup.index_dir()), false)
        .unwrap();
    let item = market.choose(&index, "hello", None).unwrap();
    let error = market
        .install(&index, item, false, &check_plugin, &mut |_, _, _| {})
        .unwrap_err();
    assert!(error.message.contains("has no plugin.wasm"), "{error}");
}

#[test]
fn a_zip_that_points_outside_its_folder_is_refused() {
    let mut setup = Setup::new();
    setup.offer(
        "tool",
        "sneaky",
        "1.0.0",
        &zip(&[
            (
                "tool.json",
                br#"{"id":"sneaky","name":"Sneaky","version":"1.0.0","author":"Me","description":"X."}"#,
            ),
            ("../escaped.txt", b"out"),
        ]),
        &json!({}),
    );
    setup.write_index();
    let market = setup.market();
    let index = market
        .fetch(&Source::Local(setup.index_dir()), false)
        .unwrap();
    let item = market.choose(&index, "sneaky", None).unwrap();
    let error = market
        .install(&index, item, false, &check_plugin, &mut |_, _, _| {})
        .unwrap_err();
    assert!(error.message.contains("points outside"), "{error}");
    let market_dir = setup.dirs().market;
    assert!(!market_dir.join("staging").join("escaped.txt").exists());
    assert!(!market_dir.join("escaped.txt").exists());
    assert!(!setup.dirs().tools.join("sneaky").exists());
}

#[test]
fn a_theme_installs_updates_and_never_replaces_what_it_did_not_install() {
    let shipped = cabinetos_themes::SHIPPED[1].1;
    let theme = |version: &str| {
        let mut value: Value = serde_json::from_str(shipped).unwrap();
        value["id"] = json!("nord-test");
        value["version"] = json!(version);
        serde_json::to_vec_pretty(&value).unwrap()
    };
    let mut setup = Setup::new();
    setup.offer("theme", "nord-test", "1.0.0", &theme("1.0.0"), &json!({}));
    setup.offer("theme", "nord-test", "1.1.0", &theme("1.1.0"), &json!({}));
    setup.offer("theme", "nord", "1.0.0", shipped.as_bytes(), &json!({}));
    setup.write_index();
    let market = setup.market();
    let index = market
        .fetch(&Source::Local(setup.index_dir()), false)
        .unwrap();
    let themes = setup.dirs().themes;

    let first = market.choose(&index, "nord-test", Some("1.0.0")).unwrap();
    let installed = market
        .install(&index, first, false, &check_plugin, &mut |_, _, _| {})
        .unwrap();
    assert_eq!(installed.files, ["nord-test.json"]);
    let newest = market.choose(&index, "nord-test", None).unwrap();
    assert_eq!(newest.version, "1.1.0");
    market
        .install(&index, newest, false, &check_plugin, &mut |_, _, _| {})
        .unwrap();
    let on_disk: Value =
        serde_json::from_slice(&fs::read(themes.join("nord-test.json")).unwrap()).unwrap();
    assert_eq!(on_disk["version"], "1.1.0");
    assert_eq!(market.installed()["nord-test"].version, "1.1.0");

    // A theme file the marketplace did not install stays untouched.
    fs::write(themes.join("nord.json"), shipped).unwrap();
    let nord = market.choose(&index, "nord", None).unwrap();
    let error = market
        .install(&index, nord, false, &check_plugin, &mut |_, _, _| {})
        .unwrap_err();
    assert_eq!(error.code, ErrorCode::AlreadyExists);

    // The caller may refuse an uninstall; then nothing is removed.
    let refused = market
        .uninstall("nord-test", |_| {
            Err(cabinetos_market::MarketError::new(
                ErrorCode::MarketplaceError,
                "in use",
            ))
        })
        .unwrap_err();
    assert_eq!(refused.message, "in use");
    assert!(themes.join("nord-test.json").is_file());
    market.uninstall("nord-test", |_| Ok(())).unwrap();
    assert!(!themes.join("nord-test.json").exists());
    assert!(themes.join("nord.json").is_file());
}

#[test]
fn a_tool_is_unpacked_listed_and_removed_with_its_empty_folders() {
    let mut setup = Setup::new();
    setup.offer(
        "tool",
        "md-preview",
        "1.0.0",
        &zip(&[
            (
                "tool.json",
                br#"{"id":"md-preview","name":"Markdown Preview","version":"1.0.0","author":"Me","description":"Shows Markdown.","entry":"index.html","accepts":["*.md"],"placement":"pane"}"#,
            ),
            ("index.html", b"<!doctype html>"),
            ("assets/app.js", b"// app"),
        ]),
        &json!({}),
    );
    setup.write_index();
    let market = setup.market();
    let index = market
        .fetch(&Source::Local(setup.index_dir()), false)
        .unwrap();
    let item = market.choose(&index, "md-preview", None).unwrap();
    let installed = market
        .install(&index, item, false, &check_plugin, &mut |_, _, _| {})
        .unwrap();
    assert_eq!(
        installed.files,
        [
            "md-preview/assets/app.js",
            "md-preview/index.html",
            "md-preview/tool.json"
        ]
    );
    let tools = market.tools();
    assert_eq!(tools.len(), 1);
    assert_eq!(tools[0].name, "Markdown Preview");
    market.uninstall("md-preview", |_| Ok(())).unwrap();
    assert!(!setup.dirs().tools.join("md-preview").exists());
    assert!(market.tools().is_empty());
}

#[test]
fn a_client_is_offered_one_version_per_extension_and_sees_what_is_installed() {
    let mut setup = Setup::new();
    let package = zip(&[
        ("plugin.json", &fixture("plugin.json")),
        ("plugin.wasm", &fixture("plugin.wasm")),
    ]);
    setup.offer_hello(&package);
    let mut newer = setup.items[0].clone();
    newer["version"] = json!("0.3.0");
    newer["minCoreVersion"] = json!("9.0.0");
    setup.items.push(newer);
    setup.offer("theme", "nord-test", "1.0.0", b"{}", &json!({}));
    setup.offer("theme", "nord-test", "1.2.0", b"{}", &json!({}));
    setup.offer("theme", "nord-test", "1.1.0", b"{}", &json!({}));
    setup.offer(
        "tool",
        "too-new",
        "1.0.0",
        b"PK",
        &json!({"minCoreVersion": "9.0.0"}),
    );
    setup.write_index();
    let market = setup.market();
    let index = market
        .fetch(&Source::Local(setup.index_dir()), false)
        .unwrap();
    assert_eq!(index.items.len(), 6);

    let offered = market.offers(&index.items);
    let shown: Vec<(&str, &str)> = offered
        .iter()
        .map(|item| (item.id.as_str(), item.version.as_str()))
        .collect();
    // hello 0.3.0 and the tool need a newer core; the newest theme wins.
    assert_eq!(shown, [("hello", "0.1.0"), ("nord-test", "1.2.0")]);
    assert!(offered.iter().all(|item| item.installed_version.is_none()));

    let hello = market.choose(&index, "hello", None).unwrap();
    market
        .install(&index, hello, false, &check_plugin, &mut |_, _, _| {})
        .unwrap();
    let offered = market.offers(&index.items);
    assert_eq!(offered[0].installed_version.as_deref(), Some("0.1.0"));
    assert_eq!(offered[1].installed_version, None);
    market.uninstall("hello", |_| Ok(())).unwrap();
    assert_eq!(market.offers(&index.items)[0].installed_version, None);
}

#[test]
fn the_newest_version_this_core_runs_is_chosen() {
    let mut setup = Setup::new();
    let package = zip(&[
        ("plugin.json", &fixture("plugin.json")),
        ("plugin.wasm", &fixture("plugin.wasm")),
    ]);
    setup.offer_hello(&package);
    let mut newer = setup.items[0].clone();
    newer["version"] = json!("0.2.0");
    newer["minCoreVersion"] = json!("9.0.0");
    setup.items.push(newer);
    setup.write_index();
    let market = setup.market();
    let index = market
        .fetch(&Source::Local(setup.index_dir()), false)
        .unwrap();
    assert_eq!(
        market.choose(&index, "hello", None).unwrap().version,
        "0.1.0"
    );
    let too_new = market.choose(&index, "hello", Some("0.2.0")).unwrap_err();
    assert_eq!(too_new.code, ErrorCode::Incompatible);
    assert!(
        too_new.message.contains("needs CabinetOS 9.0.0"),
        "{too_new}"
    );
    let missing = market.choose(&index, "hello", Some("3.0.0")).unwrap_err();
    assert_eq!(missing.code, ErrorCode::NoSuchExtension);
    let unknown = market.choose(&index, "nothing", None).unwrap_err();
    assert_eq!(unknown.code, ErrorCode::NoSuchExtension);
}

/// A web server on 127.0.0.1 that serves `files` by path, answers
/// `If-None-Match: "v1"` with 304, and records every request's head.
fn serve(files: BTreeMap<String, Vec<u8>>, requests: usize) -> (u16, Arc<Mutex<Vec<String>>>) {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let seen = Arc::new(Mutex::new(Vec::new()));
    let log = Arc::clone(&seen);
    std::thread::spawn(move || {
        for _ in 0..requests {
            let (mut stream, _) = listener.accept().unwrap();
            let mut head = Vec::new();
            let mut byte = [0; 1];
            while !head.ends_with(b"\r\n\r\n") && stream.read(&mut byte).unwrap() == 1 {
                head.push(byte[0]);
            }
            let head = String::from_utf8_lossy(&head).into_owned();
            let path = head.split_whitespace().nth(1).unwrap_or("/").to_owned();
            let response = if head.to_ascii_lowercase().contains("if-none-match: \"v1\"") {
                b"HTTP/1.1 304 Not Modified\r\nETag: \"v1\"\r\nConnection: close\r\n\r\n".to_vec()
            } else if let Some(body) = files.get(&path) {
                let mut response = format!(
                    "HTTP/1.1 200 OK\r\nETag: \"v1\"\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                    body.len()
                )
                .into_bytes();
                response.extend_from_slice(body);
                response
            } else {
                b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".to_vec()
            };
            stream.write_all(&response).unwrap();
            log.lock().unwrap().push(head);
        }
    });
    (port, seen)
}

/// A cached copy of the index that cannot be read (half of it, as an
/// interrupted write leaves it) must not stick: the server answers the
/// cache's tag with 304, which would send every refresh back to the broken
/// copy until the index changes on the server.
#[test]
fn a_damaged_cached_index_is_fetched_again() {
    let mut setup = Setup::new();
    setup.offer("theme", "paper", "1.0.0", b"{}", &json!({}));
    setup.write_index();
    let files = BTreeMap::from([(
        "/market/index.json".to_owned(),
        fs::read(setup.index_dir().join("index.json")).unwrap(),
    )]);
    let (port, seen) = serve(files, 3);
    let source =
        Source::parse(&format!("http://127.0.0.1:{port}/market/index.json"), true).unwrap();
    let market = setup.market();
    let first = market.fetch(&source, true).unwrap();

    let cached = setup.dirs().market.join("index.json");
    let text = fs::read(&cached).unwrap();
    fs::write(&cached, &text[..text.len() / 2]).unwrap();
    let second = market.fetch(&source, true).unwrap();
    assert_eq!(second.items, first.items);
    assert_eq!(fs::read(&cached).unwrap(), text, "the cache is whole again");

    let seen = seen.lock().unwrap();
    assert_eq!(seen.len(), 3);
    assert!(seen[1].to_ascii_lowercase().contains("if-none-match"));
    assert!(!seen[2].to_ascii_lowercase().contains("if-none-match"));
}

/// The HTTPS client sets up rustls with its crypto provider and the Windows
/// certificate store when it first connects. A local port that hangs up
/// makes the handshake start and fail: an error, not a panic about a
/// missing provider, and nothing leaves the machine.
#[test]
fn the_https_client_sets_up_its_tls_and_reports_a_failed_handshake() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    std::thread::spawn(move || {
        if let Ok((stream, _)) = listener.accept() {
            drop(stream);
        }
    });
    let setup = Setup::new();
    let source = Source::parse(&format!("https://127.0.0.1:{port}/index.json"), false).unwrap();
    let error = setup.market().fetch(&source, false).unwrap_err();
    assert_eq!(error.code, ErrorCode::MarketplaceError);
    assert!(error.message.contains("cannot fetch the index"), "{error}");
}

#[test]
fn a_web_index_is_refused_over_plain_http_unless_allowed_and_then_cached() {
    let mut setup = Setup::new();
    let package = zip(&[
        ("plugin.json", &fixture("plugin.json")),
        ("plugin.wasm", &fixture("plugin.wasm")),
    ]);
    setup.offer_hello(&package);
    setup.write_index();
    let files = BTreeMap::from([
        (
            "/market/index.json".to_owned(),
            fs::read(setup.index_dir().join("index.json")).unwrap(),
        ),
        ("/market/files/hello-0.1.0.bin".to_owned(), package.clone()),
    ]);
    let (port, seen) = serve(files, 3);
    let url = format!("http://127.0.0.1:{port}/market/index.json");

    let refused = Source::parse(&url, false).unwrap_err();
    assert_eq!(refused.code, ErrorCode::MarketplaceError);
    assert!(refused.message.contains("plain http"), "{refused}");

    let source = Source::parse(&url, true).unwrap();
    let market = setup.market();
    let first = market.fetch(&source, true).unwrap();
    assert_eq!(first.items.len(), 1);
    assert_eq!(first.source, url);
    let cache = setup.dirs().market;
    assert!(cache.join("index.json").is_file());
    let meta: Value =
        serde_json::from_slice(&fs::read(cache.join("index.meta.json")).unwrap()).unwrap();
    assert_eq!(meta["etag"], "\"v1\"");

    // Unchanged: the server answers 304 and the cached copy is used.
    let second = market.fetch(&source, true).unwrap();
    assert_eq!(second.items, first.items);

    // The download comes from the web too, next to the index.
    let item = market.choose(&second, "hello", None).unwrap();
    market
        .install(&second, item, true, &check_plugin, &mut |_, _, _| {})
        .unwrap();
    assert!(
        setup
            .dirs()
            .plugins
            .join("hello")
            .join("plugin.wasm")
            .is_file()
    );

    let seen = seen.lock().unwrap();
    assert!(!seen[0].to_ascii_lowercase().contains("if-none-match"));
    assert!(
        seen[1]
            .to_ascii_lowercase()
            .contains("if-none-match: \"v1\"")
    );
    assert!(seen[2].contains("/market/files/hello-0.1.0.bin"));
}

/// The files of a folder of the repository, as a zip would carry them:
/// names with `/`, relative to the folder.
fn folder_files(dir: &Path) -> Vec<(String, Vec<u8>)> {
    fn walk(root: &Path, dir: &Path, into: &mut Vec<(String, Vec<u8>)>) {
        let mut entries: Vec<_> = fs::read_dir(dir).unwrap().flatten().collect();
        entries.sort_by_key(std::fs::DirEntry::file_name);
        for entry in entries {
            let path = entry.path();
            if path.is_dir() {
                walk(root, &path, into);
            } else {
                let name = path
                    .strip_prefix(root)
                    .unwrap()
                    .to_string_lossy()
                    .replace('\\', "/");
                into.push((name, fs::read(&path).unwrap()));
            }
        }
    }
    let mut found = Vec::new();
    walk(dir, dir, &mut found);
    found
}

#[test]
fn the_agent_extension_installs_as_a_plugin_and_a_tool_from_the_repository_s_own_files() {
    let repository = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let plugin = repository.join("sdk/fixtures/plugins/agent");
    let tool = repository.join("sdk/tools/agent-chat");
    let mut setup = Setup::new();
    let manifest: Value =
        serde_json::from_slice(&fs::read(plugin.join("plugin.json")).unwrap()).unwrap();
    let capabilities = manifest["capabilities"].clone();
    let plugin_zip = zip(&[
        (
            "plugin.json",
            &fs::read(plugin.join("plugin.json")).unwrap(),
        ),
        (
            "plugin.wasm",
            &fs::read(plugin.join("plugin.wasm")).unwrap(),
        ),
    ]);
    setup.offer(
        "plugin",
        "agent",
        "0.1.0",
        &plugin_zip,
        &json!({ "manifest": manifest, "capabilities": capabilities }),
    );
    let files = folder_files(&tool);
    let tool_zip = zip(&files
        .iter()
        .map(|(name, bytes)| (name.as_str(), bytes.as_slice()))
        .collect::<Vec<_>>());
    let tool_manifest: Value =
        serde_json::from_slice(&fs::read(tool.join("tool.json")).unwrap()).unwrap();
    setup.offer(
        "tool",
        "agent-chat",
        "0.1.0",
        &tool_zip,
        &json!({ "manifest": tool_manifest }),
    );
    setup.write_index();

    let market = setup.market();
    let index = market
        .fetch(&Source::Local(setup.index_dir()), false)
        .unwrap();
    for id in ["agent", "agent-chat"] {
        let item = market.choose(&index, id, None).unwrap();
        market
            .install(&index, item, false, &check_plugin, &mut |_, _, _| {})
            .unwrap();
    }
    assert!(
        setup
            .dirs()
            .plugins
            .join("agent")
            .join("plugin.wasm")
            .is_file()
    );
    let tools = market.tools();
    assert_eq!(tools.len(), 1);
    assert_eq!(
        (tools[0].id.as_str(), tools[0].name.as_str()),
        ("agent-chat", "Agent Chat")
    );
    for (name, _) in &files {
        assert!(
            setup.dirs().tools.join("agent-chat").join(name).is_file(),
            "{name}"
        );
    }
}

// The two catalogues (Phase 23, ADR 0022): `index.json` for extensions and
// `themes.json` for themes.

/// A theme's catalogue item, with the gallery's keys; its download is not
/// needed for reading the catalogue.
fn theme_item(id: &str, appearance: &str, density: bool, accent: &str) -> Value {
    json!({
        "id": id,
        "kind": "theme",
        "name": id,
        "author": {"name": "Tester", "verified": false},
        "version": "1.0.0",
        "description": "A test theme.",
        "size": 10,
        "download": {"url": format!("files/{id}-1.0.0.json"), "sha256": "0".repeat(64)},
        "manifest": {"id": id},
        "minCoreVersion": "0.1.0",
        "license": "MIT",
        "appearance": appearance,
        "density": density,
        "tile": {"background": "#202020", "text": "#FFFFFF", "accent": accent}
    })
}

fn write_catalogue(dir: &Path, file: &str, items: &[Value]) {
    let catalogue =
        json!({"schemaVersion": 1, "generatedAt": "2026-10-03T00:00:00Z", "items": items});
    fs::write(
        dir.join(file),
        serde_json::to_string_pretty(&catalogue).unwrap(),
    )
    .unwrap();
}

fn ids(index: &Index) -> Vec<&str> {
    index.items.iter().map(|item| item.id.as_str()).collect()
}

/// `themes.json` wins: the theme items of `index.json` are not read, and
/// the extensions' list has none of them either way.
#[test]
fn themes_json_wins_over_the_theme_items_of_the_index() {
    let mut setup = Setup::new();
    setup.offer_hello(&zip(&[("plugin.json", &fixture("plugin.json"))]));
    setup
        .items
        .push(theme_item("old", "dark", false, "#111111"));
    setup.write_index();
    write_catalogue(
        &setup.index_dir(),
        "themes.json",
        &[
            theme_item("fresh", "light", false, "#0067C0"),
            theme_item("compact", "system", true, "#60CDFF"),
        ],
    );
    let market = setup.market();
    let source = Source::Local(setup.index_dir());

    // themes.json is there, so the index's address is never asked for.
    let themes = market
        .fetch_themes(&source, || panic!("the index is not needed"), false)
        .unwrap();
    assert_eq!(ids(&themes), ["fresh", "compact"]);
    assert!(themes.source.ends_with("themes.json"), "{}", themes.source);
    let fresh = &themes.items[0];
    assert_eq!(fresh.appearance, Some(ThemeKind::Light));
    assert_eq!(fresh.density, Some(false));
    assert_eq!(fresh.tile.as_ref().unwrap().accent, "#0067C0");
    assert_eq!(themes.items[1].density, Some(true));

    let extensions = market
        .fetch(&source, false)
        .unwrap()
        .only(Catalogue::Extensions);
    assert_eq!(ids(&extensions), ["hello"]);
}

/// While `themes.json` is missing from a local folder, the theme items of
/// `index.json` stand in for it, and their downloads still resolve next to
/// the index.
#[test]
fn a_missing_themes_file_falls_back_to_the_theme_items_of_the_index() {
    let mut setup = Setup::new();
    setup.offer_hello(&zip(&[("plugin.json", &fixture("plugin.json"))]));
    let mut paper: Value = serde_json::from_str(cabinetos_themes::SHIPPED[1].1).unwrap();
    paper["id"] = json!("paper");
    setup.offer(
        "theme",
        "paper",
        "1.0.0",
        &serde_json::to_vec(&paper).unwrap(),
        &json!({"appearance": "light"}),
    );
    setup.write_index();
    let market = setup.market();
    let source = Source::Local(setup.index_dir());

    let themes = market
        .fetch_themes(&source, || Ok(source.clone()), false)
        .unwrap();
    assert_eq!(ids(&themes), ["paper"]);
    assert!(themes.source.ends_with("index.json"), "{}", themes.source);
    assert!(
        themes.items[0].tile.is_none(),
        "an old index has no tiles; the window paints a plain one"
    );
    // The fallback index still finds the download next to index.json.
    let item = market.choose(&themes, "paper", None).unwrap();
    market
        .install(&themes, item, false, &check_plugin, &mut |_, _, _| {})
        .unwrap();
    assert!(setup.dirs().themes.join("paper.json").is_file());
}

/// The themes address may name a themes.json file or its folder.
#[test]
fn the_themes_address_may_be_a_file_or_a_folder() {
    let setup = Setup::new();
    write_catalogue(
        &setup.index_dir(),
        "themes.json",
        &[theme_item("a", "dark", false, "#222222")],
    );
    write_catalogue(&setup.index_dir(), "index.json", &[]);
    let market = setup.market();
    let index = Source::Local(setup.index_dir());
    for themes in [
        Source::Local(setup.index_dir()),
        Source::Local(setup.index_dir().join("themes.json")),
    ] {
        let found = market
            .fetch_themes(&themes, || Ok(index.clone()), false)
            .unwrap();
        assert_eq!(ids(&found), ["a"]);
    }
}

/// An item of another kind in `themes.json` is left out of the themes, and
/// an item with a bad tile colour or an unknown appearance is left out with
/// the rest staying.
#[test]
fn only_well_formed_theme_items_are_kept_from_the_themes_catalogue() {
    let mut setup = Setup::new();
    setup.offer_hello(&zip(&[("plugin.json", &fixture("plugin.json"))]));
    let hello = setup.items[0].clone();
    setup.write_index();
    let mut bad_tile = theme_item("bad-tile", "dark", false, "#222222");
    bad_tile["tile"]["text"] = json!("white");
    let mut bad_appearance = theme_item("bad-appearance", "dark", false, "#222222");
    bad_appearance["appearance"] = json!("sepia");
    write_catalogue(
        &setup.index_dir(),
        "themes.json",
        &[
            theme_item("good", "dark", false, "#222222"),
            bad_tile,
            bad_appearance,
            hello,
        ],
    );
    let market = setup.market();
    let source = Source::Local(setup.index_dir());
    let themes = market
        .fetch_themes(&source, || Ok(source.clone()), false)
        .unwrap();
    assert_eq!(ids(&themes), ["good"]);
}

/// A themes.json that is there but damaged is an error: the fallback is for
/// a file that is missing, not for one that cannot be read.
#[test]
fn a_damaged_themes_file_is_an_error_not_a_fallback() {
    let mut setup = Setup::new();
    setup.offer("theme", "paper", "1.0.0", b"{}", &json!({}));
    setup.write_index();
    fs::write(setup.index_dir().join("themes.json"), "{ not json").unwrap();
    let source = Source::Local(setup.index_dir());
    let error = setup
        .market()
        .fetch_themes(&source, || Ok(source.clone()), false)
        .unwrap_err();
    assert_eq!(error.code, ErrorCode::MarketplaceError);
    assert!(error.message.contains("the themes catalogue"), "{error}");
}

/// On the web: a 404 for themes.json falls back to the index, then once
/// the site has the file it is read, kept in the cache under its own
/// names, and answered with a 304 the next time.
#[test]
fn a_web_themes_catalogue_falls_back_on_a_404_and_is_cached_by_its_tag() {
    let mut setup = Setup::new();
    setup.offer("theme", "paper", "1.0.0", b"{}", &json!({}));
    setup.write_index();
    let index_bytes = fs::read(setup.index_dir().join("index.json")).unwrap();
    write_catalogue(
        &setup.index_dir(),
        "themes.json",
        &[theme_item("fresh", "dark", false, "#222222")],
    );
    let themes_bytes = fs::read(setup.index_dir().join("themes.json")).unwrap();

    // Before the site has themes.json: its path answers 404.
    let (port, seen) = serve(
        BTreeMap::from([("/market/index.json".to_owned(), index_bytes.clone())]),
        2,
    );
    let index = Source::parse(&format!("http://127.0.0.1:{port}/market/index.json"), true).unwrap();
    let themes =
        Source::parse(&format!("http://127.0.0.1:{port}/market/themes.json"), true).unwrap();
    let market = setup.market();
    let fallback = market
        .fetch_themes(&themes, || Ok(index.clone()), true)
        .unwrap();
    assert_eq!(ids(&fallback), ["paper"]);
    {
        let seen = seen.lock().unwrap();
        assert!(
            seen[0].starts_with("GET /market/themes.json"),
            "{}",
            seen[0]
        );
        assert!(seen[1].starts_with("GET /market/index.json"), "{}", seen[1]);
    }
    let cache = setup.dirs().market;
    assert!(!cache.join("themes.json").exists(), "a 404 caches nothing");

    // After the site publishes it.
    let (port, seen) = serve(
        BTreeMap::from([
            ("/market/index.json".to_owned(), index_bytes),
            ("/market/themes.json".to_owned(), themes_bytes),
        ]),
        2,
    );
    let index = Source::parse(&format!("http://127.0.0.1:{port}/market/index.json"), true).unwrap();
    let themes =
        Source::parse(&format!("http://127.0.0.1:{port}/market/themes.json"), true).unwrap();
    let first = market
        .fetch_themes(&themes, || Ok(index.clone()), true)
        .unwrap();
    assert_eq!(ids(&first), ["fresh"]);
    assert!(cache.join("themes.json").is_file());
    let meta: Value =
        serde_json::from_slice(&fs::read(cache.join("themes.meta.json")).unwrap()).unwrap();
    assert_eq!(meta["etag"], "\"v1\"");
    let second = market
        .fetch_themes(&themes, || Ok(index.clone()), true)
        .unwrap();
    assert_eq!(second.items, first.items);
    let seen = seen.lock().unwrap();
    assert!(!seen[0].to_ascii_lowercase().contains("if-none-match"));
    assert!(
        seen[1]
            .to_ascii_lowercase()
            .contains("if-none-match: \"v1\"")
    );
}

/// A themes server that cannot be reached is an error, not a reason to use
/// the index: only a 404 means "not published yet".
#[test]
fn an_unreachable_themes_server_is_an_error() {
    let mut setup = Setup::new();
    setup.offer("theme", "paper", "1.0.0", b"{}", &json!({}));
    setup.write_index();
    // A port that nothing listens on.
    let closed = {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        listener.local_addr().unwrap().port()
    };
    let themes = Source::parse(&format!("http://127.0.0.1:{closed}/themes.json"), true).unwrap();
    let index = Source::Local(setup.index_dir());
    let error = setup
        .market()
        .fetch_themes(&themes, || Ok(index.clone()), true)
        .unwrap_err();
    assert_eq!(error.code, ErrorCode::MarketplaceError);
    assert!(
        error.message.contains("cannot fetch the themes catalogue"),
        "{error}"
    );
}

/// The extensions' index is not allowed to be missing: a 404 for it is an
/// error that names the index.
#[test]
fn a_missing_index_is_still_an_error() {
    let setup = Setup::new();
    let (port, _seen) = serve(BTreeMap::new(), 1);
    let source = Source::parse(&format!("http://127.0.0.1:{port}/index.json"), true).unwrap();
    let error = setup.market().fetch(&source, true).unwrap_err();
    assert!(
        error.message.contains("404") && error.message.contains("the index"),
        "{error}"
    );
}
