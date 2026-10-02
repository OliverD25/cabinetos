//! The GUI context commands against a real core: `pane`, `selection`, and
//! `copy` and `move` with `--selection --dest`, as `cab` runs them in a
//! CabinetOS terminal. This test is the window: a client that says `hello`
//! and sends `window_state`, and stays connected (the core forgets a
//! window's state when its connection ends). Every file lives under
//! `%TEMP%\cabinetos-gui-test\`, removed by the tests.
//!
//! Needs `cabinetos-core.exe` next to `cabinetos-cli.exe`; `cargo test
//! --workspace` builds both.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{Pane, PaneState, Request, Response, WindowPanes, WindowState, WindowTab};
use serde_json::Value;
use tempfile::TempDir;

const CLI_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-cli");
const DEADLINE: Duration = Duration::from_secs(30);

fn scratch(prefix: &str) -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-gui-test");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix(prefix)
        .tempdir_in(root)
        .unwrap()
}

fn shown(path: &Path) -> String {
    path.display().to_string()
}

struct Core {
    child: Child,
    pipe: PipeName,
}

impl Drop for Core {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn core_exe() -> PathBuf {
    let path = Path::new(CLI_EXE).with_file_name("cabinetos-core.exe");
    assert!(
        path.exists(),
        "{} is missing; build cabinetos-core first",
        path.display()
    );
    path
}

fn start_core(dir: &Path) -> Core {
    let pipe = PipeName::random();
    let child = Command::new(core_exe())
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(dir.join("cabinetos.json"))
        .env("CABINETOS_LOG_DIR", dir.join("logs"))
        .env("CABINETOS_PLUGINS_DIR", dir.join("plugins"))
        .env("CABINETOS_PLUGINS_DATA_DIR", dir.join("plugins-data"))
        .env("CABINETOS_THEMES_DIR", dir.join("themes"))
        .env("CABINETOS_UNDO_DIR", dir.join("undo"))
        .env(
            "CABINETOS_INDEXER_PIPE",
            format!(
                r"\\.\pipe\cabinetos-indexer-none-{}",
                PipeName::random().token()
            ),
        )
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let core = Core { child, pipe };
    let deadline = Instant::now() + DEADLINE;
    while !cab(&core, &["ping"]).status.success() {
        assert!(Instant::now() < deadline, "the core did not answer");
        std::thread::sleep(Duration::from_millis(50));
    }
    core
}

fn command(core: &Core, args: &[&str]) -> Command {
    let mut command = Command::new(CLI_EXE);
    command
        .args(["--pipe", core.pipe.token()])
        .args(args)
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_PIPE");
    command
}

fn cab(core: &Core, args: &[&str]) -> Output {
    command(core, args).output().unwrap()
}

fn out(output: &Output) -> String {
    String::from_utf8_lossy(&output.stdout).into_owned()
}

fn err(output: &Output) -> String {
    String::from_utf8_lossy(&output.stderr).into_owned()
}

/// The exit code, or a panic that shows what the command printed.
fn code(output: &Output) -> i32 {
    output.status.code().unwrap_or_else(|| {
        panic!(
            "no exit code\nstdout: {}\nstderr: {}",
            out(output),
            err(output)
        )
    })
}

/// A window: `hello`, then `window_state` at will, connected until dropped.
struct Window {
    client: PipeClient,
    runtime: tokio::runtime::Runtime,
}

impl Window {
    fn connect(core: &Core) -> Self {
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        let client = runtime.block_on(async {
            let mut client = PipeClient::connect(&core.pipe, DEADLINE).await.unwrap();
            client.hello("gui-context-test").await.unwrap();
            client
        });
        Self { client, runtime }
    }

    fn say(&mut self, active_pane: Pane, left: PaneState, right: PaneState) {
        self.say_showing(active_pane, left, right, true);
    }

