//! The plugin's commands and the events it hears: what each command takes,
//! what it answers to its caller, and what it tells the chat page and the
//! window (`emit`).
//!
//! The window's general rules (docs/tool-extensions.md, docs/ui.md): a
//! command result with a string `preview` opens that preview in the other
//! pane, and so does any event whose payload has one; an event whose
//! payload has a string `notice` shows it in the status bar. So `agent.ask`
//! names its preview in its result only, and every other way to a preview
//! (the chat page, a rule) names it in an event.

use serde_json::{Value, json};

use crate::agent::{Agent, Outcome, Source, Task};
use crate::audit;
use crate::host::{Host, Level};
use crate::paths;
use crate::rules;
use crate::tier::Tier;
use crate::watch::RuleJob;

/// A command of the plugin: what `plugin.json` declares and `activate`
/// registers.
pub struct CommandSpec {
    pub id: &'static str,
    pub title: &'static str,
    pub category: &'static str,
    pub keys: &'static [&'static str],
}

/// Every command, in the order the palette lists them. A test keeps
/// `plugin.json` in step with this table.
pub const COMMANDS: &[CommandSpec] = &[
    CommandSpec {
        id: "agent.ask",
        title: "Agent: Ask",
        category: "Agent",
        keys: &["ctrl+k ctrl+a"],
    },
    CommandSpec {
        id: "agent.chat",
        title: "Agent: Chat Message",
        category: "Agent",
        keys: &[],
    },
    CommandSpec {
        id: "agent.tier",
        title: "Agent: Set Tier",
        category: "Agent",
        keys: &[],
    },
    CommandSpec {
        id: "agent.undo",
        title: "Agent: Undo the Last Changes",
        category: "Agent",
        keys: &[],
    },
    CommandSpec {
        id: "agent.audit",
        title: "Agent: Show the Audit Log",
        category: "Agent",
        keys: &[],
    },
    CommandSpec {
        id: "agent.rule.add",
        title: "Agent: Add a Watch Rule",
        category: "Agent",
        keys: &[],
    },
    CommandSpec {
        id: "agent.rule.remove",
        title: "Agent: Remove a Watch Rule",
        category: "Agent",
        keys: &[],
    },
    CommandSpec {
        id: "agent.rule.resume",
        title: "Agent: Resume a Watch Rule",
        category: "Agent",
        keys: &[],
    },
    CommandSpec {
        id: "agent.rule.list",
        title: "Agent: List the Watch Rules",
        category: "Agent",
        keys: &[],
    },
];

fn emit(host: &dyn Host, name: &str, payload: &Value) {
    host.emit(name, &payload.to_string());
}

/// The mark on the chat's button in the activity rail
/// (docs/tool-extensions.md, "The sidebar page"): a spinner, or none.
fn badge(host: &dyn Host, kind: Option<&str>) {
    emit(
        host,
        "badge",
        &json!({ "view": "agent-chat", "kind": kind }),
    );
}

/// Runs one command.
pub fn on_command(
    agent: &mut Agent,
    host: &dyn Host,
    id: &str,
    args: &str,
) -> Result<String, String> {
    let args: Value = if args.trim().is_empty() {
        json!({})
    } else {
        serde_json::from_str(args)
            .map_err(|error| format!("the arguments are not JSON: {error}"))?
    };
    // A request can take a while (a model answers over the network): the
    // chat's button in the rail shows a spinner meanwhile.
    let asks_a_model = matches!(id, "agent.ask" | "agent.chat");
    if asks_a_model {
        badge(host, Some("spinner"));
    }
    let result = match id {
        "agent.ask" => ask(agent, host, &args),
        "agent.chat" => chat(agent, host, &args),
        "agent.tier" => tier(agent, host, &args),
        "agent.undo" => undo(agent, host, &args),
        "agent.audit" => show_audit(host, &args),
        "agent.rule.add" => rule_add(agent, host, &args),
        "agent.rule.remove" => rule_remove(agent, host, &args),
        "agent.rule.resume" => rule_resume(agent, host, &args),
        "agent.rule.list" => Ok(rule_list(agent, host, &args)),
        other => Err(format!("the agent has no command {other}")),
    };
    if asks_a_model {
        badge(host, None);
    }
    match result {
        Ok(value) => Ok(value.to_string()),
        Err(error) => {
            host.log(Level::Warn, &format!("{id} failed: {error}"));
            emit(host, "agent.error", &json!({ "text": error }));
            Err(error)
        }
    }
}

