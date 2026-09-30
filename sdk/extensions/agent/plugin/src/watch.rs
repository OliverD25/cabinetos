//! Watch folders: what the agent does when a folder it has a rule for gets
//! a new file. The model is asked once for each new file, with the rule,
//! the file's name and what `describe` shows for it, and the tier decides
//! what becomes of its answer, as for any request: at tier 2 a preview is
//! offered (the window opens it, and only the user applies it), at tier 3
//! it is applied and journaled, and at tier 1 the rules stay idle.
//!
//! A rule is paused after three failures in a row, and hands the model at
//! most ten files a minute, so that a service paid by the request is not
//! called without bound by a folder that fills up.

use serde_json::Value;

use crate::agent::{Agent, Outcome, Source, Task};
use crate::host::{Host, Level};
use crate::paths;
use crate::rules::{
    self, Admit, MAX_FILES_PER_MINUTE, MAX_RULE_CHARS, MAX_RULES, Origin, Status, Wanted,
};
use crate::settings::Rule;
use crate::tier::Tier;
use crate::tools;

/// One new file for a rule to deal with.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RuleJob {
    pub folder: String,
    pub rule: String,
    pub file: String,
    /// What `describe` shows for the file.
    about: String,
}

/// What an event of a watched folder leads to.
#[derive(Debug, Default, PartialEq, Eq)]
pub struct Batch {
    pub jobs: Vec<RuleJob>,
    /// Things to tell the user.
    pub notices: Vec<String>,
}

/// What a rule's run came to.
pub struct RuleRun {
    pub outcome: Result<Outcome, String>,
    /// The notice, when this failure paused the rule.
    pub paused: Option<String>,
}

/// The message the model gets for a new file: everything it may use, since
/// a rule has one round.
#[must_use]
pub fn rule_prompt(job: &RuleJob, folder_brief: &str) -> String {
    format!(
        "Rule for {name}: {rule}\n\nA new file appeared in the folder {folder}: {file}\nWhat describe shows for it: {about}\n\nThe folder holds:\n{brief}\n\nDo what the rule says for this file only. If the rule does not apply to it, answer with words only.",
        name = paths::file_name(&job.folder),
        rule = job.rule,
        folder = job.folder,
        file = job.file,
        about = job.about,
        brief = folder_brief,
    )
}

impl Agent {
    /// The rules that are wanted: those of the settings, then the added
    /// ones for other folders. A folder's rule in the settings wins.
    fn wanted_rules(&mut self, host: &dyn Host) -> Result<Vec<Wanted>, String> {
        let settings = self.settings(host)?;
        let mut wanted: Vec<Wanted> = settings
            .rules
            .into_iter()
            .map(|rule| Wanted {
                rule,
                origin: Origin::Settings,
            })
            .collect();
        for rule in &self.rules.stored {
            if !wanted
                .iter()
                .any(|known| paths::same(&known.rule.folder, &rule.folder))
            {
                wanted.push(Wanted {
                    rule: rule.clone(),
                    origin: Origin::Added,
                });
            }
        }
        Ok(wanted)
    }

    /// Makes the watched folders the wanted ones: at the start, and each
    /// time the settings change. Returns what to tell the user.
    pub fn sync_rules(&mut self, host: &dyn Host) -> Vec<String> {
        match self.wanted_rules(host) {
            Ok(wanted) => {
                let roots = self.roots.clone();
                self.rules.reconcile(host, wanted, &roots)
            }
            Err(error) => vec![format!(
                "The agent's settings have a mistake, so its watch rules stay as they were: {error}"
            )],
        }
    }