    /// As `say`, also saying whether the window shows both panes.
    fn say_showing(&mut self, active_pane: Pane, left: PaneState, right: PaneState, dual: bool) {
        let state = WindowState {
            active_pane,
            panes: WindowPanes { left, right },
            dual,
        };
        let Self { client, runtime } = self;
        let reply = runtime
            .block_on(client.request(Request::WindowState(state)))
            .unwrap()
            .body;
        assert_eq!(reply, Response::Ok);
    }
}

/// A pane that shows `folder`, its cursor on `cursor`, with `marked` marked.
fn pane(folder: &Path, cursor: Option<&Path>, marked: &[&Path]) -> PaneState {
    PaneState {
        tabs: vec![WindowTab {
            path: shown(folder),
            ..WindowTab::default()
        }],
        cursor: cursor.map(shown),
        marked: marked.iter().map(|path| shown(path)).collect(),
        ..PaneState::default()
    }
}

/// Two folders, `left` with the files `names` (each holds its own name and
/// its folder), and an empty `right`.
fn folders(base: &Path, names: &[&str]) -> (PathBuf, PathBuf) {
    let (left, right) = (base.join("left"), base.join("Правий 'pane'"));
    fs::create_dir_all(&left).unwrap();
    fs::create_dir_all(&right).unwrap();
    for name in names {
        fs::write(left.join(name), format!("left {name}")).unwrap();
    }
    (left, right)
}

fn lines(text: &str) -> Vec<&str> {
    text.lines().collect()
}

#[test]
fn pane_prints_the_folders_the_window_shows() {
    let dir = scratch("pane");
    let core = start_core(dir.path());
    let (left, right) = folders(dir.path(), &["a.txt"]);

    // No window has spoken: nothing to print, and that is exit code 2.
    let silent = cab(&core, &["pane"]);
    assert_eq!(code(&silent), 2, "{}", err(&silent));
    assert_eq!(out(&silent), "");
    assert!(err(&silent).contains("no window"), "{}", err(&silent));

    let mut window = Window::connect(&core);
    window.say(Pane::Left, pane(&left, None, &[]), pane(&right, None, &[]));
    let active = cab(&core, &["pane"]);
    assert_eq!(code(&active), 0, "{}", err(&active));
    assert_eq!(out(&active), format!("{}\n", shown(&left)));
    assert_eq!(out(&cab(&core, &["pane", "--left"])), out(&active));
    assert_eq!(
        out(&cab(&core, &["pane", "--right"])),
        format!("{}\n", shown(&right)),
        "a name with spaces, quotes and Cyrillic comes back whole"
    );

    // The keyboard moves to the right pane: `pane` follows it at once.
    window.say(Pane::Right, pane(&left, None, &[]), pane(&right, None, &[]));
    assert_eq!(
        out(&cab(&core, &["pane"])),
        format!("{}\n", shown(&right)),
        "the context is live, not what it was when the shell started"
    );

    // As a shell of a CabinetOS terminal runs it: no --pipe, the variable.
    let from_env = Command::new(CLI_EXE)
        .arg("pane")
        .env("CABINETOS_PIPE", core.pipe.token())
        .env_remove("CABINETOS_LOG")
        .output()
        .unwrap();
    assert_eq!(out(&from_env), format!("{}\n", shown(&right)));

    let json = cab(&core, &["pane", "--json"]);
    assert_eq!(code(&json), 0, "{}", err(&json));
    let whole: Value = serde_json::from_str(&out(&json)).unwrap();
    assert_eq!(whole["active"], "right");
    assert_eq!(whole["left"], shown(&left));
    assert_eq!(whole["right"], shown(&right));
    assert_eq!(whole["selection"], serde_json::json!([]));
    assert_eq!(whole["selection_total"], 0);
    assert_eq!(whole["cursor"], Value::Null);
    assert_eq!(whole["dual"], true);

    // A tool in front of a pane: that pane has no folder to print.
    let mut tool = pane(&left, None, &[]);
    tool.tabs[0].tool = Some("markdown-preview".to_owned());
    window.say(Pane::Left, tool, pane(&right, None, &[]));
    let none = cab(&core, &["pane"]);
    assert_eq!(code(&none), 2, "{}", err(&none));
    assert_eq!(out(&none), "");
    assert!(err(&none).contains("no folder"), "{}", err(&none));
    assert_eq!(code(&cab(&core, &["pane", "--right"])), 0);

    // The window closes: its state goes with it.
    drop(window);
    let deadline = Instant::now() + DEADLINE;
    while code(&cab(&core, &["pane", "--right"])) != 2 {
        assert!(Instant::now() < deadline, "the state stayed");
        std::thread::sleep(Duration::from_millis(30));
    }
}

#[test]
fn selection_prints_the_marked_rows_or_the_cursor_row() {
    let dir = scratch("selection");
    let core = start_core(dir.path());
    let (left, right) = folders(dir.path(), &["a.txt", "b b.txt", "ґ'c.txt"]);
    let [a, b, c] = ["a.txt", "b b.txt", "ґ'c.txt"].map(|name| left.join(name));
    let mut window = Window::connect(&core);

    let silent = cab(&core, &["selection"]);
    assert_eq!(code(&silent), 2, "{}", err(&silent));
    assert_eq!(out(&silent), "");

    // Marks win over the cursor; one path per line, in the pane's order.
    window.say(
        Pane::Left,
        pane(&left, Some(&c), &[&a, &c]),
        pane(&right, None, &[]),
    );
    let marked = cab(&core, &["selection"]);
    assert_eq!(code(&marked), 0, "{}", err(&marked));
    assert_eq!(
        lines(&out(&marked)),
        [shown(&a).as_str(), shown(&c).as_str()]
    );
    let json = cab(&core, &["selection", "--json"]);
    let list: Vec<String> = serde_json::from_str(&out(&json)).unwrap();
    assert_eq!(list, [shown(&a), shown(&c)]);

    // Only a cursor: the row the window's own commands would act on.
    window.say(
        Pane::Left,
        pane(&left, Some(&b), &[]),
        pane(&right, None, &[]),
    );
    assert_eq!(out(&cab(&core, &["selection"])), format!("{}\n", shown(&b)));

    // The other pane's marks are not the selection.
    window.say(
        Pane::Right,
        pane(&left, Some(&b), &[&a]),
        pane(&right, None, &[]),
    );
    let empty = cab(&core, &["selection"]);
    assert_eq!(code(&empty), 2, "an empty folder selects nothing");
    assert_eq!(out(&empty), "");
    assert!(
        err(&empty).contains("nothing is selected"),
        "{}",
        err(&empty)
    );
    let empty_json = cab(&core, &["selection", "--json"]);
    assert_eq!(code(&empty_json), 2);
    assert_eq!(
        serde_json::from_str::<Value>(&out(&empty_json)).unwrap(),
        serde_json::json!([]),
        "the list is still valid JSON"
    );

    // A selection the window cut at 1,000 rows is a failure, with nothing
    // printed that could pass for all of it.
    let mut cut = pane(&left, Some(&a), &[&a, &b]);
    cut.marked_total = Some(5000);
    window.say(Pane::Left, cut, pane(&right, None, &[]));
    let refused = cab(&core, &["selection"]);
    assert_eq!(code(&refused), 1, "{}", err(&refused));
    assert_eq!(out(&refused), "");
    assert!(
        err(&refused).contains("5000") && err(&refused).contains("part of a selection"),
        "{}",
        err(&refused)
    );
}

#[test]
fn copy_runs_the_selection_into_the_opposite_pane_and_skips_what_is_there() {
    let dir = scratch("copy");
    let core = start_core(dir.path());
    let (left, right) = folders(dir.path(), &["a.txt", "b b.txt", "c.txt"]);
    fs::write(right.join("b b.txt"), "right b b.txt, already there").unwrap();
    let [a, b] = ["a.txt", "b b.txt"].map(|name| left.join(name));
    let mut window = Window::connect(&core);
    window.say(
        Pane::Left,
        pane(&left, Some(&b), &[&a, &b]),
        pane(&right, None, &[]),
    );

    let copied = cab(&core, &["copy", "--selection", "--dest", "opposite_pane"]);
    assert_eq!(code(&copied), 0, "{}\n{}", out(&copied), err(&copied));
    let text = out(&copied);
    assert!(
        text.contains(&format!(
            "copying 2 selected items of the left pane to {}",
            shown(&right)
        )),
        "{text}"
    );
    assert!(text.contains("started"), "{text}");
    assert!(text.contains("completed"), "the end of the job: {text}");
    assert!(
        text.contains("1 skipped"),
        "the file that was there: {text}"
    );
    assert_eq!(
        fs::read_to_string(right.join("a.txt")).unwrap(),
        "left a.txt"
    );
    assert_eq!(
        fs::read_to_string(right.join("b b.txt")).unwrap(),
        "right b b.txt, already there",
        "the default policy of the selection form is skip"
    );
    assert!(!right.join("c.txt").exists(), "c.txt was not selected");
    assert!(left.join("a.txt").exists(), "a copy leaves its source");

    // The same selection with overwrite replaces it; with rename it keeps both.
    let overwritten = cab(
        &core,
        &[
            "copy",
            "--selection",
            "--dest",
            "opposite_pane",
            "--on-conflict",
            "overwrite",
        ],
    );
    assert_eq!(code(&overwritten), 0, "{}", err(&overwritten));
    assert_eq!(
        fs::read_to_string(right.join("b b.txt")).unwrap(),
        "left b b.txt"
    );
    let renamed = cab(
        &core,
        &[
            "copy",
            "--selection",
            "--dest",
            "opposite_pane",
            "--on-conflict",
            "rename",
        ],
    );
    assert_eq!(code(&renamed), 0, "{}", err(&renamed));
    assert_eq!(
        fs::read_to_string(right.join("b b (2).txt")).unwrap(),
        "left b b.txt"
    );

    // With the keyboard in the right pane, the opposite pane is the left.
    let c = left.join("c.txt");
    fs::write(right.join("only-right.txt"), "from the right").unwrap();
    let only_right = right.join("only-right.txt");
    window.say(
        Pane::Right,
        pane(&left, Some(&c), &[]),
        pane(&right, Some(&only_right), &[]),
    );
    let back = cab(&core, &["copy", "--selection", "--dest", "opposite_pane"]);
    assert_eq!(code(&back), 0, "{}", err(&back));
    assert_eq!(
        fs::read_to_string(left.join("only-right.txt")).unwrap(),
        "from the right"
    );
}

#[test]
fn with_one_pane_shown_the_opposite_pane_is_refused_and_a_path_still_works() {
    let dir = scratch("one-pane");
    let core = start_core(dir.path());
    let (left, right) = folders(dir.path(), &["a.txt", "b.txt"]);
    let [a, b] = ["a.txt", "b.txt"].map(|name| left.join(name));
    let mut window = Window::connect(&core);
    // The window shows only the left pane; the right pane's folder is still in its state.
    window.say_showing(
        Pane::Left,
        pane(&left, Some(&a), &[&a, &b]),
        pane(&right, None, &[]),
        false,
    );

    for verb in ["copy", "move"] {
        let refused = cab(&core, &[verb, "--selection", "--dest", "opposite_pane"]);
        assert_eq!(code(&refused), 1, "{verb}: {}", err(&refused));
        assert_eq!(out(&refused), "", "{verb} started nothing and said nothing");
        assert!(
            err(&refused).contains("the other pane is hidden; show both panes or name a path"),
            "{verb}: {}",
            err(&refused)
        );
    }
    assert_eq!(
        out(&cab(&core, &["jobs"])).trim(),
        "no jobs",
        "no job was queued"
    );
    assert!(a.exists() && b.exists(), "a refused move moved nothing");
    assert!(
        !right.join("a.txt").exists(),
        "a refused copy copied nothing"
    );

    // The pane commands still say what the state holds, and `--json` says why.
    assert_eq!(
        out(&cab(&core, &["pane", "--right"])),
        format!("{}\n", shown(&right))
    );
    let whole: Value = serde_json::from_str(&out(&cab(&core, &["pane", "--json"]))).unwrap();
    assert_eq!(whole["dual"], false);

    // Naming a path is what the message offers: it works with one pane shown.
    let named = cab(&core, &["copy", "--selection", "--dest", &shown(&right)]);
    assert_eq!(code(&named), 0, "{}\n{}", out(&named), err(&named));
    assert_eq!(
        fs::read_to_string(right.join("a.txt")).unwrap(),
        "left a.txt"
    );

    // Showing both panes again lifts the refusal.
    window.say(
        Pane::Left,
        pane(&left, Some(&b), &[]),
        pane(&right, None, &[]),
    );
    let both = cab(&core, &["copy", "--selection", "--dest", "opposite_pane"]);
    assert_eq!(code(&both), 0, "{}", err(&both));
    assert_eq!(
        fs::read_to_string(right.join("b.txt")).unwrap(),
        "left b.txt"
    );
}

#[test]
fn move_takes_the_cursor_row_and_a_path_is_read_against_the_shell_s_folder() {
    let dir = scratch("move");
    let core = start_core(dir.path());
    let (left, right) = folders(dir.path(), &["a.txt", "b.txt"]);
    let a = left.join("a.txt");
    let mut window = Window::connect(&core);
    window.say(
        Pane::Left,
        pane(&left, Some(&a), &[]),
        pane(&right, None, &[]),
    );

    // A relative `--dest`: the CLI resolves it against the folder the
    // shell is in, here `dir`, not against the core's.
    let moved = command(&core, &["move", "--selection", "--dest", "Правий 'pane'"])
        .current_dir(dir.path())
        .output()
        .unwrap();
    assert_eq!(code(&moved), 0, "{}\n{}", out(&moved), err(&moved));
    assert!(
        out(&moved).contains("moving 1 selected item of the left pane to"),
        "{}",
        out(&moved)
    );
    assert!(!a.exists(), "a move takes its source");
    assert_eq!(
        fs::read_to_string(right.join("a.txt")).unwrap(),
        "left a.txt"
    );
    assert!(left.join("b.txt").exists(), "the other row stayed");

    // An absolute path, and a destination that does not exist yet: the
    // core creates it.
    let b = left.join("b.txt");
    let fresh = dir.path().join("fresh");
    window.say(
        Pane::Left,
        pane(&left, Some(&b), &[]),
        pane(&right, None, &[]),
    );
    let to_fresh = cab(&core, &["move", "--selection", "--dest", &shown(&fresh)]);
    assert_eq!(code(&to_fresh), 0, "{}", err(&to_fresh));
    assert_eq!(
        fs::read_to_string(fresh.join("b.txt")).unwrap(),
        "left b.txt"
    );
}

#[test]
fn the_exit_codes_say_nothing_to_use_or_a_failure() {
    let dir = scratch("exit");
    let core = start_core(dir.path());
    let (left, right) = folders(dir.path(), &["a.txt"]);
    let a = left.join("a.txt");
    let copy = |dest: &str| cab(&core, &["copy", "--selection", "--dest", dest]);

    // No window: 2, and nothing was started.
    let nobody = copy("opposite_pane");
    assert_eq!(code(&nobody), 2, "{}", err(&nobody));
    assert!(err(&nobody).contains("no window"), "{}", err(&nobody));
    assert_eq!(code(&cab(&core, &["jobs"])), 0);
    assert_eq!(out(&cab(&core, &["jobs"])).trim(), "no jobs");

    let mut window = Window::connect(&core);
    // An empty folder: 2.
    window.say(Pane::Left, pane(&left, None, &[]), pane(&right, None, &[]));
    let empty = copy("opposite_pane");
    assert_eq!(code(&empty), 2, "{}", err(&empty));
    assert!(
        err(&empty).contains("nothing is selected"),
        "{}",
        err(&empty)
    );
    assert_eq!(
        code(&cab(
            &core,
            &["move", "--selection", "--dest", "opposite_pane"]
        )),
        2
    );

    // A destination that is a file: the core refuses the job, 1.
    window.say(
        Pane::Left,
        pane(&left, Some(&a), &[]),
        pane(&right, None, &[]),
    );
    let file = dir.path().join("a-file");
    fs::write(&file, "x").unwrap();
    let refused = copy(&shown(&file));
    assert_eq!(code(&refused), 1, "{}\n{}", out(&refused), err(&refused));
    assert!(err(&refused).contains("start_job"), "{}", err(&refused));
    assert!(a.exists());

    // An opposite pane that shows a tool has no folder to copy into: 1.
    let mut tool = pane(&right, None, &[]);
    tool.tabs[0].tool = Some("markdown-preview".to_owned());
    window.say(Pane::Left, pane(&left, Some(&a), &[]), tool);
    let no_folder = copy("opposite_pane");
    assert_eq!(code(&no_folder), 1, "{}", err(&no_folder));
    assert!(err(&no_folder).contains("--dest"), "{}", err(&no_folder));

    // A selection the window cut: 1, and nothing was started.
    let mut cut = pane(&left, Some(&a), &[&a]);
    cut.marked_total = Some(2000);
    window.say(Pane::Left, cut, pane(&right, None, &[]));
    let part = copy("opposite_pane");
    assert_eq!(code(&part), 1, "{}", err(&part));
    assert!(err(&part).contains("part of a selection"), "{}", err(&part));
    assert!(!right.join("a.txt").exists());

    // The options that only go with each other: a usage error is clap's own
    // exit code, 2, with its message.
    assert!(!cab(&core, &["copy", "--selection"]).status.success());
    assert!(
        !cab(&core, &["copy", "--selection", "--dest", "x", "a", "b"])
            .status
            .success()
    );
}

#[test]
fn with_no_core_the_selection_commands_fail_fast() {
    let pipe = PipeName::random();
    for args in [
        &["pane"][..],
        &["selection"],
        &["copy", "--selection", "--dest", "opposite_pane"],
    ] {
        let started = Instant::now();
        let output = Command::new(CLI_EXE)
            .args(["--pipe", pipe.token()])
            .args(args)
            .env_remove("CABINETOS_LOG")
            .output()
            .unwrap();
        assert_eq!(code(&output), 1, "{args:?}: {}", err(&output));
        assert!(
            err(&output).contains("cannot connect"),
            "{args:?}: {}",
            err(&output)
        );
        assert!(
            started.elapsed() < Duration::from_secs(4),
            "{args:?} took {:?}: a shell waits for it",
            started.elapsed()
        );
    }
}
