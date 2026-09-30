//! Phase 18 (protocol version 15), end to end: the user's programs and
//! Windows' own context menu, with the real `cabinetos-core.exe` on a random
//! pipe. Every file and folder lives under `%TEMP%\cabinetos-core-test\`, in
//! a folder removed at the end.

use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    CommandSource, CommandTarget, ErrorCode, Pane, PaneState, Request, Response, WindowPanes,
    WindowState, WindowTab,
};
use serde_json::json;
use tempfile::TempDir;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);

/// A running core, killed at the end of the test, with its own folder for
/// the configuration, the logs and the test's files.
struct Core {
    child: Child,
    pipe: PipeName,
    dir: TempDir,
}

impl Drop for Core {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

impl Core {
    fn files(&self) -> PathBuf {
        let files = self.dir.path().join("files");
        std::fs::create_dir_all(&files).unwrap();
        files
    }

    fn log(&self) -> String {
        let mut text = String::new();
        for entry in std::fs::read_dir(self.dir.path().join("logs")).unwrap() {
            let path = entry.unwrap().path();
            if path
                .file_name()
                .is_some_and(|name| name.to_string_lossy().starts_with("core."))
            {
                text.push_str(&std::fs::read_to_string(path).unwrap_or_default());
            }
        }
        text
    }
}

/// A core whose `cabinetos.json` is `config`, written before it starts.
fn start_core(config: &serde_json::Value) -> Core {
    let root = std::env::temp_dir().join("cabinetos-core-test");
    std::fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("phase18")
        .tempdir_in(root)
        .unwrap();
    let config_path = dir.path().join("config").join("cabinetos.json");
    std::fs::create_dir_all(config_path.parent().unwrap()).unwrap();
    std::fs::write(&config_path, config.to_string()).unwrap();
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(&config_path)
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
        .env("CABINETOS_PLUGINS_DIR", dir.path().join("plugins"))
        .env(
            "CABINETOS_PLUGINS_DATA_DIR",
            dir.path().join("plugins-data"),
        )
        .env("CABINETOS_THEMES_DIR", dir.path().join("themes"))
        .env("CABINETOS_UNDO_DIR", dir.path().join("undo"))
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_LOG_STDERR")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    Core { child, pipe, dir }
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

async fn greeted(core: &Core, name: &str) -> PipeClient {
    let mut client = connect(&core.pipe).await;
    let _events = client.events().unwrap();
    let welcome = client.hello(name).await.unwrap();
    assert!(
        matches!(welcome.body, Response::Welcome { .. }),
        "{welcome:?}"
    );
    client
}

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
}

fn error_code(response: &Response) -> Option<ErrorCode> {
    match response {
        Response::Error { code, .. } => Some(*code),
        _ => None,
    }
}

fn text(path: &Path) -> String {
    path.to_str().unwrap().to_owned()
}

fn run(command: &str) -> Request {
    Request::ExecuteCommand {
        command: command.to_owned(),
        args: serde_json::Value::Null,
    }
}

/// The right pane in `folder`, its cursor on `cursor`, `marked` marked.
fn window(folder: &Path, cursor: Option<&Path>, marked: &[&Path]) -> Request {
    Request::WindowState(WindowState {
        active_pane: Pane::Right,
        panes: WindowPanes {
            left: PaneState::default(),
            right: PaneState {
                tabs: vec![WindowTab {
                    path: text(folder),
                    locked: false,
                    tool: None,
                }],
                active: 0,
                cursor: cursor.map(text),
                marked: marked.iter().map(|path| text(path)).collect(),
            },
        },
    })
}

/// The lines of a file a program writes, once it is there and complete.
fn wait_for_lines(path: &Path) -> Vec<String> {
    let deadline = Instant::now() + Duration::from_secs(20);
    loop {
        if let Ok(text) = std::fs::read_to_string(path)
            && text.ends_with('\n')
        {
            return text.lines().map(str::to_owned).collect();
        }
        assert!(
            Instant::now() < deadline,
            "{} was not written",
            path.display()
        );
        std::thread::sleep(Duration::from_millis(50));
    }
}

/// A batch file that writes each of its arguments on a line of its own,
/// next to itself, then the folder it ran in, and exits.
fn recorder(folder: &Path) -> PathBuf {
    let stub = folder.join("record args.cmd");
    std::fs::write(
        &stub,
        "@echo off\r\n(for %%a in (%*) do @echo %%~a\r\n@echo cwd=%CD%\r\n) >\"%~dp0args.tmp\"\r\nmove /y \"%~dp0args.tmp\" \"%~dp0args.txt\" >nul\r\n",
    )
    .unwrap();
    stub
}

/// `programs` entries become `program.<name>` commands that the core runs:
/// the tokens come from the window's state, the program starts in the
/// active pane's folder, and a key in `keybindings` binds one.
#[tokio::test]
#[expect(clippy::too_many_lines, reason = "one scenario, step by step")]
async fn a_program_runs_with_the_paths_of_the_window_and_is_a_command() {
    let scratch = tempfile::Builder::new()
        .prefix("phase18-programs")
        .tempdir_in(std::env::temp_dir())
        .unwrap();
    let stub = recorder(scratch.path());
    let core = start_core(&json!({
        "programs": [
            {"name": "record", "title": "Record the Paths", "command": text(&stub),
             "args": ["--path={path}", "{selection}", "{cwd}"]},
            {"name": "gone", "command": "cabinetos-no-such-program", "args": ["{path}"]}
        ],
        "keybindings": [{"command": "program.record", "keys": "ctrl+alt+r"}]
    }));
    let folder = core.files().join("photos 2026");
    std::fs::create_dir_all(&folder).unwrap();
    let a = folder.join("a b.jpg");
    let c = folder.join("c.jpg");
    std::fs::write(&a, "a").unwrap();
    std::fs::write(&c, "c").unwrap();
    let mut client = greeted(&core, "phase18").await;

    let Response::Commands { commands } = ask(&mut client, Request::ListCommands).await else {
        panic!("expected commands")
    };
    let record = commands
        .iter()
        .find(|command| command.id == "program.record")
        .unwrap();
    assert_eq!(
        (
            record.title.as_str(),
            record.category.as_str(),
            record.target,
            record.keys.clone()
        ),
        (
            "Record the Paths",
            "Programs",
            CommandTarget::Core,
            vec!["ctrl+alt+r".to_owned()]
        )
    );
    assert_eq!(
        record.source,
        CommandSource::Program {
            name: "record".to_owned()
        }
    );

    // Before the window said what it shows, the tokens stand for nothing.
    let reply = ask(&mut client, run("program.record")).await;
    assert_eq!(error_code(&reply), Some(ErrorCode::NoWindow), "{reply:?}");

    assert_eq!(
        ask(&mut client, window(&folder, Some(&a), &[&a, &c])).await,
        Response::Ok
    );
    let reply = ask(&mut client, run("program.record")).await;
    let Response::CommandResult { result } = &reply else {
        panic!("expected command_result, got {reply:?}")
    };
    assert_eq!(result["program"], "record");
    let lines = wait_for_lines(&scratch.path().join("args.txt"));
    assert_eq!(
        lines,
        [
            format!("--path={}", text(&a)),
            text(&a),
            text(&c),
            text(&folder),
            format!("cwd={}", text(&folder)),
        ]
    );

    // A program not in the list, one found nowhere, one without a cursor.
    let reply = ask(&mut client, run("program.nosuch")).await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::UnknownProgram),
        "{reply:?}"
    );
    let reply = ask(&mut client, run("program.gone")).await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::ProgramRefused),
        "{reply:?}"
    );
    ask(&mut client, window(&folder, None, &[])).await;
    let reply = ask(&mut client, run("program.record")).await;
    let Response::Error { code, message } = &reply else {
        panic!("{reply:?}")
    };
    assert_eq!(*code, ErrorCode::ProgramRefused);
    assert!(message.contains("{path}"), "{message}");

    // Each refusal is a warning that names the program.
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        let log = core.log();
        let refused: Vec<&str> = log
            .lines()
            .filter(|line| line.contains("program refused") && line.contains("\"WARN\""))
            .collect();
        if refused.len() >= 4 {
            for name in ["nosuch", "gone", "record"] {
                assert!(
                    refused
                        .iter()
                        .any(|line| line.contains(&format!("\"program\":\"{name}\""))),
                    "{name}: {refused:?}"
                );
            }
            break;
        }
        assert!(Instant::now() < deadline, "{log}");
        tokio::time::sleep(Duration::from_millis(100)).await;
    }
}

