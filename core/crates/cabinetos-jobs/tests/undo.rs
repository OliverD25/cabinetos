//! The undo journal on real files: what jobs record, how an undo reverses
//! them, and what it refuses. Every test works in its own folder under
//! `%TEMP%\cabinetos-jobs-test\`, with its own undo folder inside it.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, Receiver};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use cabinetos_jobs::{EngineConfig, EventSink, JobQueueManager, UndoStarted};
use cabinetos_protocol::{
    ConflictPolicy, ErrorCode, Event, JobKind, JobOptions, JobRequest, JobState,
};
use serde_json::Value;
use tempfile::TempDir;

const JOB_DEADLINE: Duration = Duration::from_secs(60);

fn scratch() -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-jobs-test");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("undo")
        .tempdir_in(root)
        .unwrap()
}

struct Engine {
    manager: JobQueueManager,
    events: Mutex<Receiver<Event>>,
    undo_dir: PathBuf,
}

fn engine(dir: &Path) -> Engine {
    let undo_dir = dir.join("undo");
    let (sender, events) = mpsc::channel();
    let sink: EventSink = Arc::new(move |event| {
        let _ = sender.send(event);
    });
    let config = EngineConfig {
        undo_dir: Some(undo_dir.clone()),
        ..EngineConfig::default()
    };
    Engine {
        manager: JobQueueManager::new(config, sink),
        events: Mutex::new(events),
        undo_dir,
    }
}

impl Engine {
    fn run(&self, kind: JobKind, sources: &[&Path], destination: Option<&Path>) -> u64 {
        let job = self
            .manager
            .start(JobRequest {
                kind,
                sources: sources
                    .iter()
                    .map(|path| path.display().to_string())
                    .collect(),
                destination: destination.map(|path| path.display().to_string()),
                options: JobOptions {
                    on_conflict: ConflictPolicy::Overwrite,
                    ..JobOptions::default()
                },
            })
            .unwrap();
        assert_eq!(self.wait(job), JobState::Completed);
        job
    }

    fn wait(&self, job: u64) -> JobState {
        let events = self.events.lock().unwrap();
        let deadline = Instant::now() + JOB_DEADLINE;
        loop {
            let left = deadline.saturating_duration_since(Instant::now());
            match events.recv_timeout(left) {
                Ok(Event::JobStateChanged { job_id, state })
                    if job_id == job && state.is_terminal() =>
                {
                    return state;
                }
                Ok(_) => {}
                Err(error) => panic!("job {job} did not end: {error}"),
            }
        }
    }

    fn undo(&self, job: Option<u64>) -> UndoStarted {
        let started = self.manager.undo(job).unwrap();
        assert_eq!(self.wait(started.job_id), JobState::Completed);
        started
    }

    /// The journal's lines, parsed.
    fn journal(&self) -> Vec<Value> {
        fs::read_to_string(self.undo_dir.join("journal.jsonl"))
            .unwrap_or_default()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect()
    }

    fn line_of(&self, job: u64) -> Value {
        self.journal()
            .into_iter()
            .find(|line| line["job"] == job)
            .unwrap_or_else(|| panic!("no journal line for job {job}"))
    }
}

fn text(path: &Path) -> String {
    fs::read_to_string(path).unwrap()
}

#[test]
fn a_copy_over_files_is_undone_and_the_replaced_file_comes_back() {
    let dir = scratch();
    let engine = engine(dir.path());
    let from = dir.path().join("from");
    let to = dir.path().join("to");
    fs::create_dir_all(from.join("sub")).unwrap();
    fs::create_dir_all(&to).unwrap();
    fs::write(from.join("a.txt"), "new a").unwrap();
    fs::write(from.join("sub").join("b.txt"), "b").unwrap();
    fs::write(to.join("a.txt"), "old a").unwrap();

    let copy = engine.run(
        JobKind::Copy,
        &[&from.join("a.txt"), &from.join("sub")],
        Some(&to),
    );
    assert_eq!(text(&to.join("a.txt")), "new a");
    let line = engine.line_of(copy);
    assert_eq!(line["kind"], "copy");
    assert_eq!(line["state"], "completed");
    let entries = line["entries"].as_array().unwrap();
    let saved = entries
        .iter()
        .find(|entry| entry["op"] == "overwritten")
        .expect("the replaced file is recorded")["saved"]
        .as_str()
        .unwrap()
        .to_owned();
    assert_eq!(
        saved,
        engine
            .undo_dir
            .join(copy.to_string())
            .join("0")
            .display()
            .to_string()
    );
    assert_eq!(text(Path::new(&saved)), "old a");
    assert!(
        entries.iter().any(|entry| entry["op"] == "created"
            && entry["path"] == to.join("sub").display().to_string())
    );

    let undo = engine.undo(Some(copy));
    assert_eq!(undo.undoes, copy);
    assert!(undo.left.is_empty(), "{:?}", undo.left);
    assert_eq!(text(&to.join("a.txt")), "old a");
    assert!(
        !to.join("sub").exists(),
        "the copied folder went to the Recycle Bin"
    );
    assert!(!Path::new(&saved).exists(), "the saved copy was used");
    assert_eq!(engine.line_of(undo.job_id)["undoes"], copy);

    let again = engine.manager.undo(Some(copy)).unwrap_err();
    assert_eq!(again.code, ErrorCode::NotUndoable);
    assert!(again.message.contains("undone already"), "{again}");
}