/// The files the caller pointed at: the selection of the pane, else the
/// row under the cursor (what the window sends as `paths` and `path`).
fn selected(args: &Value) -> Vec<String> {
    let paths: Vec<String> = args["paths"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
        .map(str::to_owned)
        .collect();
    if paths.is_empty() {
        args["path"]
            .as_str()
            .map(str::to_owned)
            .into_iter()
            .collect()
    } else {
        paths
    }
}

/// What the chat page shows of a reply.
fn reply_payload(outcome: &Outcome, source: &str, prompt: &str) -> Value {
    json!({
        "source": source,
        "prompt": prompt,
        "text": outcome.text,
        "commands": outcome.commands.iter().map(crate::agent::CommandOutcome::to_json).collect::<Vec<_>>(),
        "changes": outcome.changes,
        "tier": outcome.tier,
    })
}

fn ask(agent: &mut Agent, host: &dyn Host, args: &Value) -> Result<Value, String> {
    let request = args["input"].as_str().map(str::trim).unwrap_or_default();
    if request.is_empty() {
        return Err("Type what the agent should do.".to_owned());
    }
    let paths = selected(args);
    let outcome = agent.run(
        host,
        &Task {
            source: Source::Ask,
            prompt: request,
            selected: &paths,
        },
    )?;
    emit(
        host,
        "agent.reply",
        &reply_payload(&outcome, "ask", request),
    );
    if let Some(notice) = &outcome.notice {
        emit(host, "agent.notice", &json!({ "notice": notice }));
    }
    let mut result = reply_payload(&outcome, "ask", request);
    // Rule 1 of the window: a result that names a preview opens it.
    result["preview"] = json!(outcome.preview);
    Ok(result)
}

fn chat(agent: &mut Agent, host: &dyn Host, args: &Value) -> Result<Value, String> {
    let message = args["message"].as_str().map(str::trim).unwrap_or_default();
    if message.is_empty() {
        return Err("The chat message is empty.".to_owned());
    }
    let paths = selected(args);
    let outcome = agent.run(
        host,
        &Task {
            source: Source::Chat,
            prompt: message,
            selected: &paths,
        },
    )?;
    emit(
        host,
        "agent.reply",
        &reply_payload(&outcome, "chat", message),
    );
    if let Some(preview) = &outcome.preview {
        // Rule 2 of the window: an event that names a preview opens it.
        emit(host, "agent.preview", &json!({ "preview": preview }));
    }
    if let Some(notice) = &outcome.notice {
        emit(host, "agent.notice", &json!({ "notice": notice }));
    }
    Ok(reply_payload(&outcome, "chat", message))
}

fn tier(agent: &mut Agent, host: &dyn Host, args: &Value) -> Result<Value, String> {
    let wanted = args["tier"].as_u64().or_else(|| {
        args["input"]
            .as_str()
            .and_then(|text| text.trim().parse().ok())
    });
    let current = {
        let settings = agent.settings(host)?;
        agent.tier_of(&settings)
    };
    let tier = match wanted {
        Some(number) => {
            let tier = Tier::from_number(number).ok_or("The tier is 1, 2 or 3.")?;
            agent.set_tier(host, tier);
            tier
        }
        None => current,
    };
    let payload = json!({ "tier": tier.number(), "label": tier.label() });
    emit(host, "agent.tier", &payload);
    if wanted.is_some() {
        emit(
            host,
            "agent.notice",
            &json!({ "notice": format!("Agent tier {}: {}.", tier.number(), tier.label()) }),
        );
    }
    Ok(payload)
}

fn undo(agent: &mut Agent, host: &dyn Host, args: &Value) -> Result<Value, String> {
    let job = args["job"].as_u64();
    let undone = agent.undo(host, job)?;
    emit(host, "agent.notice", &json!({ "notice": undone.text }));
    Ok(json!({
        "undoes": undone.undoes,
        "left": undone.left.iter().map(|(path, reason)| json!({ "path": path, "reason": reason })).collect::<Vec<_>>(),
        "text": undone.text,
    }))
}

fn show_audit(host: &dyn Host, args: &Value) -> Result<Value, String> {
    let count = args["n"]
        .as_u64()
        .and_then(|n| usize::try_from(n).ok())
        .unwrap_or(20);
    let entries = audit::last(host, count)?;
    emit(host, "agent.audit", &json!({ "entries": entries }));
    Ok(json!({ "entries": entries }))
}

/// A line for the status bar (the window shows any event whose payload has
/// a string `notice`).
fn notice(host: &dyn Host, text: &str) {
    emit(host, "agent.notice", &json!({ "notice": text }));
}

/// The rules and where each stands, for the chat page.
fn emit_rules(agent: &Agent, host: &dyn Host) {
    emit(
        host,
        "agent.rules",
        &json!({ "rules": agent.rules.to_json() }),
    );
}

/// The folder a rule command names: `folder`, or the text of the palette's
/// box, with or without quotes.
fn folder_of(args: &Value) -> Result<String, String> {
    let text = args["folder"]
        .as_str()
        .or_else(|| args["input"].as_str())
        .map(|text| text.trim().trim_matches('"').trim())
        .unwrap_or_default();
    if text.is_empty() {
        Err("Name the folder of the rule.".to_owned())
    } else {
        Ok(text.to_owned())
    }
}

fn rule_add(agent: &mut Agent, host: &dyn Host, args: &Value) -> Result<Value, String> {
    let (folder, rule) = match (args["folder"].as_str(), args["rule"].as_str()) {
        (Some(folder), Some(rule)) => (folder.trim().to_owned(), rule.trim().to_owned()),
        _ => rules::split_folder_and_rule(args["input"].as_str().unwrap_or_default())?,
    };
    agent.add_rule(host, &folder, &rule)?;
    notice(host, &format!("Watching {folder}: {rule}"));
    emit_rules(agent, host);
    Ok(json!({ "rules": agent.rules.to_json() }))
}

fn rule_remove(agent: &mut Agent, host: &dyn Host, args: &Value) -> Result<Value, String> {
    let folder = folder_of(args)?;
    agent.remove_rule(host, &folder)?;
    notice(host, &format!("The watch rule for {folder} is removed."));
    emit_rules(agent, host);
    Ok(json!({ "rules": agent.rules.to_json() }))
}

fn rule_resume(agent: &mut Agent, host: &dyn Host, args: &Value) -> Result<Value, String> {
    let folder = folder_of(args)?;
    agent.resume_rule(host, &folder)?;
    notice(host, &format!("The watch rule for {folder} works again."));
    emit_rules(agent, host);
    Ok(json!({ "rules": agent.rules.to_json() }))
}

/// The rules, to the chat page; and, unless the page asks for `quiet`, a
/// line for the status bar, which is what the palette shows.
fn rule_list(agent: &Agent, host: &dyn Host, args: &Value) -> Value {
    emit_rules(agent, host);
    let rules = agent.rules.to_json();
    let text = match rules.as_array().map(Vec::as_slice) {
        None | Some([]) => "There are no watch rules.".to_owned(),
        Some(list) => format!(
            "Watch rules: {}",
            list.iter()
                .map(|rule| format!(
                    "{} ({})",
                    rule["folder"].as_str().unwrap_or_default(),
                    rule["status"].as_str().unwrap_or_default()
                ))
                .collect::<Vec<_>>()
                .join(", ")
        ),
    };
    if args["quiet"] != true {
        notice(host, &text);
    }
    json!({ "rules": rules })
}

/// What one watch rule made of one new file, told to the chat page and the
/// window: the model's answer, the preview to look at (tier 2) or the
/// notice of what was applied (tier 3), or why it failed.
fn report_rule(host: &dyn Host, job: &RuleJob, outcome: Result<Outcome, String>) {
    let folder = paths::file_name(&job.folder);
    let file = paths::file_name(&job.file);
    match outcome {
        Ok(outcome) => {
            let prompt = format!("{folder}: {} ({file})", job.rule);
            emit(
                host,
                "agent.reply",
                &reply_payload(&outcome, "rule", &prompt),
            );
            let count = outcome.changes.len();
            let plural = if count == 1 { "" } else { "s" };
            if let Some(preview) = &outcome.preview {
                if outcome.jobs.is_empty() {
                    // Rule 2 of the window opens it; only the user applies it.
                    emit(host, "agent.preview", &json!({ "preview": preview }));
                    notice(
                        host,
                        &format!(
                            "Rule for {folder}: {count} change{plural} for {file} wait for you."
                        ),
                    );
                } else {
                    notice(
                        host,
                        &format!(
                            "Rule for {folder}: applied {count} change{plural} to {file}. agent.undo reverses them."
                        ),
                    );
                }
            }
        }
        Err(error) => notice(
            host,
            &format!("Rule for {folder} failed on {file}: {error}"),
        ),
    }
}

/// A folder the agent watches got new files: the rule deals with each.
fn folder_changed(agent: &mut Agent, host: &dyn Host, payload: &Value) {
    let batch = agent.rule_jobs(host, payload);
    for text in &batch.notices {
        notice(host, text);
    }
    for job in &batch.jobs {
        badge(host, Some("spinner"));
        let run = agent.run_rule(host, job);
        badge(host, None);
        report_rule(host, job, run.outcome);
        if let Some(text) = run.paused {
            notice(host, &text);
            emit_rules(agent, host);
        }
    }
}

/// Something the plugin asked to hear about happened.
pub fn on_event(agent: &mut Agent, host: &dyn Host, name: &str, payload: &str) {
    let payload: Value = serde_json::from_str(payload).unwrap_or(Value::Null);
    match name {
        "preview-applied" | "preview-cancelled" => agent.preview_event(host, name, &payload),
        "folder-changed" => folder_changed(agent, host, &payload),
        "folder-unwatched" => {
            if let Some(text) = agent.watch_ended(&payload) {
                notice(host, &text);
                emit_rules(agent, host);
            }
        }
        "settings-changed" => {
            for text in agent.sync_rules(host) {
                notice(host, &text);
            }
            emit_rules(agent, host);
        }
        _ => {}
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testing::FakeHost;

    fn setup(replies: &[&str], tier: u64) -> (FakeHost, Agent) {
        let host = FakeHost::new();
        host.set_settings(
            &json!({ "provider": "fake", "fakeReplies": "replies.json", "tier": tier }),
        );
        host.put_file("replies.json", &serde_json::to_string(replies).unwrap());
        let agent = Agent::new(&host, vec![r"C:\Users\Me".to_owned()], vec![]);
        (host, agent)
    }

    #[test]
    fn ask_answers_with_the_preview_in_its_result_and_tells_the_page_without_a_preview_field() {
        let (host, mut agent) = setup(&["Renaming.\n```\nrename C:\\Users\\Me\\a b\n```"], 2);
        let result = on_command(
            &mut agent,
            &host,
            "agent.ask",
            &json!({ "input": "rename a", "paths": [r"C:\Users\Me\a"] }).to_string(),
        )
        .unwrap();
        let result: Value = serde_json::from_str(&result).unwrap();
        assert_eq!(
            result["preview"], "preview-1",
            "rule 1: the window opens it"
        );
        assert_eq!(result["text"], "Renaming.");
        assert_eq!(result["commands"][0]["status"], "proposed");
        let replies = host.events_named("agent.reply");
        assert_eq!(replies.len(), 1);
        assert!(
            replies[0].get("preview").is_none(),
            "or the window would open it twice"
        );
        assert_eq!(
            (replies[0]["source"].as_str(), replies[0]["prompt"].as_str()),
            (Some("ask"), Some("rename a")),
            "the chat shows what was asked"
        );
        assert_eq!(
            replies[0]["commands"][0],
            json!({ "line": r"rename C:\Users\Me\a b", "kind": "write", "status": "proposed" })
        );
        assert!(host.events_named("agent.preview").is_empty());
        assert_eq!(
            host.events_named("badge"),
            [
                json!({ "view": "agent-chat", "kind": "spinner" }),
                json!({ "view": "agent-chat", "kind": null })
            ],
            "the chat's button spins while the model is asked"
        );
        // The selection went to the model.
        let first = String::from_utf8(host.files.borrow()["fake-requests.jsonl"].clone()).unwrap();
        assert!(first.contains(r"C:\\Users\\Me\\a"), "{first}");
    }

    #[test]
    fn chat_names_the_preview_in_an_event_the_window_opens_and_not_in_its_result() {
        let (host, mut agent) = setup(&["```\nmkdir C:\\Users\\Me\\S\n```"], 2);
        let result = on_command(
            &mut agent,
            &host,
            "agent.chat",
            &json!({ "message": "make S", "paths": [] }).to_string(),
        )
        .unwrap();
        let result: Value = serde_json::from_str(&result).unwrap();
        assert!(result.get("preview").is_none());
        assert_eq!(
            host.events_named("agent.preview"),
            [json!({ "preview": "preview-1" })]
        );
        assert_eq!(host.events_named("agent.reply").len(), 1);
    }

    #[test]
    fn a_tier_three_apply_puts_a_notice_in_the_status_bar() {
        let (host, mut agent) = setup(&["```\nmkdir C:\\Users\\Me\\S\n```"], 3);
        on_command(
            &mut agent,
            &host,
            "agent.chat",
            &json!({ "message": "make S" }).to_string(),
        )
        .unwrap();
        let notices = host.events_named("agent.notice");
        assert_eq!(notices.len(), 1);
        assert!(
            notices[0]["notice"]
                .as_str()
                .unwrap()
                .contains("Applied 1 change"),
            "{notices:?}"
        );
    }

    #[test]
    fn a_failure_reaches_the_caller_and_the_page() {
        let (host, mut agent) = setup(&[], 2);
        let error = on_command(
            &mut agent,
            &host,
            "agent.ask",
            &json!({ "input": "x" }).to_string(),
        )
        .unwrap_err();
        assert!(error.contains("no more replies"), "{error}");
        assert_eq!(host.events_named("agent.error"), [json!({ "text": error })]);
        assert!(
            on_command(&mut agent, &host, "agent.ask", "{}")
                .unwrap_err()
                .contains("Type what the agent should do")
        );
        assert!(
            on_command(&mut agent, &host, "agent.chat", r#"{"message": "  "}"#)
                .unwrap_err()
                .contains("empty")
        );
        assert!(
            on_command(&mut agent, &host, "agent.frob", "{}")
                .unwrap_err()
                .contains("no command")
        );
        assert!(
            on_command(&mut agent, &host, "agent.ask", "not json")
                .unwrap_err()
                .contains("not JSON")
        );
    }

    #[test]
    fn the_tier_is_read_and_set_and_the_page_hears_it() {
        let (host, mut agent) = setup(&[], 2);
        let shown = on_command(&mut agent, &host, "agent.tier", "{}").unwrap();
        assert_eq!(
            serde_json::from_str::<Value>(&shown).unwrap(),
            json!({ "tier": 2, "label": "Diff and approve" })
        );
        assert!(
            host.events_named("agent.notice").is_empty(),
            "looking changes nothing"
        );
        on_command(
            &mut agent,
            &host,
            "agent.tier",
            &json!({ "tier": 1 }).to_string(),
        )
        .unwrap();
        on_command(
            &mut agent,
            &host,
            "agent.tier",
            &json!({ "input": " 3 " }).to_string(),
        )
        .unwrap();
        let heard = host.events_named("agent.tier");
        assert_eq!(
            heard.last().unwrap(),
            &json!({ "tier": 3, "label": "Autonomous" })
        );
        assert_eq!(heard.len(), 3);
        assert!(
            on_command(
                &mut agent,
                &host,
                "agent.tier",
                &json!({ "tier": 4 }).to_string()
            )
            .unwrap_err()
            .contains("1, 2 or 3")
        );
    }

    #[test]
    fn undo_and_the_audit_list_answer_and_tell_the_page() {
        let (host, mut agent) = setup(&["```\nmkdir C:\\Users\\Me\\S\n```"], 2);
        on_command(
            &mut agent,
            &host,
            "agent.ask",
            &json!({ "input": "x" }).to_string(),
        )
        .unwrap();
        let undone = on_command(
            &mut agent,
            &host,
            "agent.undo",
            &json!({ "job": 5 }).to_string(),
        )
        .unwrap();
        assert!(
            serde_json::from_str::<Value>(&undone).unwrap()["text"]
                .as_str()
                .unwrap()
                .starts_with("Undid 1 job")
        );
        assert!(
            host.events_named("agent.notice")[0]["notice"]
                .as_str()
                .unwrap()
                .starts_with("Undid")
        );
        let listed = on_command(
            &mut agent,
            &host,
            "agent.audit",
            &json!({ "n": 10 }).to_string(),
        )
        .unwrap();
        let entries = serde_json::from_str::<Value>(&listed).unwrap()["entries"]
            .as_array()
            .unwrap()
            .clone();
        assert_eq!(entries.len(), 2, "the ask and the undo");
        assert_eq!(entries[0]["source"], "ask");
        assert_eq!(entries[1]["source"], "undo");
        assert_eq!(
            host.events_named("agent.audit")[0]["entries"]
                .as_array()
                .unwrap()
                .len(),
            2
        );
    }

    #[test]
    fn the_table_of_commands_is_what_plugin_json_declares() {
        let manifest: Value = serde_json::from_str(include_str!("../plugin.json")).unwrap();
        let declared = manifest["commands"].as_array().unwrap();
        assert_eq!(declared.len(), COMMANDS.len());
        for (spec, declared) in COMMANDS.iter().zip(declared) {
            assert_eq!(declared["id"], spec.id);
            assert_eq!(declared["title"], spec.title);
            assert_eq!(declared["category"], spec.category);
            assert_eq!(declared["defaultKeys"], json!(spec.keys));
        }
        for id in ["agent.rule.add", "agent.rule.remove", "agent.rule.resume"] {
            let command = declared.iter().find(|command| command["id"] == id).unwrap();
            assert!(
                command["input"]["title"].is_string(),
                "{id} asks for text in the palette"
            );
        }
        // Ctrl+K alone is the chord prefix: the key is a chord.
        assert_eq!(declared[0]["defaultKeys"], json!(["ctrl+k ctrl+a"]));
        assert_eq!(declared[0]["input"]["title"], "Agent: Ask");
    }

    // ----- Watch rules -----

    const INBOX: &str = r"C:\Users\Me\Inbox";

    fn with_rule(replies: &[&str], tier: u64) -> (FakeHost, Agent) {
        let host = FakeHost::new();
        host.set_settings(&json!({
            "provider": "fake", "fakeReplies": "replies.json", "tier": tier,
            "rules": [{ "folder": INBOX, "rule": "sort by year" }],
        }));
        host.put_file("replies.json", &serde_json::to_string(replies).unwrap());
        host.put_folder(
            INBOX,
            vec![crate::host::DirEntry {
                name: "a.jpg".to_owned(),
                is_dir: false,
                size: 10,
                modified_ms: None,
            }],
        );
        let mut agent = Agent::new(&host, vec![r"C:\Users\Me".to_owned()], vec![]);
        agent.sync_rules(&host);
        (host, agent)
    }

    fn new_file() -> String {
        json!({
            "path": INBOX,
            "overflow": false,
            "changes": [{ "kind": "created", "path": format!(r"{INBOX}\a.jpg"), "old_path": null }],
        })
        .to_string()
    }

    fn notices(host: &FakeHost) -> Vec<String> {
        host.events_named("agent.notice")
            .iter()
            .map(|payload| payload["notice"].as_str().unwrap().to_owned())
            .collect()
    }

    #[test]
    fn at_tier_two_a_new_file_reaches_the_chat_as_a_reply_and_the_window_as_a_preview() {
        let (host, mut agent) = with_rule(&["```\nmkdir C:\\Users\\Me\\Inbox\\2026\n```"], 2);
        on_event(&mut agent, &host, "folder-changed", &new_file());
        let replies = host.events_named("agent.reply");
        assert_eq!(replies.len(), 1);
        assert_eq!(replies[0]["source"], "rule");
        assert!(
            replies[0]["prompt"]
                .as_str()
                .unwrap()
                .contains("Inbox: sort by year (a.jpg)"),
            "{}",
            replies[0]["prompt"]
        );
        assert!(
            replies[0].get("preview").is_none(),
            "or the window would open it twice"
        );
        // Rule 2 of the window: an event that names a preview opens it.
        assert_eq!(
            host.events_named("agent.preview"),
            [json!({ "preview": "preview-1" })]
        );
        assert_eq!(
            notices(&host),
            ["Rule for Inbox: 1 change for a.jpg wait for you."]
        );
        assert_eq!(
            host.events_named("badge"),
            [
                json!({ "view": "agent-chat", "kind": "spinner" }),
                json!({ "view": "agent-chat", "kind": null })
            ]
        );
        assert!(host.requests_of("preview_apply").is_empty());
    }

    #[test]
    fn at_tier_three_a_new_file_is_applied_and_the_notice_says_so() {
        let (host, mut agent) = with_rule(&["```\nmkdir C:\\Users\\Me\\Inbox\\2026\n```"], 3);
        on_event(&mut agent, &host, "folder-changed", &new_file());
        assert!(
            host.events_named("agent.preview").is_empty(),
            "nothing is left to approve"
        );
        assert_eq!(
            notices(&host),
            ["Rule for Inbox: applied 1 change to a.jpg. agent.undo reverses them."]
        );
        let undone = on_command(&mut agent, &host, "agent.undo", "{}").unwrap();
        assert!(undone.contains("undoes"), "{undone}");
    }

    #[test]
    fn a_rule_that_keeps_failing_says_so_and_then_is_paused_in_the_chat_s_list() {
        let (host, mut agent) = with_rule(&[], 2);
        for _ in 0..3 {
            on_event(&mut agent, &host, "folder-changed", &new_file());
        }
        let told = notices(&host);
        assert_eq!(told.len(), 4, "{told:?}");
        assert!(
            told[0].starts_with("Rule for Inbox failed on a.jpg:"),
            "{told:?}"
        );
        assert!(told[3].contains("3 failures in a row"), "{told:?}");
        let rules = host.events_named("agent.rules");
        assert_eq!(rules.last().unwrap()["rules"][0]["status"], "paused");
        assert!(host.events_named("agent.reply").is_empty());
        // The palette lists the rules, and the chat page hears them too.
        let listed = on_command(&mut agent, &host, "agent.rule.list", "{}").unwrap();
        assert!(listed.contains("paused"), "{listed}");
        assert!(
            notices(&host)
                .last()
                .unwrap()
                .starts_with("Watch rules: C:\\Users\\Me\\Inbox (paused)")
        );
        // The chat page asks for the list without a line in the status bar.
        let lines = notices(&host).len();
        on_command(&mut agent, &host, "agent.rule.list", r#"{"quiet":true}"#).unwrap();
        assert_eq!(notices(&host).len(), lines);
        // Resume it.
        on_command(
            &mut agent,
            &host,
            "agent.rule.resume",
            &json!({ "input": INBOX }).to_string(),
        )
        .unwrap();
        assert_eq!(
            host.events_named("agent.rules").last().unwrap()["rules"][0]["status"],
            "watching"
        );
    }

    #[test]
    fn rules_are_added_and_removed_with_commands_the_palette_and_the_page_can_send() {
        let (host, mut agent) = with_rule(&[], 2);
        // The palette's one text box: a quoted folder and the rule.
        let added = on_command(
            &mut agent,
            &host,
            "agent.rule.add",
            &json!({ "input": r#""C:\Users\Me\My Downloads" sort by type"# }).to_string(),
        )
        .unwrap();
        let added: Value = serde_json::from_str(&added).unwrap();
        assert_eq!(added["rules"].as_array().unwrap().len(), 2);
        assert_eq!(added["rules"][1]["folder"], r"C:\Users\Me\My Downloads");
        assert_eq!(added["rules"][1]["origin"], "added");
        assert_eq!(
            notices(&host).last().unwrap(),
            r"Watching C:\Users\Me\My Downloads: sort by type"
        );
        // The page sends the two parts apart (and an input, so that the window does not ask).
        on_command(
            &mut agent,
            &host,
            "agent.rule.add",
            &json!({ "folder": r"C:\Users\Me\Docs", "rule": "sort", "input": "sort" }).to_string(),
        )
        .unwrap();
        assert_eq!(
            host.events_named("agent.rules").last().unwrap()["rules"]
                .as_array()
                .unwrap()
                .len(),
            3
        );
        on_command(
            &mut agent,
            &host,
            "agent.rule.remove",
            &json!({ "folder": r"C:\Users\Me\Docs" }).to_string(),
        )
        .unwrap();
        assert_eq!(
            host.events_named("agent.rules").last().unwrap()["rules"]
                .as_array()
                .unwrap()
                .len(),
            2
        );
        // A mistake is an error the page and the palette can show.
        let error = on_command(
            &mut agent,
            &host,
            "agent.rule.add",
            r#"{"input":"C:\\Inbox"}"#,
        )
        .unwrap_err();
        assert!(error.contains("what to do with each new file"), "{error}");
        assert_eq!(
            host.events_named("agent.error").last().unwrap()["text"],
            error
        );
        let error = on_command(&mut agent, &host, "agent.rule.remove", "{}").unwrap_err();
        assert!(error.contains("Name the folder"), "{error}");
    }

    #[test]
    fn a_settings_change_and_a_watch_the_core_ended_reach_the_rules_and_the_page() {
        let (host, mut agent) = with_rule(&[], 2);
        on_event(
            &mut agent,
            &host,
            "folder-unwatched",
            &json!({ "path": INBOX, "message": "the folder was deleted" }).to_string(),
        );
        assert!(notices(&host)[0].contains("the folder was deleted"));
        assert_eq!(
            host.events_named("agent.rules").last().unwrap()["rules"][0]["status"],
            "blocked"
        );
        // The settings changed: the rule for another folder replaces it.
        host.set_settings(&json!({
            "provider": "fake",
            "rules": [{ "folder": r"C:\Users\Me\Photos", "rule": "sort" }],
        }));
        on_event(&mut agent, &host, "settings-changed", "{}");
        let rules = host.events_named("agent.rules").last().unwrap().clone();
        assert_eq!(rules["rules"].as_array().unwrap().len(), 1);
        assert_eq!(rules["rules"][0]["folder"], r"C:\Users\Me\Photos");
        assert_eq!(*host.watched.borrow(), [r"C:\Users\Me\Photos"]);
    }

    #[test]
    fn the_manifest_asks_to_watch_and_names_the_rule_commands() {
        let manifest: Value = serde_json::from_str(include_str!("../plugin.json")).unwrap();
        let watch = manifest["capabilities"]
            .as_array()
            .unwrap()
            .iter()
            .find(|capability| capability["name"] == "fs:watch")
            .expect("the agent watches folders");
        assert_eq!(
            watch["roots"], manifest["capabilities"][3]["roots"],
            "the same folders it may read"
        );
    }
}
