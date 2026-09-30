//! Watch rules: a folder and what to do with each new file in it. The
//! rules come from two places: `plugins.agent.settings.rules`, which the
//! user writes in `cabinetos.json`, and `rules.json` in the plugin's own
//! folder, which `agent.rule.add` writes, because a plugin may not change
//! the configuration. This module holds what a rule is, what the plugin
//! knows of each one, and the checks on the events; `watch.rs` runs them.

use serde_json::{Value, json};

use crate::host::Host;
use crate::paths;
use crate::settings::Rule;

/// The most rules: the core lets a plugin watch 16 folders.
pub const MAX_RULES: usize = 16;
/// The longest rule text.
pub const MAX_RULE_CHARS: usize = 1000;
/// A rule that fails this many times in a row is paused.
pub const PAUSE_AFTER: u8 = 3;
/// The most new files one rule hands to the model in a minute. A folder
/// that gets a hundred files at once must not send a hundred requests to
/// a service that is paid by the request.
pub const MAX_FILES_PER_MINUTE: usize = 10;
const MINUTE_MS: u64 = 60_000;
/// How many paths a rule remembers as its own doing.
const REMEMBERED: usize = 200;
/// The plugin's own file for the rules `agent.rule.add` made.
pub const FILE: &str = "rules.json";

/// Where a rule is written.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Origin {
    /// `plugins.agent.settings.rules`: the user's file.
    Settings,
    /// `rules.json`: made with `agent.rule.add`.
    Added,
}

impl Origin {
    fn word(self) -> &'static str {
        match self {
            Self::Settings => "settings",
            Self::Added => "added",
        }
    }
}

/// Whether a rule is at work.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Status {
    /// The folder is watched and each new file goes to the model.
    Watching,
    /// Stopped after too many failures in a row, or by the user; the text
    /// says why. `agent.rule.resume` starts it again.
    Paused(String),
    /// The folder cannot be watched; the text says why.
    Blocked(String),
}

impl Status {
    fn word(&self) -> &'static str {
        match self {
            Self::Watching => "watching",
            Self::Paused(_) => "paused",
            Self::Blocked(_) => "blocked",
        }
    }
}

/// What the plugin knows of one rule.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RuleState {
    pub folder: String,
    pub rule: String,
    pub origin: Origin,
    pub status: Status,
    /// Failures in a row.
    pub failures: u8,
    window_start_ms: u64,
    handled: usize,
    skipped: usize,
}

impl RuleState {
    fn new(rule: Rule, origin: Origin) -> Self {
        Self {
            folder: rule.folder,
            rule: rule.rule,
            origin,
            status: Status::Watching,
            failures: 0,
            window_start_ms: 0,
            handled: 0,
            skipped: 0,
        }
    }

    /// The rule as the chat page shows it.
    #[must_use]
    pub fn to_json(&self) -> Value {
        let why = match &self.status {
            Status::Watching => None,
            Status::Paused(why) | Status::Blocked(why) => Some(why.as_str()),
        };
        json!({
            "folder": self.folder,
            "rule": self.rule,
            "status": self.status.word(),
            "why": why,
            "origin": self.origin.word(),
        })
    }
}

/// A rule the settings or `rules.json` asks for.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Wanted {
    pub rule: Rule,
    pub origin: Origin,
}

/// Whether the rule may hand one more file to the model now.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Admit {
    Yes,
    /// The minute's limit is reached; `first` is true for the first file
    /// skipped in that minute, which is the one to tell the user about.
    No {
        first: bool,
    },
}

/// Every rule, and the paths the agent's own changes made.
#[derive(Debug, Default)]
pub struct RuleBook {
    pub rules: Vec<RuleState>,
    /// The rules `agent.rule.add` made, as `rules.json` holds them.
    pub stored: Vec<Rule>,
    ignored: Vec<String>,
    /// Whether the user was told that tier 1 leaves the rules idle.
    pub advisor_told: bool,
}

impl RuleBook {
    /// A rule book with the rules `agent.rule.add` made, not watching yet.
    #[must_use]
    pub fn new(stored: Vec<Rule>) -> Self {
        Self {
            stored,
            ..Self::default()
        }
    }

    /// The place of the rule for a folder.
    #[must_use]
    pub fn position(&self, folder: &str) -> Option<usize> {
        self.rules
            .iter()
            .position(|rule| paths::same(&rule.folder, folder))
    }

