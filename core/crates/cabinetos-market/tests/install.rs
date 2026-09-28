//! The marketplace client end to end, without a core: indexes built on the
//! fly in `%TEMP%\cabinetos-core-test\`, from the committed `hello` fixture
//! plugin, and a web server on 127.0.0.1 for the web path. Nothing here
//! reaches the network.

use std::collections::BTreeMap;
use std::fs;
use std::io::{Cursor, Read, Write};
use std::net::TcpListener;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use cabinetos_market::{Dirs, Market, Source};
use cabinetos_protocol::ErrorCode;
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
                br#"{"id":"sneaky","name":"Sneaky","version":"1.0.0"}"#,
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
                br#"{"id":"md-preview","name":"Markdown Preview","version":"1.0.0","entry":"index.html"}"#,
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
