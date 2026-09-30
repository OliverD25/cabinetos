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
use crate::tier::Tier;

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
];

fn emit(host: &dyn Host, name: &str, payload: &Value) {
    host.emit(name, &payload.to_string());
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
    let result = match id {
        "agent.ask" => ask(agent, host, &args),
        "agent.chat" => chat(agent, host, &args),
        "agent.tier" => tier(agent, host, &args),
        "agent.undo" => undo(agent, host, &args),
        "agent.audit" => show_audit(host, &args),
        other => Err(format!("the agent has no command {other}")),
    };
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

/// Something the plugin asked to hear about happened.
pub fn on_event(agent: &mut Agent, host: &dyn Host, name: &str, payload: &str) {
    let payload: Value = serde_json::from_str(payload).unwrap_or(Value::Null);
    match name {
        "preview-applied" | "preview-cancelled" => agent.preview_event(host, name, &payload),
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
        // Ctrl+K alone is the chord prefix: the key is a chord.
        assert_eq!(declared[0]["defaultKeys"], json!(["ctrl+k ctrl+a"]));
        assert_eq!(declared[0]["input"]["title"], "Agent: Ask");
    }
}