    /// Makes the rules the wanted ones: a rule that is not wanted any more
    /// stops being watched, a new one starts, and one that stays keeps
    /// what happened to it, unless its text changed. Returns the notices
    /// for rules that could not start.
    pub fn reconcile(
        &mut self,
        host: &dyn Host,
        wanted: Vec<Wanted>,
        read_roots: &[String],
    ) -> Vec<String> {
        let mut notices = Vec::new();
        let mut next: Vec<RuleState> = Vec::new();
        for want in wanted {
            let existing = self
                .position(&want.rule.folder)
                .map(|at| self.rules.remove(at));
            // A new rule starts watching; so does one that could not, in
            // case its folder is there now. One that is at work is left as
            // it is: the core keeps its watch.
            let (mut state, start) = match existing {
                Some(mut state) => {
                    if state.rule != want.rule.rule {
                        state.rule = want.rule.rule.clone();
                        state.failures = 0;
                        if matches!(state.status, Status::Paused(_)) {
                            state.status = Status::Watching;
                        }
                    }
                    state.origin = want.origin;
                    let retry = matches!(state.status, Status::Blocked(_));
                    (state, retry)
                }
                None => (RuleState::new(want.rule, want.origin), true),
            };
            if start {
                state.status = begin(host, &state.folder, read_roots);
                if let Status::Blocked(why) = &state.status {
                    notices.push(format!(
                        "The watch rule for {} cannot start: {why}",
                        state.folder
                    ));
                }
            }
            next.push(state);
        }
        // What is left was in the book and is not wanted: stop watching.
        for gone in self.rules.drain(..) {
            host.unwatch(&gone.folder);
        }
        self.rules = next;
        notices
    }

    /// The rule failed: counts it, and pauses the rule after
    /// [`PAUSE_AFTER`] in a row. Returns the notice when it paused.
    pub fn fail(&mut self, at: usize, error: &str) -> Option<String> {
        let state = self.rules.get_mut(at)?;
        state.failures = state.failures.saturating_add(1);
        if state.failures < PAUSE_AFTER {
            return None;
        }
        let why = format!("paused after {PAUSE_AFTER} failures in a row; the last: {error}");
        state.status = Status::Paused(why.clone());
        Some(format!(
            "The watch rule for {} is {why}. Fix the cause, then resume it (agent.rule.resume).",
            state.folder
        ))
    }

    /// The rule worked: the failures in a row start again from none.
    pub fn succeed(&mut self, at: usize) {
        if let Some(state) = self.rules.get_mut(at) {
            state.failures = 0;
        }
    }

    /// Starts a rule again after a pause, or tries once more to watch a
    /// folder that could not be watched.
    pub fn resume(
        &mut self,
        host: &dyn Host,
        at: usize,
        read_roots: &[String],
    ) -> Result<(), String> {
        let state = self.rules.get_mut(at).ok_or("there is no such rule")?;
        state.failures = 0;
        state.status = begin(host, &state.folder, read_roots);
        match &state.status {
            Status::Blocked(why) => Err(why.clone()),
            _ => Ok(()),
        }
    }

    /// Ends a rule's watch for a reason the core gave.
    pub fn stopped(&mut self, at: usize, why: &str) {
        if let Some(state) = self.rules.get_mut(at) {
            state.status = Status::Blocked(why.to_owned());
        }
    }

    /// Whether the rule may hand one more file to the model in this
    /// minute.
    pub fn admit(&mut self, at: usize, now_ms: u64) -> Admit {
        let Some(state) = self.rules.get_mut(at) else {
            return Admit::No { first: false };
        };
        if now_ms.saturating_sub(state.window_start_ms) >= MINUTE_MS {
            state.window_start_ms = now_ms;
            state.handled = 0;
            state.skipped = 0;
        }
        if state.handled < MAX_FILES_PER_MINUTE {
            state.handled += 1;
            Admit::Yes
        } else {
            state.skipped += 1;
            Admit::No {
                first: state.skipped == 1,
            }
        }
    }

    /// Remembers paths the agent's own changes make, so that the events
    /// they cause are not taken for new files.
    pub fn remember(&mut self, targets: &[String]) {
        for target in targets {
            self.ignored.push(paths::normalize(target).to_lowercase());
        }
        let extra = self.ignored.len().saturating_sub(REMEMBERED);
        self.ignored.drain(..extra);
    }

    /// Whether the path is one the agent made; it is forgotten once seen.
    pub fn take_ignored(&mut self, path: &str) -> bool {
        let path = paths::normalize(path).to_lowercase();
        match self.ignored.iter().position(|known| *known == path) {
            Some(at) => {
                self.ignored.remove(at);
                true
            }
            None => false,
        }
    }

