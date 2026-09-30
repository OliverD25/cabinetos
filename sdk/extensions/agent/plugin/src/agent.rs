//! The agent: one conversation with the model, from a request to a preview
//! (or an answer), under the tier's rules, and the undo that reverses what
//! was applied. Everything here works through the [`Host`] trait, so the
//! whole loop runs as an ordinary Rust test with the fake provider.

use std::cell::Cell;
use std::rc::Rc;

use serde_json::{Value, json};

use crate::audit::{self, CommandRecord, Entry, Round};
use crate::cmdline::{self, Access, AgentCommand};
use crate::host::{Host, Level};
use crate::plan::{self, Row};
use crate::prompt;
use crate::provider::{self, Completion, Message};
use crate::settings::Settings;
use crate::state::{self, Applied, Saved};
use crate::tier::{Decision, Tier};
use crate::tools;

/// How many times the model may answer one request: it asks to look, is
/// told what it saw, and may look again.
pub const MAX_ROUNDS: usize = 3;

/// Who asked.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Source {
    /// `agent.ask`, from the prompt box.
    Ask,
    /// `agent.chat`, from the chat page.
    Chat,
    /// A watch folder's rule, on a new file.
    Rule,
}

impl Source {
    /// The word in the audit log.
    #[must_use]
    pub fn word(self) -> &'static str {
        match self {
            Self::Ask => "ask",
            Self::Chat => "chat",
            Self::Rule => "rule",
        }
    }
}

/// One request to the agent.
pub struct Task<'a> {
    pub source: Source,
    /// What the user wants, or for a rule, the rule with the file's name.
    pub prompt: &'a str,
    /// The files the user pointed at.
    pub selected: &'a [String],
}

/// One command line and what became of it, for the chat page.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CommandOutcome {
    pub line: String,
    /// `read`, `write` or `invalid`.
    pub kind: &'static str,
    /// `ok`, `error: ...`, `proposed`, `applied`, `blocked ...`, `not run ...`.
    pub status: String,
}

impl CommandOutcome {
    #[must_use]
    pub fn to_json(&self) -> Value {
        json!({ "line": self.line, "kind": self.kind, "status": self.status })
    }
}

/// What a request came to.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Outcome {
    /// The model's words.
    pub text: String,
    /// Every command line of the last round that had any, and every round
    /// before it that looked.
    pub commands: Vec<CommandOutcome>,
    /// The preview proposed, when there is one.
    pub preview: Option<String>,
    /// The jobs started, when tier 3 applied it.
    pub jobs: Vec<u64>,
    /// The changes in words, one per row.
    pub changes: Vec<String>,
    /// A line for the status bar, when something was done for the user.
    pub notice: Option<String>,
    /// The tier it ran under, 1 to 3.
    pub tier: u8,
}

/// What an undo did.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Undone {
    /// The jobs that were reversed.
    pub undoes: Vec<u64>,
    /// What could not be brought back, with the reason in words.
    pub left: Vec<(String, String)>,
    /// One sentence for the status bar.
    pub text: String,
}

/// The agent's state.
pub struct Agent {
    /// The folders it may read and change (the plugin's `fs:read` roots),
    /// as Windows paths.
    pub roots: Vec<String>,
    /// The folders it may watch, as Windows paths.
    pub watch_roots: Vec<String>,
    fake_cursor: Rc<Cell<usize>>,
    saved: Saved,
    /// The preview it proposed last, until it is applied or cancelled.
    pending_preview: Option<String>,
    /// The tier `settings.tier` had when last read: a change of it wins
    /// over what `agent.tier` set.
    settings_tier: Option<Tier>,
}

/// What a round of the model's reply leads to.
enum Next {
    /// Nothing more to ask the model.
    Done,
    /// Tell the model this, and let it answer again.
    Ask(String),
}

impl Agent {
    /// A new agent with the state the last run saved.
    #[must_use]
    pub fn new(host: &dyn Host, roots: Vec<String>, watch_roots: Vec<String>) -> Self {
        Self {
            roots,
            watch_roots,
            fake_cursor: Rc::new(Cell::new(0)),
            saved: state::load(host),
            pending_preview: None,
            settings_tier: None,
        }
    }

    /// The settings now: `plugins.agent.settings` of the configuration.
    pub fn settings(&mut self, host: &dyn Host) -> Result<Settings, String> {
        let value = host
            .config("plugins.agent.settings")
            .and_then(|text| serde_json::from_str::<Value>(&text).ok())
            .unwrap_or_else(|| json!({}));
        let settings = Settings::from_value(&value)?;
        // A change of `settings.tier` is the user's newest word.
        if self.settings_tier.is_some_and(|seen| seen != settings.tier)
            && self.saved.tier.take().is_some()
        {
            self.save(host);
        }
        self.settings_tier = Some(settings.tier);
        Ok(settings)
    }