    /// The new files of a `folder-changed` event that a rule takes up.
    pub fn rule_jobs(&mut self, host: &dyn Host, payload: &Value) -> Batch {
        let mut batch = Batch::default();
        let Some(news) = rules::new_files(payload) else {
            return batch;
        };
        let Some(at) = self.rules.position(&news.folder) else {
            return batch;
        };
        if !matches!(self.rules.rules[at].status, Status::Watching) {
            return batch;
        }
        let tier = match self.settings(host) {
            Ok(settings) => self.tier_of(&settings),
            Err(error) => {
                batch
                    .notices
                    .push(format!("The watch rules cannot run: {error}"));
                return batch;
            }
        };
        if tier == Tier::Advisor {
            if !self.rules.advisor_told {
                self.rules.advisor_told = true;
                batch.notices.push(
                    "The watch rules are idle: tier 1 (Advisor) changes nothing. Choose tier 2 or 3 to let them work."
                        .to_owned(),
                );
            }
            return batch;
        }
        self.rules.advisor_told = false;
        if news.overflow {
            batch.notices.push(format!(
                "Too many changes at once in {}: the watch rule may have missed new files.",
                news.folder
            ));
        }
        let rule = self.rules.rules[at].rule.clone();
        for file in news.files {
            // What the agent's own changes made is not new to the user.
            if self.rules.take_ignored(&file) {
                continue;
            }
            // A folder, or a file that is gone already, is not for a rule.
            let about = match tools::describe_entry(host, &news.folder, paths::file_name(&file)) {
                Ok(Some((false, line))) => line,
                Ok(_) => continue,
                Err(error) => {
                    host.log(
                        Level::Warn,
                        &format!("watch rule: cannot look at {file}: {error}"),
                    );
                    continue;
                }
            };
            match self.rules.admit(at, host.now_ms()) {
                Admit::Yes => batch.jobs.push(RuleJob {
                    folder: news.folder.clone(),
                    rule: rule.clone(),
                    file,
                    about,
                }),
                Admit::No { first } => {
                    if first {
                        batch.notices.push(format!(
                            "The watch rule for {} takes at most {MAX_FILES_PER_MINUTE} new files a minute; the others are skipped.",
                            news.folder
                        ));
                    }
                }
            }
        }
        batch
    }

    /// Asks the model about one new file, and keeps count of what came of
    /// it: three failures in a row pause the rule.
    pub fn run_rule(&mut self, host: &dyn Host, job: &RuleJob) -> RuleRun {
        let brief = tools::folder_brief(host, &job.folder).unwrap_or_default();
        let prompt = rule_prompt(job, &brief);
        let outcome = self.run(
            host,
            &Task {
                source: Source::Rule,
                prompt: &prompt,
                selected: &[],
            },
        );
        let mut paused = None;
        if let Some(at) = self.rules.position(&job.folder) {
            match &outcome {
                Ok(done) => {
                    self.rules.succeed(at);
                    self.rules.remember(&done.targets);
                }
                Err(error) => paused = self.rules.fail(at, error),
            }
        }
        RuleRun { outcome, paused }
    }

    /// The core stopped a watch by itself (`folder-unwatched`): the rule
    /// is blocked, and the notice says why.
    pub fn watch_ended(&mut self, payload: &Value) -> Option<String> {
        let folder = payload["path"].as_str()?;
        let at = self.rules.position(folder)?;
        let message = payload["message"].as_str().unwrap_or("the watch ended");
        self.rules.stopped(at, message);
        Some(format!(
            "The watch rule for {folder} stopped: {message}. Resume it (agent.rule.resume) when the folder is back."
        ))
    }

    // ----- The rule commands -----

    /// Adds a rule made with `agent.rule.add`: kept in `rules.json`,
    /// because a plugin may not change the configuration. A rule that the
    /// settings hold for the folder stays as it is.
    pub fn add_rule(&mut self, host: &dyn Host, folder: &str, rule: &str) -> Result<(), String> {
        let rule = rule.trim();
        if rule.is_empty() || rule.chars().count() > MAX_RULE_CHARS {
            return Err(format!(
                "Write what to do with each new file, in at most {MAX_RULE_CHARS} characters."
            ));
        }
        if !paths::is_absolute(folder) || paths::has_dot_parts(folder) {
            return Err(format!(
                "{folder}: give the folder as an absolute path, such as C:\\Inbox"
            ));
        }
        let folder = paths::check_allowed(folder, &self.roots)?;
        let settings = self.settings(host)?;
        if settings
            .rules
            .iter()
            .any(|known| paths::same(&known.folder, &folder))
        {
            return Err(format!(
                "{folder} has a rule in your settings (plugins.agent.settings.rules); change it there."
            ));
        }
        let mut stored = self.rules.stored.clone();
        stored.retain(|known| !paths::same(&known.folder, &folder));
        if settings.rules.len() + stored.len() >= MAX_RULES {
            return Err(format!("At most {MAX_RULES} folders can have a rule."));
        }
        // The core says whether the folder may be watched, before anything
        // is written.
        host.watch(&folder)?;
        stored.push(Rule {
            folder,
            rule: rule.to_owned(),
        });
        rules::save(host, &stored)?;
        self.rules.stored = stored;
        self.rules.advisor_told = false;
        for notice in self.sync_rules(host) {
            host.log(Level::Warn, &notice);
        }
        Ok(())
    }

