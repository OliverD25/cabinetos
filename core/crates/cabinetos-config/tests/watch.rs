//! The watcher and the store together, on a real directory: edits saved the
//! ways editors save them are in effect within a second, and the store's own
//! writes are not reported back.

use std::path::PathBuf;
use std::sync::{Arc, Mutex, mpsc};
use std::time::{Duration, Instant};

use cabinetos_config::{
    Config, ConfigStore, ConfigWatcher, DEBOUNCE, Layout, Rejection, Reload, WatchEvent,
};

/// The Phase 3 promise: a saved edit is in effect within one second.
const DEADLINE: Duration = Duration::from_secs(1);

#[expect(clippy::unnecessary_wraps, reason = "it stands in for a validator")]
fn accept(_: &Config) -> Result<(), Rejection> {
    Ok(())
}

struct Fixture {
    _dir: tempfile::TempDir,
    path: PathBuf,
    store: Arc<Mutex<ConfigStore>>,
    reloads: mpsc::Receiver<Reload>,
    watcher: ConfigWatcher,
}

fn start() -> Fixture {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("cabinetos.json");
    let (store, _) = ConfigStore::open(path.clone(), accept);
    let store = Arc::new(Mutex::new(store));
    let (reload_tx, reloads) = mpsc::channel();
    let shared = Arc::clone(&store);
    let watcher = ConfigWatcher::start(&path, move |event| {
        assert_eq!(event, WatchEvent::Changed);
        let _ = reload_tx.send(shared.lock().unwrap().reload(accept));
    })
    .unwrap();
    Fixture {
        _dir: dir,
        path,
        store,
        reloads,
        watcher,
    }
}

impl Fixture {
    /// The first reload that is not `Unchanged`, and how long it took from
    /// `since`.
    fn next_change(&self, since: Instant) -> (Reload, Duration) {
        loop {
            let left = DEADLINE.saturating_sub(since.elapsed());
            match self.reloads.recv_timeout(left) {
                Ok(Reload::Unchanged) => {}
                Ok(reload) => return (reload, since.elapsed()),
                Err(error) => panic!("no change was picked up within {DEADLINE:?}: {error}"),
            }
        }
    }
}

#[test]
fn an_in_place_save_is_in_effect_within_a_second() {
    let fixture = start();
    let saved = Instant::now();
    std::fs::write(&fixture.path, r#"{ "ui": { "layout": "rail" } }"#).unwrap();
    let (reload, elapsed) = fixture.next_change(saved);
    assert_eq!(reload, Reload::Changed(vec!["ui.layout".to_owned()]));
    assert!(elapsed < DEADLINE, "{elapsed:?}");
    assert_eq!(
        fixture.store.lock().unwrap().config().ui.layout,
        Layout::Rail
    );
    fixture.watcher.stop();
}

#[test]
fn a_rename_into_place_save_is_in_effect_within_a_second() {
    let fixture = start();
    // What many editors do: write a temporary file, then rename it over the
    // original, which replaces the file the watcher started with.
    let temporary = fixture.path.with_file_name("cabinetos.json~");
    std::fs::write(&temporary, r#"{ "panes": { "showHidden": true } }"#).unwrap();
    let saved = Instant::now();
    std::fs::rename(&temporary, &fixture.path).unwrap();
    let (reload, elapsed) = fixture.next_change(saved);
    assert_eq!(reload, Reload::Changed(vec!["panes.showHidden".to_owned()]));
    assert!(elapsed < DEADLINE, "{elapsed:?}");

    // The watch survives the replacement: the next save is seen as well.
    let saved = Instant::now();
    std::fs::write(&fixture.path, r#"{ "panes": { "showHidden": false } }"#).unwrap();
    let (reload, _) = fixture.next_change(saved);
    assert_eq!(reload, Reload::Changed(vec!["panes.showHidden".to_owned()]));
    fixture.watcher.stop();
}

#[test]
fn a_bad_save_is_reported_with_its_line_and_the_settings_stay() {
    let fixture = start();
    let saved = Instant::now();
    std::fs::write(
        &fixture.path,
        "{\n  \"ui\": {\n    \"layout\": \"diagonal\"\n  }\n}",
    )
    .unwrap();
    let (reload, _) = fixture.next_change(saved);
    let Reload::Invalid(error) = reload else {
        panic!("expected an error, got {reload:?}")
    };
    assert_eq!(error.line, Some(3), "{error}");
    assert_eq!(
        fixture.store.lock().unwrap().config().ui.layout,
        Layout::Classic
    );
    fixture.watcher.stop();
}

#[test]
fn the_stores_own_writes_are_not_reported() {
    let fixture = start();
    let changed = fixture
        .store
        .lock()
        .unwrap()
        .set_keybinding(
            "view.toggleSidebar",
            Some("ctrl+alt+b".parse().unwrap()),
            accept,
        )
        .unwrap();
    assert_eq!(changed, ["keybindings"]);
    // The write causes notifications; every reload they cause must find
    // nothing new.
    let quiet_until = Instant::now() + DEBOUNCE * 5;
    while let Some(left) = quiet_until.checked_duration_since(Instant::now()) {
        match fixture.reloads.recv_timeout(left) {
            Ok(Reload::Unchanged) | Err(mpsc::RecvTimeoutError::Timeout) => {}
            Ok(other) => panic!("the store's own write came back as {other:?}"),
            Err(mpsc::RecvTimeoutError::Disconnected) => panic!("the watcher stopped"),
        }
    }
    fixture.watcher.stop();
}