    /// Every rule, for the chat page.
    #[must_use]
    pub fn to_json(&self) -> Value {
        Value::Array(self.rules.iter().map(RuleState::to_json).collect())
    }
}

/// Starts watching a folder for a rule: it must be one the agent may read,
/// and the core must let the plugin watch it.
fn begin(host: &dyn Host, folder: &str, read_roots: &[String]) -> Status {
    if let Err(why) = paths::check_allowed(folder, read_roots) {
        return Status::Blocked(why);
    }
    match host.watch(folder) {
        Ok(()) => Status::Watching,
        Err(why) => Status::Blocked(why),
    }
}

/// The rules `agent.rule.add` made.
pub fn load(host: &dyn Host) -> Vec<Rule> {
    let Ok(Some(bytes)) = host.read_data(FILE) else {
        return Vec::new();
    };
    let Ok(value) = serde_json::from_slice::<Value>(&bytes) else {
        host.log(
            crate::host::Level::Warn,
            &format!("{FILE} is damaged; the agent starts without those rules"),
        );
        return Vec::new();
    };
    crate::settings::rules_of(value.get("rules")).unwrap_or_default()
}

/// Writes the rules `agent.rule.add` made.
pub fn save(host: &dyn Host, rules: &[Rule]) -> Result<(), String> {
    let text = json!({
        "rules": rules
            .iter()
            .map(|rule| json!({ "folder": rule.folder, "rule": rule.rule }))
            .collect::<Vec<_>>(),
    });
    host.write_data(FILE, format!("{text}\n").as_bytes())
}

/// What a `folder-changed` event says about new files.
#[derive(Debug, PartialEq, Eq)]
pub struct NewFiles {
    /// The watched folder.
    pub folder: String,
    /// The paths of the files that appeared, once each.
    pub files: Vec<String>,
    /// Changes were lost: the plugin cannot know which files are new.
    pub overflow: bool,
}

const TEMPORARY: [&str; 6] = [
    "crdownload",
    "part",
    "tmp",
    "download",
    "partial",
    "opdownload",
];

/// A name a browser or a program gives a file while it is still being
/// written: the file gets its real name when it is done.
#[must_use]
pub fn is_temporary(name: &str) -> bool {
    TEMPORARY.contains(&paths::extension(name).as_str())
}

/// A name that is not a file of the user's: Office's lock files and the
/// files Windows adds to a folder.
fn is_noise(name: &str) -> bool {
    name.starts_with("~$")
        || name.eq_ignore_ascii_case("desktop.ini")
        || name.eq_ignore_ascii_case("thumbs.db")
}

/// The new files in a `folder-changed` payload: `created` ones, and files
/// that got their real name from a temporary one (a finished download).
#[must_use]
pub fn new_files(payload: &Value) -> Option<NewFiles> {
    let folder = payload["path"].as_str()?.to_owned();
    let mut files: Vec<String> = Vec::new();
    for change in payload["changes"].as_array().into_iter().flatten() {
        let Some(path) = change["path"].as_str() else {
            continue;
        };
        let is_new = match change["kind"].as_str() {
            Some("created") => true,
            Some("renamed") => change["old_path"]
                .as_str()
                .is_some_and(|old| is_temporary(paths::file_name(old))),
            _ => false,
        };
        let name = paths::file_name(path);
        if is_new
            && !is_temporary(name)
            && !is_noise(name)
            && !files.iter().any(|known| paths::same(known, path))
        {
            files.push(path.to_owned());
        }
    }
    Some(NewFiles {
        folder,
        files,
        overflow: payload["overflow"].as_bool().unwrap_or(false),
    })
}