    /// Removes a rule that `agent.rule.add` made.
    pub fn remove_rule(&mut self, host: &dyn Host, folder: &str) -> Result<(), String> {
        let settings = self.settings(host)?;
        if settings
            .rules
            .iter()
            .any(|known| paths::same(&known.folder, folder))
        {
            return Err(format!(
                "The rule for {folder} is in your settings (plugins.agent.settings.rules); remove it there."
            ));
        }
        let mut stored = self.rules.stored.clone();
        let before = stored.len();
        stored.retain(|known| !paths::same(&known.folder, folder));
        if stored.len() == before {
            return Err(format!("There is no watch rule for {folder}."));
        }
        rules::save(host, &stored)?;
        self.rules.stored = stored;
        for notice in self.sync_rules(host) {
            host.log(Level::Warn, &notice);
        }
        Ok(())
    }

    /// Starts a paused rule again, or tries once more to watch a folder
    /// that could not be watched.
    pub fn resume_rule(&mut self, host: &dyn Host, folder: &str) -> Result<(), String> {
        let at = self
            .rules
            .position(folder)
            .ok_or_else(|| format!("There is no watch rule for {folder}."))?;
        let roots = self.roots.clone();
        self.rules.resume(host, at, &roots)
    }
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;
    use crate::host::DirEntry;
    use crate::testing::FakeHost;

    const ROOT: &str = r"C:\Users\Me";
    const INBOX: &str = r"C:\Users\Me\Inbox";
    const RULE: &str = "put each file in a folder of its year";

    fn file(name: &str, size: u64) -> DirEntry {
        DirEntry {
            name: name.to_owned(),
            is_dir: false,
            size,
            modified_ms: Some(1_790_730_000_000),
        }
    }

    /// An agent with the rule for the inbox, the fake provider that gives
    /// `replies`, and two files in the folder.
    fn setup(replies: &[&str], tier: u64) -> (FakeHost, Agent) {
        let host = FakeHost::new();
        host.set_settings(&json!({
            "provider": "fake", "fakeReplies": "replies.json", "tier": tier,
            "rules": [{ "folder": INBOX, "rule": RULE }],
        }));
        host.put_file("replies.json", &serde_json::to_string(replies).unwrap());
        host.put_folder(INBOX, vec![file("a.jpg", 2048), file("b.jpg", 10)]);
        let mut agent = Agent::new(&host, vec![ROOT.to_owned()], vec![ROOT.to_owned()]);
        assert!(agent.sync_rules(&host).is_empty());
        (host, agent)
    }

    fn created(names: &[&str]) -> Value {
        json!({
            "path": INBOX,
            "overflow": false,
            "changes": names.iter().map(|name| json!({
                "kind": "created", "path": format!(r"{INBOX}\{name}"), "old_path": null,
            })).collect::<Vec<_>>(),
        })
    }

    fn model_calls(host: &FakeHost) -> Vec<Value> {
        let text = host
            .files
            .borrow()
            .get("fake-requests.jsonl")
            .map(|bytes| String::from_utf8(bytes.clone()).unwrap())
            .unwrap_or_default();
        text.lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect()
    }

    #[test]
    fn a_new_file_is_one_call_to_the_model_and_at_tier_two_only_a_preview() {
        let (host, mut agent) = setup(
            &["A folder for 2026.\n```\nmkdir C:\\Users\\Me\\Inbox\\2026\n```"],
            2,
        );
        assert_eq!(*host.watched.borrow(), [INBOX], "watched from the start");
        let batch = agent.rule_jobs(&host, &created(&["a.jpg"]));
        assert!(batch.notices.is_empty());
        assert_eq!(batch.jobs.len(), 1);
        let outcome = agent.run_rule(&host, &batch.jobs[0]).outcome.unwrap();
        assert_eq!(outcome.preview.as_deref(), Some("preview-1"));
        assert!(outcome.jobs.is_empty(), "the user applies it");
        assert!(host.requests_of("preview_apply").is_empty());

        let calls = model_calls(&host);
        assert_eq!(calls.len(), 1, "never more than one call for a file");
        let message = calls[0]["messages"][0]["content"].as_str().unwrap();
        for wanted in [
            RULE,
            r"C:\Users\Me\Inbox\a.jpg",
            "JPG File",
            "2,048",
            "The folder holds:",
            "b.jpg",
        ] {
            assert!(message.contains(wanted), "{wanted}\n{message}");
        }
        assert!(
            calls[0]["system"]
                .as_str()
                .unwrap()
                .contains("one round only"),
            "the model is told it has one round"
        );
        assert!(
            host.requests_of("get_window_state").is_empty(),
            "a rule is not about what the window shows"
        );
    }

