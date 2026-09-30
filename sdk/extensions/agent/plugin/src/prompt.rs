//! What the model is told: the system prompt (`prompt.md`, with the
//! command reference made from the command line's own help), and the
//! messages that carry the request and the output of the commands that
//! looked.

use crate::cmdline::{READ_COMMANDS, WRITE_COMMANDS};
use crate::tier::Tier;

/// The system prompt's text, with `{{roots}}`, `{{tier}}` and
/// `{{reference}}` to fill in.
pub const TEMPLATE: &str = include_str!("../prompt.md");

/// The most characters of one command's output the model gets.
pub const MAX_OUTPUT_CHARS: usize = 6000;

/// The commands the model is told about. `undo` only when the tier applies
/// changes at once: at the other tiers the user undoes, with
/// `agent.undo`.
#[must_use]
pub fn command_names(tier: Tier) -> Vec<&'static str> {
    READ_COMMANDS
        .iter()
        .chain(WRITE_COMMANDS.iter())
        .copied()
        .filter(|name| *name != "undo" || tier == Tier::Autonomous)
        .collect()
}

/// The system prompt for a tier and the folders the agent may use.
#[must_use]
pub fn system_prompt(tier: Tier, roots: &[String]) -> String {
    TEMPLATE
        .replace("{{roots}}", &roots.join(", "))
        .replace("{{tier}}", tier.about())
        .replace(
            "{{reference}}",
            &cabinetos_cli_args::reference(&command_names(tier)),
        )
}

/// The first message: what the user asked, what the window shows, and the
/// files they pointed at.
#[must_use]
pub fn user_message(request: &str, window: Option<&str>, selected: &[String]) -> String {
    let mut text = String::new();
    match window {
        Some(window) => text.push_str(&format!("What the window shows now:\n{window}\n\n")),
        None => text.push_str("No window has told the agent what it shows.\n\n"),
    }
    if !selected.is_empty() {
        text.push_str("Selected in the active pane:\n");
        for path in selected.iter().take(50) {
            text.push_str(&format!("- {path}\n"));
        }
        if selected.len() > 50 {
            text.push_str(&format!("- and {} more\n", selected.len() - 50));
        }
        text.push('\n');
    }
    text.push_str(&format!("Request: {request}"));
    text
}

/// The message that answers a reply with commands: what each did, or why
/// it did not run.
#[must_use]
pub fn results_message(results: &[(String, Result<String, String>)], notes: &[String]) -> String {
    let mut text = String::from("Results of your commands:\n");
    for (line, result) in results {
        text.push_str(&format!("\n$ {line}\n"));
        match result {
            Ok(output) => text.push_str(&cut(output)),
            Err(error) => text.push_str(&format!("error: {}", cut(error))),
        }
        text.push('\n');
    }
    for note in notes {
        text.push_str(&format!("\nNote: {note}\n"));
    }
    text.push_str("\nAnswer with one short sentence and, if you still need commands, one fenced block. If you are done, answer with words only.");
    text
}

fn cut(text: &str) -> String {
    if text.chars().count() <= MAX_OUTPUT_CHARS {
        return text.to_owned();
    }
    let head: String = text.chars().take(MAX_OUTPUT_CHARS).collect();
    format!(
        "{head}\n... (cut: {} more characters)",
        text.chars().count() - MAX_OUTPUT_CHARS
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn roots() -> Vec<String> {
        vec![r"C:\Users\Me".to_owned()]
    }

    #[test]
    fn the_system_prompt_names_the_folders_the_tier_and_every_command_the_model_may_use() {
        let prompt = system_prompt(Tier::Diff, &roots());
        assert!(!prompt.contains("{{"), "every placeholder is filled");
        assert!(prompt.contains(r"C:\Users\Me"));
        assert!(prompt.contains("Diff and approve"));
        for name in [
            "ls", "describe", "search", "state", "mkdir", "mkfile", "rename", "copy", "move",
            "delete",
        ] {
            assert!(
                prompt.contains(&format!("Usage: cab {name}")),
                "{name}\n{prompt}"
            );
        }
        assert!(!prompt.contains("Usage: cab undo"), "undo only at tier 3");
        assert!(!prompt.contains("--pipe"), "no way to reach a core");
        assert!(!prompt.contains("Usage: cab shutdown"));
        assert!(system_prompt(Tier::Autonomous, &roots()).contains("Usage: cab undo"));
        assert!(system_prompt(Tier::Advisor, &roots()).contains("Advisor"));
    }

    #[test]
    fn the_first_message_carries_the_request_the_window_and_the_selection() {
        let text = user_message(
            "rename these to vacation_*",
            Some("The pane that has the keyboard: left"),
            &[r"C:\p\a.jpg".to_owned(), r"C:\p\b.jpg".to_owned()],
        );
        assert!(
            text.starts_with("What the window shows now:\nThe pane that has the keyboard: left")
        );
        assert!(text.contains("Selected in the active pane:\n- C:\\p\\a.jpg\n- C:\\p\\b.jpg\n"));
        assert!(text.ends_with("Request: rename these to vacation_*"));
        let bare = user_message("hi", None, &[]);
        assert_eq!(
            bare,
            "No window has told the agent what it shows.\n\nRequest: hi"
        );
    }

    #[test]
    fn results_show_each_command_its_output_or_error_and_the_notes() {
        let text = results_message(
            &[
                (
                    r"ls C:\x".to_owned(),
                    Ok("f a.txt\n1 entries in C:\\x".to_owned()),
                ),
                (
                    "frob".to_owned(),
                    Err("`frob` is not a command you may use".to_owned()),
                ),
            ],
            &["changes in the same reply as reads are not run".to_owned()],
        );
        assert!(text.contains("$ ls C:\\x\nf a.txt"));
        assert!(text.contains("$ frob\nerror: `frob` is not a command you may use"));
        assert!(text.contains("Note: changes in the same reply as reads are not run"));
        let long = results_message(
            &[("ls".to_owned(), Ok("x".repeat(MAX_OUTPUT_CHARS + 50)))],
            &[],
        );
        assert!(long.contains("(cut: 50 more characters)"));
    }
}