#[test]
fn a_move_goes_back_and_the_last_job_is_the_default() {
    let dir = scratch();
    let engine = engine(dir.path());
    let from = dir.path().join("from");
    let to = dir.path().join("to");
    fs::create_dir_all(from.join("folder")).unwrap();
    fs::create_dir_all(&to).unwrap();
    fs::write(from.join("m.txt"), "m").unwrap();
    fs::write(from.join("folder").join("inner.txt"), "i").unwrap();

    let first = engine.run(JobKind::Move, &[&from.join("m.txt")], Some(&to));
    let second = engine.run(JobKind::Move, &[&from.join("folder")], Some(&to));
    assert!(second > first);
    assert!(to.join("folder").join("inner.txt").exists());

    let undo = engine.undo(None);
    assert_eq!(undo.undoes, second, "the newest job");
    assert!(from.join("folder").join("inner.txt").exists());
    assert!(
        to.join("m.txt").exists(),
        "the first move stays until it is undone"
    );
    let undo = engine.undo(None);
    assert_eq!(undo.undoes, first, "an undo job is never the default");
    assert_eq!(text(&from.join("m.txt")), "m");
    assert!(!to.join("m.txt").exists());
    let none = engine.manager.undo(None).unwrap_err();
    assert_eq!(none.code, ErrorCode::NoSuchJob);
}

#[test]
fn a_delete_cannot_be_undone_and_says_what_to_do() {
    let dir = scratch();
    let engine = engine(dir.path());
    let doomed = dir.path().join("doomed.txt");
    let gone = dir.path().join("gone.txt");
    fs::write(&doomed, "d").unwrap();
    fs::write(&gone, "g").unwrap();

    let recycled = engine.run(JobKind::Delete { permanent: false }, &[&doomed], None);
    let refused = engine.manager.undo(Some(recycled)).unwrap_err();
    assert_eq!(refused.code, ErrorCode::NotUndoable);
    assert!(refused.message.contains("Recycle Bin"), "{refused}");
    let deleted = engine.run(JobKind::Delete { permanent: true }, &[&gone], None);
    let refused = engine.manager.undo(Some(deleted)).unwrap_err();
    assert!(refused.message.contains("for good"), "{refused}");
    assert_eq!(
        engine.line_of(deleted)["entries"],
        serde_json::json!([{"op": "deleted", "path": gone.display().to_string()}])
    );
    let unknown = engine.manager.undo(Some(u64::MAX)).unwrap_err();
    assert_eq!(unknown.code, ErrorCode::NoSuchJob);
}

#[test]
fn the_journal_keeps_200_jobs_and_new_ids_follow_it() {
    let dir = scratch();
    let undo_dir = dir.path().join("undo");
    fs::create_dir_all(undo_dir.join("5000")).unwrap();
    fs::write(undo_dir.join("5000").join("0"), "saved").unwrap();
    let lines: String = (5000..5200)
        .map(|job| {
            format!(
                r#"{{"job":{job},"kind":"steps","state":"completed","ended_ms":1,"entries":[{{"op":"created","path":"C:\\nowhere\\{job}"}}]}}"#
            ) + "\n"
        })
        .collect();
    fs::write(undo_dir.join("journal.jsonl"), lines).unwrap();

    let engine = engine(dir.path());
    let file = dir.path().join("f.txt");
    let to = dir.path().join("to");
    fs::write(&file, "f").unwrap();
    let job = engine.run(JobKind::Copy, &[&file], Some(&to));
    assert!(job >= 5200, "job IDs follow the journal: {job}");

    let journal = engine.journal();
    assert_eq!(journal.len(), 200);
    assert_eq!(journal[0]["job"], 5001, "the oldest line went");
    assert_eq!(journal[199]["job"], job);
    assert!(!undo_dir.join("5000").exists(), "with its saved copies");
}