    #[test]
    fn a_model_that_asks_to_look_gets_no_second_round() {
        let (host, mut agent) = setup(&["```\nls C:\\Users\\Me\\Inbox\n```", "never asked"], 2);
        let batch = agent.rule_jobs(&host, &created(&["a.jpg"]));
        let outcome = agent.run_rule(&host, &batch.jobs[0]).outcome.unwrap();
        assert!(outcome.preview.is_none());
        assert!(
            outcome.text.contains("Stopped after 1 round."),
            "{}",
            outcome.text
        );
        assert_eq!(model_calls(&host).len(), 1);
    }

    #[test]
    fn at_tier_three_a_rule_applies_and_its_own_changes_are_not_new_files() {
        let (host, mut agent) = setup(
            &["```\nrename C:\\Users\\Me\\Inbox\\a.jpg 2026-a.jpg\n```"],
            3,
        );
        let batch = agent.rule_jobs(&host, &created(&["a.jpg"]));
        let outcome = agent.run_rule(&host, &batch.jobs[0]).outcome.unwrap();
        assert_eq!(outcome.jobs, [11, 12]);
        assert_eq!(host.requests_of("preview_apply").len(), 1);
        assert_eq!(
            agent.last_applied().unwrap().jobs,
            [11, 12],
            "agent.undo can reverse it"
        );
        // The rename shows up as a new file in the folder: it must not
        // start the rule again, or the rule would rename it for ever.
        host.put_folder(INBOX, vec![file("2026-a.jpg", 2048), file("b.jpg", 10)]);
        let again = agent.rule_jobs(&host, &created(&["2026-a.jpg"]));
        assert!(again.jobs.is_empty() && again.notices.is_empty());
        assert_eq!(model_calls(&host).len(), 1);
        // A real new file still counts.
        assert_eq!(agent.rule_jobs(&host, &created(&["b.jpg"])).jobs.len(), 1);
    }

    #[test]
    fn at_tier_one_the_rules_are_idle_and_the_user_is_told_once() {
        let (host, mut agent) = setup(&[], 1);
        let first = agent.rule_jobs(&host, &created(&["a.jpg"]));
        assert!(first.jobs.is_empty());
        assert_eq!(first.notices.len(), 1);
        assert!(first.notices[0].contains("Advisor"), "{:?}", first.notices);
        let second = agent.rule_jobs(&host, &created(&["b.jpg"]));
        assert!(second.jobs.is_empty() && second.notices.is_empty());
        assert!(model_calls(&host).is_empty(), "no call is made for nothing");
    }

    #[test]
    fn three_failures_in_a_row_pause_the_rule_and_resume_starts_it_again() {
        // The fake has no replies: every call fails.
        let (host, mut agent) = setup(&[], 2);
        let mut told = Vec::new();
        for name in ["a.jpg", "b.jpg", "a.jpg"] {
            let batch = agent.rule_jobs(&host, &created(&[name]));
            assert_eq!(batch.jobs.len(), 1, "{name}");
            let run = agent.run_rule(&host, &batch.jobs[0]);
            assert!(run.outcome.is_err());
            told.push(run.paused);
        }
        assert!(told[0].is_none() && told[1].is_none());
        let notice = told[2].clone().unwrap();
        assert!(
            notice.contains("3 failures in a row") && notice.contains("agent.rule.resume"),
            "{notice}"
        );
        assert!(matches!(agent.rules.rules[0].status, Status::Paused(_)));
        // A paused rule does not call the model.
        let calls = model_calls(&host).len();
        assert!(agent.rule_jobs(&host, &created(&["b.jpg"])).jobs.is_empty());
        assert_eq!(model_calls(&host).len(), calls);
        agent.resume_rule(&host, INBOX).unwrap();
        assert_eq!(agent.rule_jobs(&host, &created(&["b.jpg"])).jobs.len(), 1);
        assert!(agent.resume_rule(&host, r"C:\Users\Me\Nowhere").is_err());
    }