    /// The tier in effect: what `agent.tier` set, else the settings'.
    #[must_use]
    pub fn tier_of(&self, settings: &Settings) -> Tier {
        self.saved.tier.unwrap_or(settings.tier)
    }

    /// Sets the tier for now and for the next start (`agent.tier`).
    pub fn set_tier(&mut self, host: &dyn Host, tier: Tier) {
        self.saved.tier = Some(tier);
        self.save(host);
    }

    /// The jobs of the last applied preview, when known.
    #[must_use]
    pub fn last_applied(&self) -> Option<&Applied> {
        self.saved.last.as_ref()
    }

    fn save(&self, host: &dyn Host) {
        if let Err(error) = state::save(host, &self.saved) {
            host.log(
                Level::Warn,
                &format!("cannot save the agent's state: {error}"),
            );
        }
    }

    /// A preview this plugin proposed was applied or cancelled (the core
    /// tells it, `docs/plugins.md`).
    pub fn preview_event(&mut self, host: &dyn Host, name: &str, payload: &Value) {
        let Some(preview) = payload["preview"].as_str() else {
            return;
        };
        if self.pending_preview.as_deref() == Some(preview) {
            self.pending_preview = None;
        }
        if name == "preview-applied" {
            let jobs: Vec<u64> = payload["jobs"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(Value::as_u64)
                .collect();
            if !jobs.is_empty() {
                self.saved.last = Some(Applied {
                    preview: preview.to_owned(),
                    jobs,
                });
                self.save(host);
            }
        }
    }

    // ----- One request -----

    /// Runs one request: the conversation with the model, the tier's
    /// decision about the changes it asks for, and the audit entry.
    pub fn run(&mut self, host: &dyn Host, task: &Task<'_>) -> Result<Outcome, String> {
        let settings = self.settings(host)?;
        let tier = self.tier_of(&settings);
        let mut entry = Entry {
            time_ms: host.now_ms(),
            tier: tier.number(),
            source: task.source.word(),
            prompt: task.prompt.to_owned(),
            state: None,
            provider: settings.provider.name().to_owned(),
            model: settings.model.clone(),
            rounds: Vec::new(),
            preview: None,
            jobs: Vec::new(),
            error: None,
        };
        let result = self.converse(host, task, &settings, tier, &mut entry);
        if let Err(error) = &result {
            entry.error = Some(error.clone());
        }
        if let Err(error) = audit::write(host, &entry) {
            host.log(Level::Warn, &format!("cannot write the audit log: {error}"));
        }
        result
    }

    fn converse(
        &mut self,
        host: &dyn Host,
        task: &Task<'_>,
        settings: &Settings,
        tier: Tier,
        entry: &mut Entry,
    ) -> Result<Outcome, String> {
        let provider = provider::make(settings, host, &self.fake_cursor)?;
        let window = tools::window_state(host);
        let window_text = window.as_ref().map(tools::state_text);
        entry.state = window;
        let system = prompt::system_prompt(tier, &self.roots);
        let mut messages = vec![Message::user(prompt::user_message(
            task.prompt,
            window_text.as_deref(),
            task.selected,
        ))];
        let mut outcome = Outcome {
            tier: tier.number(),
            ..Outcome::default()
        };
        for round in 1..=MAX_ROUNDS {
            let reply = provider.complete(
                host,
                &Completion {
                    system: system.clone(),
                    messages: messages.clone(),
                },
            )?;
            let parsed = cmdline::split_reply(&reply);
            outcome.text.clone_from(&parsed.text);
            let mut record = Round {
                reply: reply.clone(),
                commands: Vec::new(),
            };
            let next = self.round(
                host,
                task,
                tier,
                &parsed.lines,
                round,
                &mut outcome,
                &mut record,
            )?;
            entry.rounds.push(record);
            match next {
                Next::Done => break,
                Next::Ask(feedback) => {
                    messages.push(Message::assistant(reply));
                    messages.push(Message::user(feedback));
                }
            }
        }
        entry.preview.clone_from(&outcome.preview);
        entry.jobs.clone_from(&outcome.jobs);
        if outcome.text.is_empty()
            && let Some(preview) = &outcome.preview
        {
            outcome.text = format!(
                "{} changes for you to approve ({preview}).",
                outcome.changes.len()
            );
        }
        Ok(outcome)
    }

    /// Runs what one reply of the model asks: the commands that look run
    /// now; the ones that change files wait for the tier.
    #[allow(clippy::too_many_arguments, clippy::too_many_lines)]
    fn round(
        &mut self,
        host: &dyn Host,
        task: &Task<'_>,
        tier: Tier,
        lines: &[String],
        round: usize,
        outcome: &mut Outcome,
        record: &mut Round,
    ) -> Result<Next, String> {
        // What the model is told next: the output of what looked, and the
        // reason a line did not run.
        let mut results: Vec<(String, Result<String, String>)> = Vec::new();
        let mut notes: Vec<String> = Vec::new();
        // The lines that change files, once checked: the place of each
        // in `statuses`.
        let mut writes: Vec<(usize, AgentCommand, Vec<Row>)> = Vec::new();
        let mut statuses: Vec<CommandOutcome> = Vec::new();

        for line in lines {
            match cmdline::parse_line(line) {
                Err(error) => {
                    statuses.push(CommandOutcome {
                        line: line.clone(),
                        kind: "invalid",
                        status: format!("error: {error}"),
                    });
                    results.push((line.clone(), Err(error)));
                }
                Ok(command) => match command.access() {
                    Access::Read => {
                        let output = tools::run_read(host, &command, &self.roots);
                        statuses.push(CommandOutcome {
                            line: line.clone(),
                            kind: "read",
                            status: match &output {
                                Ok(_) => "ok".to_owned(),
                                Err(error) => format!("error: {error}"),
                            },
                        });
                        results.push((line.clone(), output));
                    }
                    Access::Write => {
                        let rows = match &command {
                            AgentCommand::Undo { .. } => Ok(Vec::new()),
                            other => plan::rows_of(other, &self.roots),
                        };
                        match rows {
                            Ok(rows) => {
                                writes.push((statuses.len(), command, rows));
                                statuses.push(CommandOutcome {
                                    line: line.clone(),
                                    kind: "write",
                                    status: "pending".to_owned(),
                                });
                            }
                            Err(error) => {
                                statuses.push(CommandOutcome {
                                    line: line.clone(),
                                    kind: "write",
                                    status: format!("error: {error}"),
                                });
                                results.push((line.clone(), Err(error)));
                            }
                        }
                    }
                },
            }
        }

        let must_answer = !results.is_empty();
        let mut next = Next::Done;
        if must_answer && !writes.is_empty() {
            notes.push(
                "Commands that change files were not run, because this reply also had commands that look or that failed. Send them again, in a reply of their own, once you have what you need."
                    .to_owned(),
            );
            for (place, ..) in &writes {
                statuses[*place].status = "not run: sent with commands that look".to_owned();
            }
            writes.clear();
        }
        if must_answer {
            if round < MAX_ROUNDS {
                next = Next::Ask(prompt::results_message(&results, &notes));
            } else {
                outcome.text = format!("{} (Stopped after {MAX_ROUNDS} rounds.)", outcome.text)
                    .trim()
                    .to_owned();
            }
        } else if !writes.is_empty() {
            next = self.changes(host, task, tier, writes, round, &mut statuses, outcome)?;
        }
        for status in &statuses {
            record.commands.push(CommandRecord {
                line: status.line.clone(),
                kind: status.kind,
                outcome: status.status.clone(),
            });
        }
        outcome.commands.extend(statuses);
        Ok(next)
    }

    /// The tier's decision about the commands that change files.
    #[allow(clippy::too_many_arguments, clippy::too_many_lines)]
    fn changes(
        &mut self,
        host: &dyn Host,
        task: &Task<'_>,
        tier: Tier,
        writes: Vec<(usize, AgentCommand, Vec<Row>)>,
        round: usize,
        statuses: &mut [CommandOutcome],
        outcome: &mut Outcome,
    ) -> Result<Next, String> {
        let mut rows: Vec<Row> = Vec::new();
        let mut row_places: Vec<usize> = Vec::new();
        let mut undos: Vec<(usize, Option<u64>)> = Vec::new();
        for (place, command, command_rows) in writes {
            match (&command, tier.decide(Access::Write)) {
                (_, Decision::Refuse) => {
                    statuses[place].status = "blocked: the Advisor tier changes nothing".to_owned();
                }
                (AgentCommand::Undo { job }, Decision::Apply) => undos.push((place, *job)),
                (AgentCommand::Undo { .. }, _) => {
                    statuses[place].status =
                        "blocked: undo is yours to run (agent.undo) at this tier".to_owned();
                }
                _ => {
                    row_places.extend(command_rows.iter().map(|_| place));
                    rows.extend(command_rows);
                }
            }
        }
        for (place, job) in undos {
            statuses[place].status = match self.undo(host, job) {
                Ok(undone) => {
                    outcome.notice = Some(undone.text.clone());
                    format!("applied: {}", undone.text)
                }
                Err(error) => format!("error: {error}"),
            };
        }
        if rows.is_empty() {
            return Ok(Next::Done);
        }
        if rows.len() > plan::MAX_ROWS {
            let message = format!(
                "{} changes are more than one preview holds ({}); ask for fewer at a time",
                rows.len(),
                plan::MAX_ROWS
            );
            return self.refused(&row_places, statuses, &message, round);
        }

        // One pending preview at a time: the last proposal replaces it.
        if let Some(old) = self.pending_preview.take() {
            let _ = host.core_request(&json!({ "type": "preview_cancel", "preview": old }));
        }
        let reply = host.core_request(&json!({
            "type": "preview_listing",
            "title": plan::title(task.prompt),
            "rows": rows.iter().map(Row::to_json).collect::<Vec<_>>(),
        }))?;
        if let Some(error) = tools::reply_error(&reply) {
            return self.refused(
                &row_places,
                statuses,
                &format!("the core refused the changes: {error}"),
                round,
            );
        }
        let Some(preview) = reply["preview"].as_str().map(str::to_owned) else {
            return Err("the core's answer to preview_listing named no preview".to_owned());
        };
        outcome.changes = rows.iter().map(plan::describe).collect();
        outcome.preview = Some(preview.clone());
        self.pending_preview = Some(preview.clone());

        if tier.decide(Access::Write) == Decision::Apply {
            let applied =
                host.core_request(&json!({ "type": "preview_apply", "preview": preview }))?;
            if let Some(error) = tools::reply_error(&applied) {
                for place in &row_places {
                    statuses[*place].status = format!("proposed, but not applied: {error}");
                }
                return Ok(Next::Done);
            }
            let jobs: Vec<u64> = applied["jobs"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(Value::as_u64)
                .collect();
            self.pending_preview = None;
            self.saved.last = Some(Applied {
                preview: preview.clone(),
                jobs: jobs.clone(),
            });
            self.save(host);
            outcome.jobs = jobs;
            outcome.notice = Some(format!(
                "Applied {} change{}. agent.undo reverses them.",
                rows.len(),
                if rows.len() == 1 { "" } else { "s" }
            ));
            for place in &row_places {
                statuses[*place].status = "applied".to_owned();
            }
        } else {
            for place in &row_places {
                statuses[*place].status = "proposed".to_owned();
            }
        }
        Ok(Next::Done)
    }

    /// The changes were refused (too many, or by the core): the model may
    /// correct them while it has rounds left.
    fn refused(
        &self,
        row_places: &[usize],
        statuses: &mut [CommandOutcome],
        message: &str,
        round: usize,
    ) -> Result<Next, String> {
        for place in row_places {
            statuses[*place].status = format!("error: {message}");
        }
        if round < MAX_ROUNDS {
            let results: Vec<(String, Result<String, String>)> = row_places
                .iter()
                .map(|place| (statuses[*place].line.clone(), Err(message.to_owned())))
                .collect();
            Ok(Next::Ask(prompt::results_message(&results, &[])))
        } else {
            Ok(Next::Done)
        }
    }

    // ----- Undo -----

    /// Reverses a job, or the jobs of the last applied preview, last first
    /// (`undo_job`). Without a job and without a preview it knows, the core
    /// undoes the newest job that is not an undo itself.
    pub fn undo(&mut self, host: &dyn Host, job: Option<u64>) -> Result<Undone, String> {
        let targets: Vec<Option<u64>> = match (job, &self.saved.last) {
            (Some(job), _) => vec![Some(job)],
            (None, Some(last)) if !last.jobs.is_empty() => {
                last.jobs.iter().rev().copied().map(Some).collect()
            }
            (None, _) => vec![None],
        };
        let whole_preview = job.is_none() && self.saved.last.is_some();
        let mut undone = Undone::default();
        for (index, target) in targets.iter().enumerate() {
            let mut request = json!({ "type": "undo_job" });
            if let Some(target) = target {
                request["job"] = json!(target);
            }
            let reply = host.core_request(&request)?;
            if let Some(error) = tools::reply_error(&reply) {
                if index == 0 {
                    return Err(error);
                }
                undone.text = format!(
                    "Undid {} of {} jobs, then the core said: {error}",
                    undone.undoes.len(),
                    targets.len()
                );
                self.finish_undo(host, whole_preview, &undone);
                return Ok(undone);
            }
            if let Some(undoes) = reply["undoes"].as_u64() {
                undone.undoes.push(undoes);
            }
            for item in reply["left"].as_array().into_iter().flatten() {
                undone.left.push((
                    item["path"].as_str().unwrap_or_default().to_owned(),
                    reason_words(item["reason"].as_str().unwrap_or_default()).to_owned(),
                ));
            }
        }
        undone.text = format!(
            "Undid {} job{}{}.",
            undone.undoes.len(),
            if undone.undoes.len() == 1 { "" } else { "s" },
            if undone.left.is_empty() {
                String::new()
            } else {
                format!(
                    "; {} thing{} stay: {}",
                    undone.left.len(),
                    if undone.left.len() == 1 { "" } else { "s" },
                    undone
                        .left
                        .iter()
                        .map(|(path, why)| format!("{path} ({why})"))
                        .collect::<Vec<_>>()
                        .join(", ")
                )
            }
        );
        if job.is_none() && !whole_preview {
            undone.text.push_str(
                " Only the newest job was undone: run agent.undo again to go further back.",
            );
        }
        self.finish_undo(host, whole_preview, &undone);
        Ok(undone)
    }

    fn finish_undo(&mut self, host: &dyn Host, whole_preview: bool, undone: &Undone) {
        if whole_preview {
            self.saved.last = None;
            self.save(host);
        }
        let entry = Entry {
            time_ms: host.now_ms(),
            tier: self.saved.tier.map_or(0, Tier::number),
            source: "undo",
            prompt: format!("undo {:?}", undone.undoes),
            state: None,
            provider: String::new(),
            model: String::new(),
            rounds: vec![Round {
                reply: String::new(),
                commands: vec![CommandRecord {
                    line: "undo".to_owned(),
                    kind: "write",
                    outcome: undone.text.clone(),
                }],
            }],
            preview: None,
            jobs: undone.undoes.clone(),
            error: None,
        };
        if let Err(error) = audit::write(host, &entry) {
            host.log(Level::Warn, &format!("cannot write the audit log: {error}"));
        }
    }
}

/// The reason something stays after an undo, in words.
fn reason_words(reason: &str) -> &'static str {
    match reason {
        "in_recycle_bin" => "in the Recycle Bin: restore it from there",
        "deleted_for_good" => "deleted for good",
        "not_saved" => "replaced without a saved copy",
        "saved_copy_removed" => "its saved copy was removed",
        "put_back" => "a saved copy an undo put back",
        _ => "not undoable",
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testing::FakeHost;

    const ROOT: &str = r"C:\Users\Me";

    /// A host whose settings say `fake` and whose replies file holds `replies`.
    fn host_with(replies: &[&str], tier: u64) -> FakeHost {
        let host = FakeHost::new();
        host.set_settings(
            &json!({ "provider": "fake", "fakeReplies": "replies.json", "tier": tier }),
        );
        host.put_file("replies.json", &serde_json::to_string(replies).unwrap());
        host
    }

    fn agent(host: &FakeHost) -> Agent {
        Agent::new(host, vec![ROOT.to_owned()], vec![ROOT.to_owned()])
    }

    fn ask(agent: &mut Agent, host: &FakeHost, prompt: &str) -> Result<Outcome, String> {
        agent.run(
            host,
            &Task {
                source: Source::Ask,
                prompt,
                selected: &[],
            },
        )
    }

    fn model_calls(host: &FakeHost) -> Vec<Value> {
        String::from_utf8(
            host.files
                .borrow()
                .get("fake-requests.jsonl")
                .cloned()
                .unwrap_or_default(),
        )
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect()
    }

    fn statuses(outcome: &Outcome) -> Vec<(&str, &str)> {
        outcome
            .commands
            .iter()
            .map(|command| (command.kind, command.status.as_str()))
            .collect()
    }

    #[test]
    fn a_request_becomes_a_preview_at_tier_two_and_nothing_is_applied() {
        let host = host_with(
            &[
                "I will rename them.\n```\nrename C:\\Users\\Me\\a.jpg vacation_a.jpg\nrename C:\\Users\\Me\\b.jpg vacation_b.jpg\n```",
            ],
            2,
        );
        let mut agent = agent(&host);
        let outcome = ask(&mut agent, &host, "rename these to vacation_*").unwrap();
        assert_eq!(outcome.text, "I will rename them.");
        assert_eq!(outcome.preview.as_deref(), Some("preview-1"));
        assert_eq!(
            statuses(&outcome),
            [("write", "proposed"), ("write", "proposed")]
        );
        assert_eq!(
            outcome.changes[0],
            r"rename C:\Users\Me\a.jpg to vacation_a.jpg"
        );
        assert!(outcome.jobs.is_empty() && outcome.tier == 2);

        let proposal = &host.requests_of("preview_listing")[0];
        assert_eq!(proposal["title"], "Agent: rename these to vacation_*");
        assert_eq!(
            proposal["rows"],
            json!([
                { "path": r"C:\Users\Me\a.jpg", "kind": "rename", "to": "vacation_a.jpg" },
                { "path": r"C:\Users\Me\b.jpg", "kind": "rename", "to": "vacation_b.jpg" },
            ])
        );
        assert!(
            host.requests_of("preview_apply").is_empty(),
            "the user applies it, not the plugin"
        );
        assert!(agent.last_applied().is_none());
    }

    #[test]
    fn a_read_is_answered_and_the_model_then_proposes_from_what_it_saw() {
        let host = host_with(
            &[
                "Let me look.\n```\nls C:\\Users\\Me\\Pictures\n```",
                "Now the rename.\n```\nrename C:\\Users\\Me\\Pictures\\a.jpg b.jpg\n```",
            ],
            2,
        );
        host.put_folder(
            r"C:\Users\Me\Pictures",
            vec![crate::host::DirEntry {
                name: "a.jpg".to_owned(),
                is_dir: false,
                size: 10,
                modified_ms: None,
            }],
        );
        let mut agent = agent(&host);
        let outcome = ask(&mut agent, &host, "rename a.jpg").unwrap();
        assert_eq!(statuses(&outcome), [("read", "ok"), ("write", "proposed")]);
        let calls = model_calls(&host);
        assert_eq!(calls.len(), 2);
        let second = &calls[1]["messages"];
        assert_eq!(
            second.as_array().unwrap().len(),
            3,
            "request, its reply, the results"
        );
        assert!(
            second[2]["content"]
                .as_str()
                .unwrap()
                .contains("f a.jpg\n1 entries in C:\\Users\\Me\\Pictures")
        );
        assert_eq!(outcome.preview.as_deref(), Some("preview-1"));
    }

    #[test]
    fn changes_sent_with_reads_are_held_back_and_the_model_is_told() {
        let host = host_with(
            &[
                "```\nls C:\\Users\\Me\nrename C:\\Users\\Me\\a b\n```",
                "```\nrename C:\\Users\\Me\\a b\n```",
            ],
            2,
        );
        host.put_folder(ROOT, vec![]);
        let mut agent = agent(&host);
        let outcome = ask(&mut agent, &host, "x").unwrap();
        assert_eq!(
            statuses(&outcome),
            [
                ("read", "ok"),
                ("write", "not run: sent with commands that look"),
                ("write", "proposed")
            ]
        );
        let told = model_calls(&host)[1]["messages"][2]["content"]
            .as_str()
            .unwrap()
            .to_owned();
        assert!(
            told.contains("Note: Commands that change files were not run"),
            "{told}"
        );
        assert_eq!(
            host.requests_of("preview_listing").len(),
            1,
            "only the second reply's change"
        );
    }

    #[test]
    fn a_line_that_does_not_parse_or_leaves_the_folders_is_told_to_the_model_which_may_correct_it()
    {
        let host = host_with(
            &[
                "```\nfrobnicate C:\\x\ndelete C:\\Windows\\system32\n```",
                "```\nmkdir C:\\Users\\Me\\Sorted\n```",
            ],
            2,
        );
        let mut agent = agent(&host);
        let outcome = ask(&mut agent, &host, "x").unwrap();
        let told = model_calls(&host)[1]["messages"][2]["content"]
            .as_str()
            .unwrap()
            .to_owned();
        assert!(
            told.contains("error: `frobnicate` is not a command you may use"),
            "{told}"
        );
        assert!(told.contains("outside the folders you may use"), "{told}");
        assert_eq!(
            statuses(&outcome).last(),
            Some(&("write", "proposed")),
            "{:?}",
            outcome.commands
        );
        assert_eq!(
            host.requests_of("preview_listing")[0]["rows"][0]["path"],
            r"C:\Users\Me\Sorted\"
        );
    }

    #[test]
    fn a_model_that_keeps_asking_to_look_is_stopped_after_three_rounds() {
        let host = host_with(&["```\nls C:\\Users\\Me\n```"; 5], 2);
        host.put_folder(ROOT, vec![]);
        let mut agent = agent(&host);
        let outcome = ask(&mut agent, &host, "x").unwrap();
        assert_eq!(model_calls(&host).len(), MAX_ROUNDS);
        assert!(
            outcome.text.contains("Stopped after 3 rounds"),
            "{}",
            outcome.text
        );
        assert!(outcome.preview.is_none());
    }

    #[test]
    fn tier_one_looks_and_advises_but_proposes_and_changes_nothing() {
        let host = host_with(
            &["I would rename it.\n```\nrename C:\\Users\\Me\\a b\n```"],
            1,
        );
        let mut agent = agent(&host);
        let outcome = ask(&mut agent, &host, "x").unwrap();
        assert_eq!(outcome.text, "I would rename it.");
        assert_eq!(
            statuses(&outcome),
            [("write", "blocked: the Advisor tier changes nothing")]
        );
        assert!(host.requests_of("preview_listing").is_empty());
        assert!(host.requests_of("preview_apply").is_empty());
        assert!(outcome.preview.is_none());
        // It still reads at tier one.
        let host = host_with(&["```\nls C:\\Users\\Me\n```", "Done."], 1);
        host.put_folder(ROOT, vec![]);
        let outcome = ask(&mut agent_for(&host), &host, "x").unwrap();
        assert_eq!(statuses(&outcome), [("read", "ok")]);
    }

    fn agent_for(host: &FakeHost) -> Agent {
        agent(host)
    }

    #[test]
    fn tier_three_applies_at_once_remembers_the_jobs_and_undo_reverses_them_last_first() {
        let host = host_with(
            &["```\nmkdir C:\\Users\\Me\\S\nmove C:\\Users\\Me\\a.txt C:\\Users\\Me\\S\n```"],
            3,
        );
        let mut agent = agent(&host);
        let outcome = ask(&mut agent, &host, "sort").unwrap();
        assert_eq!(
            statuses(&outcome),
            [("write", "applied"), ("write", "applied")]
        );
        assert_eq!(outcome.jobs, [11, 12]);
        assert!(
            outcome
                .notice
                .as_deref()
                .unwrap()
                .contains("Applied 2 changes")
        );
        let asked = host.requests_of("preview_apply");
        assert_eq!(asked.len(), 1);
        assert_eq!(asked[0]["preview"], "preview-1");
        assert_eq!(agent.last_applied().unwrap().jobs, [11, 12]);
        // Kept across a restart.
        assert_eq!(state::load(&host).last.unwrap().jobs, [11, 12]);

        host.script_reply(
            "undo_job",
            Ok(json!({ "type": "undo_started", "job_id": 30, "undoes": 12, "left": [] })),
        );
        host.script_reply(
            "undo_job",
            Ok(json!({ "type": "undo_started", "job_id": 31, "undoes": 11,
                       "left": [{ "path": r"C:\Users\Me\gone.txt", "reason": "in_recycle_bin" }] })),
        );
        let undone = agent.undo(&host, None).unwrap();
        let asked = host.requests_of("undo_job");
        assert_eq!(
            asked,
            [
                json!({ "type": "undo_job", "job": 12 }),
                json!({ "type": "undo_job", "job": 11 })
            ]
        );
        assert_eq!(undone.undoes, [12, 11]);
        assert_eq!(
            undone.left,
            [(
                r"C:\Users\Me\gone.txt".to_owned(),
                "in the Recycle Bin: restore it from there".to_owned()
            )]
        );
        assert!(
            undone.text.starts_with("Undid 2 jobs; 1 thing stay"),
            "{}",
            undone.text
        );
        assert!(agent.last_applied().is_none(), "it is undone");
    }

    #[test]
    fn undo_without_a_known_preview_asks_the_core_for_the_newest_job() {
        let host = host_with(&[], 2);
        let mut agent = agent(&host);
        let undone = agent.undo(&host, None).unwrap();
        assert_eq!(
            host.requests_of("undo_job"),
            [json!({ "type": "undo_job" })]
        );
        assert!(
            undone.text.contains("Only the newest job was undone"),
            "{}",
            undone.text
        );
        let undone = agent.undo(&host, Some(7)).unwrap();
        assert_eq!(
            host.requests_of("undo_job")[1],
            json!({ "type": "undo_job", "job": 7 })
        );
        assert!(!undone.text.contains("Only the newest"));
        host.script_reply(
            "undo_job",
            Ok(json!({ "type": "error", "code": "not_undoable", "message": "job 7 is a delete" })),
        );
        let error = agent.undo(&host, Some(7)).unwrap_err();
        assert!(error.contains("job 7 is a delete"), "{error}");
    }

    #[test]
    fn a_preview_the_window_applied_is_learned_from_the_core_s_event() {
        let host = host_with(&["```\nmkdir C:\\Users\\Me\\S\n```"], 2);
        let mut agent = agent(&host);
        let outcome = ask(&mut agent, &host, "x").unwrap();
        let preview = outcome.preview.unwrap();
        agent.preview_event(
            &host,
            "preview-applied",
            &json!({ "preview": preview, "jobs": [21, 22, 23] }),
        );
        assert_eq!(agent.last_applied().unwrap().jobs, [21, 22, 23]);
        host.script_reply(
            "undo_job",
            Ok(json!({ "type": "undo_started", "job_id": 1, "undoes": 23, "left": [] })),
        );
        host.script_reply(
            "undo_job",
            Ok(json!({ "type": "undo_started", "job_id": 2, "undoes": 22, "left": [] })),
        );
        host.script_reply(
            "undo_job",
            Ok(json!({ "type": "undo_started", "job_id": 3, "undoes": 21, "left": [] })),
        );
        agent.undo(&host, None).unwrap();
        let jobs: Vec<u64> = host
            .requests_of("undo_job")
            .iter()
            .map(|r| r["job"].as_u64().unwrap())
            .collect();
        assert_eq!(jobs, [23, 22, 21]);
    }

    #[test]
    fn a_new_proposal_cancels_the_one_before_it() {
        let host = host_with(
            &[
                "```\nmkdir C:\\Users\\Me\\A\n```",
                "```\nmkdir C:\\Users\\Me\\B\n```",
            ],
            2,
        );
        let mut agent = agent(&host);
        ask(&mut agent, &host, "one").unwrap();
        assert!(host.requests_of("preview_cancel").is_empty());
        ask(&mut agent, &host, "two").unwrap();
        assert_eq!(
            host.requests_of("preview_cancel"),
            [json!({ "type": "preview_cancel", "preview": "preview-1" })]
        );
    }

    #[test]
    fn the_core_refusing_the_changes_goes_back_to_the_model_once_and_then_to_the_user() {
        let host = host_with(
            &[
                "```\nmkdir C:\\Users\\Me\\A\n```",
                "```\nmkdir C:\\Users\\Me\\B\n```",
            ],
            2,
        );
        host.script_reply(
            "preview_listing",
            Ok(json!({ "type": "error", "code": "invalid_path", "message": "C:\\Users\\Me\\A: not below a volume root" })),
        );
        let mut agent = agent(&host);
        let outcome = ask(&mut agent, &host, "x").unwrap();
        let told = model_calls(&host)[1]["messages"][2]["content"]
            .as_str()
            .unwrap()
            .to_owned();
        assert!(told.contains("the core refused the changes"), "{told}");
        assert_eq!(
            outcome.preview.as_deref(),
            Some("preview-1"),
            "the second proposal went through"
        );
    }

    #[test]
    fn the_model_s_errors_come_back_as_errors_and_every_request_is_in_the_audit_log() {
        let host = host_with(&[], 2);
        let mut agent = agent(&host);
        let error = ask(&mut agent, &host, "hello").unwrap_err();
        assert!(error.contains("no more replies"), "{error}");
        let entries = audit::last(&host, 5).unwrap();
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0]["error"], Value::String(error));
        assert_eq!(entries[0]["prompt"], "hello");
        assert_eq!(entries[0]["provider"], "fake");
        // A settings mistake is reported by name.
        host.set_settings(&json!({ "tier": 9 }));
        assert!(
            ask(&mut agent, &host, "x")
                .unwrap_err()
                .contains("settings.tier")
        );
    }