/// A program added to the file while the core runs is a command at once;
/// one removed is gone, and running it says so.
#[tokio::test]
async fn programs_follow_the_file() {
    let core = start_core(&json!({}));
    let mut client = greeted(&core, "phase18").await;
    let has = |commands: &[cabinetos_protocol::CommandInfo], id: &str| {
        commands.iter().any(|command| command.id == id)
    };
    let Response::Commands { commands } = ask(&mut client, Request::ListCommands).await else {
        panic!("expected commands")
    };
    assert!(!has(&commands, "program.notes"));
    assert_eq!(
        ask(
            &mut client,
            Request::SetValue {
                path: "programs".to_owned(),
                value: json!([{"name": "notes", "command": "notepad.exe", "args": ["{path}"]}]),
            },
        )
        .await,
        Response::Ok
    );
    let Response::Commands { commands } = ask(&mut client, Request::ListCommands).await else {
        panic!("expected commands")
    };
    assert!(has(&commands, "program.notes"));
    // A bad token is refused with the setting, and the program stays.
    let reply = ask(
        &mut client,
        Request::SetValue {
            path: "programs".to_owned(),
            value: json!([{"name": "notes", "command": "notepad.exe", "args": ["{file}"]}]),
        },
    )
    .await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::ConfigError),
        "{reply:?}"
    );
    assert_eq!(
        ask(
            &mut client,
            Request::SetValue {
                path: "programs".to_owned(),
                value: json!([]),
            },
        )
        .await,
        Response::Ok
    );
    let Response::Commands { commands } = ask(&mut client, Request::ListCommands).await else {
        panic!("expected commands")
    };
    assert!(!has(&commands, "program.notes"));
    let reply = ask(&mut client, run("program.notes")).await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::UnknownProgram),
        "{reply:?}"
    );
}