    #[test]
    fn a_folder_that_fills_up_costs_at_most_ten_calls_a_minute() {
        let (host, mut agent) = setup(&[], 2);
        let names: Vec<String> = (0..12).map(|n| format!("f{n}.txt")).collect();
        host.put_folder(INBOX, names.iter().map(|name| file(name, 1)).collect());
        let refs: Vec<&str> = names.iter().map(String::as_str).collect();
        let batch = agent.rule_jobs(&host, &created(&refs));
        assert_eq!(batch.jobs.len(), 10);
        assert_eq!(batch.notices.len(), 1);
        assert!(
            batch.notices[0].contains("at most 10"),
            "{:?}",
            batch.notices
        );
        let more = agent.rule_jobs(&host, &created(&["f0.txt"]));
        assert!(more.jobs.is_empty() && more.notices.is_empty());
        host.now.set(host.now.get() + 61_000);
        assert_eq!(agent.rule_jobs(&host, &created(&["f0.txt"])).jobs.len(), 1);
    }

    #[test]
    fn a_folder_or_a_file_that_is_gone_is_not_for_a_rule() {
        let (host, mut agent) = setup(&[], 2);
        host.put_folder(
            INBOX,
            vec![
                file("a.jpg", 1),
                DirEntry {
                    name: "sub".to_owned(),
                    is_dir: true,
                    size: 0,
                    modified_ms: None,
                },
            ],
        );
        let batch = agent.rule_jobs(&host, &created(&["sub", "vanished.txt", "a.jpg"]));
        assert_eq!(
            batch
                .jobs
                .iter()
                .map(|job| job.file.as_str())
                .collect::<Vec<_>>(),
            [r"C:\Users\Me\Inbox\a.jpg"]
        );
        // An event for a folder without a rule is nothing.
        let other = json!({ "path": r"C:\Users\Me\Other", "changes": [], "overflow": false });
        assert_eq!(agent.rule_jobs(&host, &other), Batch::default());
        // Lost changes are said, not guessed.
        let lost = json!({ "path": INBOX, "changes": [], "overflow": true });
        assert!(agent.rule_jobs(&host, &lost).notices[0].contains("missed"));
    }

    #[test]
    fn a_rule_proposal_does_not_cancel_a_pending_preview_and_a_chat_proposal_cancels_only_its_own()
    {
        let (host, mut agent) = setup(
            &[
                "```\nmkdir C:\\Users\\Me\\Made1\n```",
                "```\nmkdir C:\\Users\\Me\\Inbox\\2026\n```",
                "```\nmkdir C:\\Users\\Me\\Made2\n```",
            ],
            2,
        );
        let ask = |agent: &mut Agent| {
            agent
                .run(
                    &host,
                    &Task {
                        source: Source::Chat,
                        prompt: "make a folder",
                        selected: &[],
                    },
                )
                .unwrap()
        };
        assert_eq!(ask(&mut agent).preview.as_deref(), Some("preview-1"));
        let batch = agent.rule_jobs(&host, &created(&["a.jpg"]));
        let by_rule = agent.run_rule(&host, &batch.jobs[0]).outcome.unwrap();
        assert_eq!(by_rule.preview.as_deref(), Some("preview-2"));
        assert!(
            host.requests_of("preview_cancel").is_empty(),
            "the rule left preview-1 alone"
        );
        assert_eq!(ask(&mut agent).preview.as_deref(), Some("preview-3"));
        assert_eq!(
            host.requests_of("preview_cancel"),
            [json!({ "type": "preview_cancel", "preview": "preview-1" })],
            "and the next chat request replaced its own, not the rule's"
        );
    }

