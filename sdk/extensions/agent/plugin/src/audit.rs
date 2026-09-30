//! The audit log: one JSON object per interaction, one file a day, in the
//! plugin's own folder (`audit.<date>.jsonl`, JSON Lines like every log of
//! CabinetOS). It says what the user asked, what the window showed, which
//! model answered, what it said, and what became of each command line.
//! The plugin never holds a secret, so none can reach it.

use serde_json::{Value, json};

use crate::host::Host;
use crate::time;

/// The most entries `agent.audit` returns.
pub const MAX_LISTED: usize = 200;

/// One command line and what became of it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CommandRecord {
    pub line: String,
    /// `read`, `write` or `invalid` (a line that did not parse).
    pub kind: &'static str,
    /// For example `ok`, `error: ...`, `proposed`, `applied`, `blocked`.
    pub outcome: String,
}

/// One reply of the model, with its command lines.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Round {
    /// The model's reply, as it came.
    pub reply: String,
    pub commands: Vec<CommandRecord>,
}

/// One interaction.
#[derive(Clone, Debug, PartialEq)]
pub struct Entry {
    pub time_ms: u64,
    pub tier: u8,
    /// `ask`, `chat`, `rule` or `undo`.
    pub source: &'static str,
    pub prompt: String,
    /// What the window showed, as the core told it; `None` without one.
    pub state: Option<Value>,
    pub provider: String,
    pub model: String,
    pub rounds: Vec<Round>,
    /// The preview it proposed, if any.
    pub preview: Option<String>,
    /// The jobs it started, if any.
    pub jobs: Vec<u64>,
    /// Why it failed, if it did.
    pub error: Option<String>,
}

impl Entry {
    /// The entry as one JSON object.
    #[must_use]
    pub fn to_json(&self) -> Value {
        json!({
            "time": time::iso(self.time_ms),
            "tier": self.tier,
            "source": self.source,
            "prompt": self.prompt,
            "state": self.state,
            "provider": self.provider,
            "model": self.model,
            "rounds": self.rounds.iter().map(|round| json!({
                "reply": round.reply,
                "commands": round.commands.iter().map(|command| json!({
                    "line": command.line,
                    "kind": command.kind,
                    "outcome": command.outcome,
                })).collect::<Vec<_>>(),
            })).collect::<Vec<_>>(),
            "preview": self.preview,
            "jobs": self.jobs,
            "error": self.error,
        })
    }

    /// The file it goes into: `audit.<date>.jsonl`.
    #[must_use]
    pub fn file_name(&self) -> String {
        file_name(self.time_ms)
    }
}

/// `audit.2026-09-30.jsonl`.
#[must_use]
pub fn file_name(time_ms: u64) -> String {
    format!("audit.{}.jsonl", time::date(time_ms))
}

/// Adds the entry to the day's file.
pub fn write(host: &dyn Host, entry: &Entry) -> Result<(), String> {
    let mut line = entry.to_json().to_string();
    line.push('\n');
    host.append_data(&entry.file_name(), line.as_bytes())
}

/// The last `n` entries, oldest first, from the newest files back.
pub fn last(host: &dyn Host, n: usize) -> Result<Vec<Value>, String> {
    let n = n.clamp(1, MAX_LISTED);
    let mut files: Vec<String> = host
        .list_data()?
        .into_iter()
        .filter(|name| name.starts_with("audit.") && name.ends_with(".jsonl"))
        .collect();
    files.sort();
    let mut newest_first: Vec<Value> = Vec::new();
    for name in files.iter().rev() {
        let Some(bytes) = host.read_data(name)? else {
            continue;
        };
        let text = String::from_utf8_lossy(&bytes);
        for line in text.lines().rev() {
            if let Ok(entry) = serde_json::from_str::<Value>(line) {
                newest_first.push(entry);
                if newest_first.len() == n {
                    newest_first.reverse();
                    return Ok(newest_first);
                }
            }
        }
    }
    newest_first.reverse();
    Ok(newest_first)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testing::FakeHost;

    fn entry(time_ms: u64, prompt: &str) -> Entry {
        Entry {
            time_ms,
            tier: 2,
            source: "ask",
            prompt: prompt.to_owned(),
            state: Some(json!({ "active_pane": "left" })),
            provider: "fake".to_owned(),
            model: "claude-sonnet-5-5".to_owned(),
            rounds: vec![Round {
                reply: "Renaming.\n```\nrename C:\\a b\n```".to_owned(),
                commands: vec![CommandRecord {
                    line: r"rename C:\a b".to_owned(),
                    kind: "write",
                    outcome: "proposed".to_owned(),
                }],
            }],
            preview: Some("preview-1".to_owned()),
            jobs: vec![],
            error: None,
        }
    }

    #[test]
    fn an_entry_has_the_time_tier_prompt_state_model_reply_and_each_command_with_its_outcome() {
        let json = entry(1_790_730_123_000, "rename a").to_json();
        assert_eq!(json["time"], "2026-09-30T01:02:03Z");
        assert_eq!(json["tier"], 2);
        assert_eq!(json["source"], "ask");
        assert_eq!(json["prompt"], "rename a");
        assert_eq!(json["state"]["active_pane"], "left");
        assert_eq!(
            (json["provider"].as_str(), json["model"].as_str()),
            (Some("fake"), Some("claude-sonnet-5-5"))
        );
        assert_eq!(
            json["rounds"][0]["reply"],
            "Renaming.\n```\nrename C:\\a b\n```"
        );
        assert_eq!(json["rounds"][0]["commands"][0]["line"], r"rename C:\a b");
        assert_eq!(json["rounds"][0]["commands"][0]["kind"], "write");
        assert_eq!(json["rounds"][0]["commands"][0]["outcome"], "proposed");
        assert_eq!(json["preview"], "preview-1");
        assert_eq!(json["jobs"], json!([]));
        assert_eq!(json["error"], Value::Null);
        // One line, so the file is JSON Lines.
        assert!(!json.to_string().contains('\n'));
    }

    #[test]
    fn entries_go_into_the_file_of_their_day_and_the_last_n_come_back_in_order() {
        let host = FakeHost::new();
        let day = 1_790_730_123_000_u64;
        write(&host, &entry(day, "first")).unwrap();
        write(&host, &entry(day + 1000, "second")).unwrap();
        write(&host, &entry(day + 86_400_000, "third")).unwrap();
        let mut names = host.list_data().unwrap();
        names.sort();
        assert_eq!(names, ["audit.2026-09-30.jsonl", "audit.2026-10-01.jsonl"]);
        let prompts = |n| -> Vec<String> {
            last(&host, n)
                .unwrap()
                .iter()
                .map(|entry| entry["prompt"].as_str().unwrap().to_owned())
                .collect()
        };
        assert_eq!(prompts(2), ["second", "third"]);
        assert_eq!(prompts(50), ["first", "second", "third"]);
        assert_eq!(prompts(1), ["third"]);
        // A damaged line is skipped, not fatal.
        host.append_data("audit.2026-10-01.jsonl", b"not json\n")
            .unwrap();
        assert_eq!(prompts(3), ["first", "second", "third"]);
    }

    #[test]
    fn nothing_written_is_an_empty_list() {
        assert!(last(&FakeHost::new(), 10).unwrap().is_empty());
    }
}