/// The words a rule is added with from the palette's one text box: a folder
/// (in double quotes when it has a space), then the rule.
pub fn split_folder_and_rule(text: &str) -> Result<(String, String), String> {
    let text = text.trim();
    let (folder, rest) = if let Some(quoted) = text.strip_prefix('"') {
        quoted
            .split_once('"')
            .ok_or("the folder has an opening quote and no closing one")?
    } else {
        text.split_once(char::is_whitespace).unwrap_or((text, ""))
    };
    let rule = rest.trim();
    if folder.is_empty() || rule.is_empty() {
        return Err(
            "Write the folder, then what to do with each new file in it: C:\\Inbox sort each file by year"
                .to_owned(),
        );
    }
    Ok((folder.to_owned(), rule.to_owned()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testing::FakeHost;

    const ROOT: &str = r"C:\Users\Me";
    const INBOX: &str = r"C:\Users\Me\Inbox";

    fn rule(folder: &str, text: &str) -> Rule {
        Rule {
            folder: folder.to_owned(),
            rule: text.to_owned(),
        }
    }

    fn wanted(folder: &str, text: &str) -> Wanted {
        Wanted {
            rule: rule(folder, text),
            origin: Origin::Settings,
        }
    }

    fn change(kind: &str, path: &str, old: Option<&str>) -> Value {
        json!({ "kind": kind, "path": path, "old_path": old })
    }

    #[test]
    fn only_created_files_and_finished_downloads_are_new() {
        let payload = json!({
            "path": INBOX,
            "overflow": false,
            "changes": [
                change("created", r"C:\Users\Me\Inbox\a.jpg", None),
                change("modified", r"C:\Users\Me\Inbox\b.jpg", None),
                change("removed", r"C:\Users\Me\Inbox\c.jpg", None),
                // A download is written as .crdownload and renamed when done.
                change("renamed", r"C:\Users\Me\Inbox\report.pdf", Some(r"C:\Users\Me\Inbox\report.pdf.crdownload")),
                // A rename of an ordinary file is not a new file.
                change("renamed", r"C:\Users\Me\Inbox\new-name.txt", Some(r"C:\Users\Me\Inbox\old-name.txt")),
                // Still being written, and files that are not the user's.
                change("created", r"C:\Users\Me\Inbox\movie.mp4.crdownload", None),
                change("created", r"C:\Users\Me\Inbox\~$draft.docx", None),
                change("created", r"C:\Users\Me\Inbox\DESKTOP.INI", None),
                // The same file twice, in another case.
                change("created", r"C:\Users\Me\Inbox\A.JPG", None),
            ],
        });
        let news = new_files(&payload).unwrap();
        assert_eq!(news.folder, INBOX);
        assert_eq!(
            news.files,
            [r"C:\Users\Me\Inbox\a.jpg", r"C:\Users\Me\Inbox\report.pdf"]
        );
        assert!(!news.overflow);
        assert!(new_files(&json!({ "changes": [] })).is_none());
        let lost = new_files(&json!({ "path": INBOX, "changes": [], "overflow": true })).unwrap();
        assert!(lost.overflow && lost.files.is_empty());
    }

    #[test]
    fn a_palette_text_is_a_folder_and_a_rule() {
        assert_eq!(
            split_folder_and_rule(r"C:\Inbox  sort each file by year ").unwrap(),
            (r"C:\Inbox".to_owned(), "sort each file by year".to_owned())
        );
        assert_eq!(
            split_folder_and_rule(r#""C:\My Inbox" sort them"#).unwrap(),
            (r"C:\My Inbox".to_owned(), "sort them".to_owned())
        );
        for bad in ["", r"C:\Inbox", r#""C:\Inbox sort"#, r#""C:\Inbox""#] {
            assert!(split_folder_and_rule(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn a_rule_starts_watching_and_one_that_cannot_says_why() {
        let host = FakeHost::new();
        host.unwatchable
            .borrow_mut()
            .push(r"C:\Users\Me\Locked".to_owned());
        let roots = [ROOT.to_owned()];
        let mut book = RuleBook::default();
        let notices = book.reconcile(
            &host,
            vec![
                wanted(INBOX, "sort"),
                wanted(r"C:\Users\Me\Locked", "sort"),
                wanted(r"D:\Elsewhere", "sort"),
            ],
            &roots,
        );
        assert_eq!(*host.watched.borrow(), [INBOX]);
        assert_eq!(book.rules[0].status, Status::Watching);
        assert!(
            matches!(&book.rules[1].status, Status::Blocked(why) if why.contains("outside the roots"))
        );
        // D:\ is not a folder the agent may read: the core is not even asked.
        assert!(
            matches!(&book.rules[2].status, Status::Blocked(why) if why.contains("outside the folders you may use"))
        );
        assert_eq!(notices.len(), 2, "{notices:?}");
        assert_eq!(book.to_json()[0]["status"], "watching");
        assert_eq!(book.to_json()[1]["status"], "blocked");
    }

    #[test]
    fn a_rule_that_is_not_wanted_any_more_is_unwatched_and_one_that_stays_keeps_its_count() {
        let host = FakeHost::new();
        let roots = [ROOT.to_owned()];
        let mut book = RuleBook::default();
        book.reconcile(
            &host,
            vec![wanted(INBOX, "sort"), wanted(r"C:\Users\Me\Other", "x")],
            &roots,
        );
        assert_eq!(host.watched.borrow().len(), 2);
        book.fail(0, "the model said no");
        book.reconcile(&host, vec![wanted(INBOX, "sort")], &roots);
        assert_eq!(
            *host.watched.borrow(),
            [INBOX],
            "the other one is not watched any more"
        );
        assert_eq!(
            book.rules[0].failures, 1,
            "the same rule keeps what happened to it"
        );
        // A change of the words starts it afresh.
        book.reconcile(&host, vec![wanted(INBOX, "sort by month")], &roots);
        assert_eq!(
            (book.rules[0].failures, book.rules[0].rule.as_str()),
            (0, "sort by month")
        );
    }

    #[test]
    fn three_failures_in_a_row_pause_a_rule_and_a_success_starts_the_count_again() {
        let host = FakeHost::new();
        let roots = [ROOT.to_owned()];
        let mut book = RuleBook::default();
        book.reconcile(&host, vec![wanted(INBOX, "sort")], &roots);
        assert!(book.fail(0, "one").is_none());
        assert!(book.fail(0, "two").is_none());
        book.succeed(0);
        assert!(book.fail(0, "three").is_none());
        assert!(book.fail(0, "four").is_none());
        let told = book.fail(0, "five").unwrap();
        assert!(
            told.contains(INBOX) && told.contains("3 failures in a row") && told.contains("five"),
            "{told}"
        );
        assert!(matches!(book.rules[0].status, Status::Paused(_)));
        book.resume(&host, 0, &roots).unwrap();
        assert_eq!(
            (book.rules[0].status.clone(), book.rules[0].failures),
            (Status::Watching, 0)
        );
        // A settings change that leaves the words alone does not un-pause.
        for _ in 0..3 {
            book.fail(0, "again");
        }
        book.reconcile(&host, vec![wanted(INBOX, "sort")], &roots);
        assert!(matches!(book.rules[0].status, Status::Paused(_)));
        book.reconcile(&host, vec![wanted(INBOX, "sort differently")], &roots);
        assert_eq!(
            book.rules[0].status,
            Status::Watching,
            "new words, a new start"
        );
    }

    #[test]
    fn a_rule_hands_the_model_at_most_ten_files_a_minute() {
        let host = FakeHost::new();
        let mut book = RuleBook::default();
        book.reconcile(&host, vec![wanted(INBOX, "sort")], &[ROOT.to_owned()]);
        for _ in 0..MAX_FILES_PER_MINUTE {
            assert_eq!(book.admit(0, 1_000), Admit::Yes);
        }
        assert_eq!(book.admit(0, 30_000), Admit::No { first: true });
        assert_eq!(book.admit(0, 31_000), Admit::No { first: false });
        assert_eq!(book.admit(0, 61_001), Admit::Yes, "a new minute");
        assert_eq!(book.admit(9, 0), Admit::No { first: false }, "no such rule");
    }

    #[test]
    fn what_the_agent_made_is_ignored_once() {
        let mut book = RuleBook::default();
        book.remember(&[r"C:\Users\Me\Inbox\2026\a.jpg".to_owned()]);
        assert!(!book.take_ignored(r"C:\Users\Me\Inbox\b.jpg"));
        assert!(book.take_ignored(r"c:/users/me/inbox/2026/A.JPG"));
        assert!(!book.take_ignored(r"C:\Users\Me\Inbox\2026\a.jpg"), "once");
        book.remember(&(0..300).map(|n| format!(r"C:\x\{n}")).collect::<Vec<_>>());
        assert!(!book.take_ignored(r"C:\x\0"), "the oldest are forgotten");
        assert!(book.take_ignored(r"C:\x\299"));
    }

    #[test]
    fn the_added_rules_are_kept_in_the_plugin_s_own_file() {
        let host = FakeHost::new();
        assert!(load(&host).is_empty());
        save(&host, &[rule(INBOX, "sort by year")]).unwrap();
        assert_eq!(load(&host), [rule(INBOX, "sort by year")]);
        host.put_file(FILE, "{ this is not json");
        assert!(
            load(&host).is_empty(),
            "a damaged file is no rules, and a line in the log"
        );
        assert!(
            host.logs
                .borrow()
                .iter()
                .any(|(_, line)| line.contains("damaged"))
        );
    }
}