    #[test]
    fn a_rule_added_with_a_command_is_kept_watched_and_survives_a_restart() {
        let (host, mut agent) = setup(&[], 2);
        let inbox2 = r"C:\Users\Me\Downloads";
        host.put_folder(inbox2, vec![]);
        agent
            .add_rule(&host, inbox2, "delete nothing, sort by type")
            .unwrap();
        assert_eq!(*host.watched.borrow(), [INBOX, inbox2]);
        let saved = String::from_utf8(host.files.borrow()["rules.json"].clone()).unwrap();
        assert!(
            saved.contains("Downloads") && saved.contains("sort by type"),
            "{saved}"
        );
        assert_eq!(agent.rules.rules.len(), 2);
        assert_eq!(agent.rules.rules[1].origin, Origin::Added);

        // A new start reads the file and watches the folder again.
        host.watched.borrow_mut().clear();
        let mut again = Agent::new(&host, vec![ROOT.to_owned()], vec![]);
        assert!(again.sync_rules(&host).is_empty());
        assert_eq!(*host.watched.borrow(), [INBOX, inbox2]);

        again.remove_rule(&host, inbox2).unwrap();
        assert_eq!(*host.watched.borrow(), [INBOX]);
        assert_eq!(
            agent.rules.stored.len(),
            1,
            "the other instance still has its copy"
        );
        assert_eq!(again.rules.stored.len(), 0);
    }

    #[test]
    fn a_rule_that_cannot_be_added_is_refused_before_anything_is_written() {
        let (host, mut agent) = setup(&[], 2);
        host.unwatchable
            .borrow_mut()
            .push(r"C:\Users\Me\Locked".to_owned());
        let refuse = |agent: &mut Agent, folder: &str, rule: &str| {
            agent.add_rule(&host, folder, rule).unwrap_err()
        };
        assert!(refuse(&mut agent, r"C:\Users\Me\Locked", "x").contains("outside the roots"));
        assert!(
            refuse(&mut agent, r"D:\Elsewhere", "x").contains("outside the folders you may use")
        );
        assert!(refuse(&mut agent, r"Inbox", "x").contains("absolute path"));
        assert!(refuse(&mut agent, r"C:\Users\Me\..\Other", "x").contains("absolute path"));
        assert!(refuse(&mut agent, r"C:\Users\Me\New", "  ").contains("what to do"));
        assert!(refuse(&mut agent, INBOX, "another rule").contains("in your settings"));
        assert!(!host.files.borrow().contains_key("rules.json"));
        assert_eq!(*host.watched.borrow(), [INBOX]);
        // The rule of the settings is not the plugin's to remove.
        assert!(
            agent
                .remove_rule(&host, INBOX)
                .unwrap_err()
                .contains("remove it there")
        );
        assert!(
            agent
                .remove_rule(&host, r"C:\Users\Me\Nothing")
                .unwrap_err()
                .contains("no watch rule")
        );
    }

    #[test]
    fn a_watch_the_core_ended_blocks_the_rule_and_says_so() {
        let (host, mut agent) = setup(&[], 2);
        let told = agent
            .watch_ended(&json!({ "path": INBOX, "message": "the folder was deleted" }))
            .unwrap();
        assert!(
            told.contains("the folder was deleted") && told.contains("agent.rule.resume"),
            "{told}"
        );
        assert!(matches!(agent.rules.rules[0].status, Status::Blocked(_)));
        assert!(agent.rule_jobs(&host, &created(&["a.jpg"])).jobs.is_empty());
        assert!(
            agent
                .watch_ended(&json!({ "path": r"C:\Users\Me\Other", "message": "x" }))
                .is_none()
        );
        // The folder is back: resuming watches it again.
        host.watched.borrow_mut().clear();
        agent.resume_rule(&host, INBOX).unwrap();
        assert_eq!(*host.watched.borrow(), [INBOX]);
    }

    #[test]
    fn a_change_of_the_settings_starts_and_stops_watches() {
        let (host, mut agent) = setup(&[], 2);
        host.set_settings(&json!({
            "provider": "fake",
            "rules": [{ "folder": r"C:\Users\Me\Photos", "rule": "sort" }],
        }));
        assert!(agent.sync_rules(&host).is_empty());
        assert_eq!(*host.watched.borrow(), [r"C:\Users\Me\Photos"]);
        // A mistake in the settings keeps the rules as they are, and says so.
        host.set_settings(&json!({ "rules": "not a list" }));
        let told = agent.sync_rules(&host);
        assert!(
            told[0].contains("settings.rules must be a list"),
            "{told:?}"
        );
        assert_eq!(*host.watched.borrow(), [r"C:\Users\Me\Photos"]);
    }
}