    #[test]
    fn a_successful_request_is_written_with_the_state_the_reply_and_each_command() {
        let host = host_with(&["Made a folder.\n```\nmkdir C:\\Users\\Me\\S\n```"], 2);
        host.script_reply(
            "get_window_state",
            Ok(json!({ "type": "window_state", "client": "CabinetOS#1", "sent_at_ms": 5,
                       "state": { "active_pane": "left", "panes": { "left": { "tabs": [{ "path": ROOT, "locked": false, "tool": null }],
                                  "active": 0, "cursor": null, "marked": [] }, "right": { "tabs": [], "active": 0, "cursor": null, "marked": [] } } } })),
        );
        let mut agent = agent(&host);
        ask(&mut agent, &host, "make a folder S").unwrap();
        let entries = audit::last(&host, 5).unwrap();
        let entry = &entries[0];
        assert_eq!(entry["time"], "2026-09-30T01:02:03Z");
        assert_eq!(entry["tier"], 2);
        assert_eq!(entry["source"], "ask");
        assert_eq!(entry["prompt"], "make a folder S");
        assert_eq!(entry["state"]["active_pane"], "left");
        assert_eq!(
            entry["rounds"][0]["reply"],
            "Made a folder.\n```\nmkdir C:\\Users\\Me\\S\n```"
        );
        assert_eq!(entry["rounds"][0]["commands"][0]["outcome"], "proposed");
        assert_eq!(entry["preview"], "preview-1");
        assert_eq!(entry["error"], Value::Null);
        // The window's state is in what the model was asked.
        let first = model_calls(&host)[0]["messages"][0]["content"]
            .as_str()
            .unwrap()
            .to_owned();
        assert!(
            first.contains("The pane that has the keyboard: left"),
            "{first}"
        );
        assert!(first.ends_with("Request: make a folder S"));
    }

    #[test]
    fn the_tier_the_user_set_wins_until_the_settings_change_theirs() {
        let host = host_with(&["```\nmkdir C:\\Users\\Me\\A\n```"; 3], 2);
        let mut agent = agent(&host);
        let settings = agent.settings(&host).unwrap();
        assert_eq!(agent.tier_of(&settings), Tier::Diff);
        agent.set_tier(&host, Tier::Autonomous);
        let settings = agent.settings(&host).unwrap();
        assert_eq!(
            agent.tier_of(&settings),
            Tier::Autonomous,
            "agent.tier wins over the settings' 2"
        );
        assert_eq!(
            state::load(&host).tier,
            Some(Tier::Autonomous),
            "and survives a restart"
        );
        // The settings say 1 now: that is newer.
        host.set_settings(&json!({ "provider": "fake", "fakeReplies": "replies.json", "tier": 1 }));
        let settings = agent.settings(&host).unwrap();
        assert_eq!(agent.tier_of(&settings), Tier::Advisor);
        assert_eq!(state::load(&host).tier, None);
    }
}
