//! `cabinetos-cli config`, `commands` and `keys`: the configuration file,
//! the command registry and the keymap, as the running core sees them.

use std::collections::BTreeSet;
use std::path::{Path, PathBuf};
use std::time::SystemTime;

use anyhow::{Context, bail};
use cabinetos_commands::{CommandRegistry, compile};
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{CommandInfo, CommandSource, Envelope, Event, Keymap, Request, Response};
use serde_json::Value;

use crate::{expect_welcome, failure, say, send};

/// How many results `commands search` asks for: the protocol's default.
const SEARCH_LIMIT: u32 = 20;

/// `config path`: the file the running core reads.
pub(crate) async fn config_path(client: &mut PipeClient) -> anyhow::Result<()> {
    let (path, _) = get_config(client).await?;
    say(format_args!("{path}"));
    Ok(())
}

/// `config show`: the settings in effect, defaults included.
pub(crate) async fn config_show(client: &mut PipeClient) -> anyhow::Result<()> {
    let (_, config) = get_config(client).await?;
    say(format_args!("{}", serde_json::to_string_pretty(&config)?));
    Ok(())
}

/// `config get <path>`: one setting in effect, as JSON.
pub(crate) async fn config_get(client: &mut PipeClient, path: &str) -> anyhow::Result<()> {
    let reply = send(
        client,
        Request::GetValue {
            path: path.to_owned(),
        },
    )
    .await?;
    match reply.body {
        Response::Value { value } => {
            say(format_args!("{}", serde_json::to_string_pretty(&value)?));
            Ok(())
        }
        other => Err(failure(path, &other)),
    }
}

/// `config set <path> <value>`: the core checks the value and writes the
/// file.
pub(crate) async fn config_set(
    client: &mut PipeClient,
    path: &str,
    text: &str,
) -> anyhow::Result<()> {
    let value = json_or_text(text);
    let reply = send(
        client,
        Request::SetValue {
            path: path.to_owned(),
            value: value.clone(),
        },
    )
    .await?;
    match reply.body {
        Response::Ok => {
            say(format_args!("{path} = {value}"));
            Ok(())
        }
        other => Err(failure(path, &other)),
    }
}

/// `text` as JSON, or as a JSON string when it is not JSON, so `false` is a
/// boolean and `rail` needs no quotes.
fn json_or_text(text: &str) -> Value {
    serde_json::from_str(text).unwrap_or_else(|_| Value::String(text.to_owned()))
}

/// `config validate [file]`: checks a file the way the core would, without
/// a core: JSON syntax, known keys, values, and the keybindings against the
/// core's commands.
pub(crate) fn config_validate(file: Option<&Path>) -> anyhow::Result<()> {
    let path = file.map_or_else(|| cabinetos_config::default_path(None), Path::to_path_buf);
    let text = std::fs::read_to_string(&path)
        .with_context(|| format!("cannot read {}", path.display()))?;
    let registry = CommandRegistry::core();
    let mut warnings = Vec::new();
    let checked = cabinetos_config::parse_checked(&text, |config| {
        warnings = compile(&registry, &config.overrides())?.warnings;
        Ok(())
    });
    if let Err(error) = checked {
        bail!("{}: {error}", path.display());
    }
    for warning in &warnings {
        say(format_args!("warning: {warning}"));
    }
    say(format_args!("{}: ok", path.display()));
    Ok(())
}

async fn get_config(client: &mut PipeClient) -> anyhow::Result<(String, serde_json::Value)> {
    let reply = send(client, Request::GetConfig).await?;
    match reply.body {
        Response::Config { path, config } => Ok((path, config)),
        other => Err(failure("get_config", &other)),
    }
}

/// `commands list`: every command with its keys, or the raw list as JSON.
pub(crate) async fn commands_list(client: &mut PipeClient, json: bool) -> anyhow::Result<()> {
    let commands = list_commands(client).await?;
    if json {
        say(format_args!("{}", serde_json::to_string_pretty(&commands)?));
        return Ok(());
    }
    for command in &commands {
        if !say(format_args!("{}", command_line(command))) {
            break;
        }
    }
    Ok(())
}

/// `commands search`: the palette's ranking for `query`, best first.
pub(crate) async fn commands_search(client: &mut PipeClient, query: &str) -> anyhow::Result<()> {
    let reply = send(
        client,
        Request::SearchCommands {
            query: query.to_owned(),
            limit: SEARCH_LIMIT,
        },
    )
    .await?;
    let Response::SearchResults { hits } = reply.body else {
        return Err(failure("search_commands", &reply.body));
    };
    if hits.is_empty() {
        say(format_args!("no command matches `{query}`"));
        return Ok(());
    }
    let commands = list_commands(client).await?;
    for (rank, hit) in hits.iter().enumerate() {
        let line = commands
            .iter()
            .find(|command| command.id == hit.id)
            .map_or_else(|| hit.id.clone(), command_line);
        if !say(format_args!(
            "{:>2}. score {:>4}  {line}",
            rank + 1,
            hit.score
        )) {
            break;
        }
    }
    Ok(())
}

async fn list_commands(client: &mut PipeClient) -> anyhow::Result<Vec<CommandInfo>> {
    let reply = send(client, Request::ListCommands).await?;
    match reply.body {
        Response::Commands { commands } => Ok(commands),
        other => Err(failure("list_commands", &other)),
    }
}

/// `view.toggleSidebar   ctrl+b   View: Toggle Sidebar`; a plugin's
/// command ends with the plugin's name, as the palette's badge shows it.
fn command_line(command: &CommandInfo) -> String {
    let keys = if command.keys.is_empty() {
        "-".to_owned()
    } else {
        command.keys.join(", ")
    };
    let immutable = if command.immutable {
        "  (immutable)"
    } else {
        ""
    };
    let badge = match &command.source {
        CommandSource::Plugin { name, .. } => format!("  [{name}]"),
        CommandSource::Core => String::new(),
    };
    format!(
        "{:<30} {keys:<16} {}: {}{immutable}{badge}",
        command.id, command.category, command.title
    )
}

/// `keys list`: the keymap in effect.
pub(crate) async fn keys_list(client: &mut PipeClient) -> anyhow::Result<()> {
    let keymap = get_keymap(client).await?;
    for binding in &keymap.bindings {
        let when = binding
            .when
            .as_ref()
            .map(|when| format!(" when {when}"))
            .unwrap_or_default();
        let immutable = if keymap.immutable.contains(&binding.command) {
            " (immutable)"
        } else {
            ""
        };
        let line = format!(
            "{:<18} {:<30}{when}{immutable}",
            binding.keys, binding.command
        );
        if !say(format_args!("{}", line.trim_end())) {
            return Ok(());
        }
    }
    say(format_args!(
        "{} bindings; the second key of a chord must follow within {} ms",
        keymap.bindings.len(),
        keymap.chord_window_ms
    ));
    Ok(())
}

/// `keys set` and `keys reset`: change a binding through the core, which
/// writes the file; print the command's keys afterwards.
pub(crate) async fn keys_change(
    client: &mut PipeClient,
    command: &str,
    keys: Option<&str>,
) -> anyhow::Result<()> {
    let request = match keys {
        Some(keys) => Request::SetKeybinding {
            command: command.to_owned(),
            keys: keys.to_owned(),
        },
        None => Request::ResetKeybinding {
            command: command.to_owned(),
        },
    };
    let reply = send(client, request).await?;
    let Response::Keymap(keymap) = reply.body else {
        return Err(failure(command, &reply.body));
    };
    let keys: Vec<&str> = keymap
        .bindings
        .iter()
        .filter(|binding| binding.command == command)
        .map(|binding| binding.keys.as_str())
        .collect();
    let keys = if keys.is_empty() {
        "no keys".to_owned()
    } else {
        keys.join(", ")
    };
    say(format_args!("{command}: {keys}"));
    Ok(())
}

async fn get_keymap(client: &mut PipeClient) -> anyhow::Result<Keymap> {
    let reply = send(client, Request::GetKeymap).await?;
    match reply.body {
        Response::Keymap(keymap) => Ok(keymap),
        other => Err(failure("get_keymap", &other)),
    }
}

/// `keys watch`: prints configuration and keymap events until Ctrl+C, each
/// with the time since the file was last written.
pub(crate) async fn keys_watch(client: &mut PipeClient) -> anyhow::Result<()> {
    let mut events = client
        .events()
        .context("the event stream was already taken")?;
    expect_welcome(client).await?;
    let (path, _) = get_config(client).await?;
    let path = PathBuf::from(path);
    let mut keymap = get_keymap(client).await?;
    if !say(format_args!("watching {} (Ctrl+C to stop)", path.display())) {
        return Ok(());
    }
    loop {
        tokio::select! {
            event = events.recv() => {
                let Some(Envelope { body: event, .. }) = event else {
                    bail!("the connection to the core ended");
                };
                let after = since_written(&path);
                let open = match event {
                    Event::ConfigChanged { changed } => say(format_args!(
                        "config_changed{after}: {}",
                        if changed.is_empty() { "no setting changed".to_owned() } else { changed.join(", ") }
                    )),
                    Event::KeymapChanged { keymap: new } => {
                        let open = say(format_args!("keymap_changed{after}"))
                            && keymap_changes(&keymap, &new)
                                .iter()
                                .all(|line| say(format_args!("  {line}")));
                        keymap = new;
                        open
                    }
                    Event::ConfigError { line, column, message } => {
                        let at = match (line, column) {
                            (Some(line), Some(column)) => format!("line {line}, column {column}: "),
                            (Some(line), None) => format!("line {line}: "),
                            _ => String::new(),
                        };
                        say(format_args!("config_error{after}: {at}{message}"))
                    }
                    _ => true,
                };
                if !open {
                    break;
                }
            }
            _ = tokio::signal::ctrl_c() => break,
        }
    }
    Ok(())
}

/// ` (212 ms after the file was written)`, or nothing when the file's time
/// cannot be read.
fn since_written(path: &Path) -> String {
    std::fs::metadata(path)
        .and_then(|metadata| metadata.modified())
        .ok()
        .and_then(|written| SystemTime::now().duration_since(written).ok())
        .map(|elapsed| format!(" ({} ms after the file was written)", elapsed.as_millis()))
        .unwrap_or_default()
}

/// The bindings that went away (`-`) and that came (`+`).
fn keymap_changes(old: &Keymap, new: &Keymap) -> Vec<String> {
    let describe = |keymap: &Keymap| -> BTreeSet<String> {
        keymap
            .bindings
            .iter()
            .map(|binding| {
                let when = binding
                    .when
                    .as_ref()
                    .map(|when| format!(" when {when}"))
                    .unwrap_or_default();
                format!("{} {}{when}", binding.keys, binding.command)
            })
            .collect()
    };
    let (old, new) = (describe(old), describe(new));
    old.difference(&new)
        .map(|binding| format!("- {binding}"))
        .chain(new.difference(&old).map(|binding| format!("+ {binding}")))
        .collect()
}

#[cfg(test)]
mod tests {
    use cabinetos_protocol::KeymapBinding;
    use serde_json::json;

    use super::*;

    #[test]
    fn values_are_json_or_else_text() {
        assert_eq!(json_or_text("false"), json!(false));
        assert_eq!(json_or_text("3"), json!(3));
        assert_eq!(json_or_text(r#""rail""#), json!("rail"));
        assert_eq!(json_or_text("rail"), json!("rail"));
        assert_eq!(json_or_text(r#"["D:\\work"]"#), json!([r"D:\work"]));
        assert_eq!(json_or_text("[1,"), json!("[1,"));
    }

    fn keymap(bindings: &[(&str, &str)]) -> Keymap {
        Keymap {
            chord_window_ms: 1000,
            bindings: bindings
                .iter()
                .map(|(keys, command)| KeymapBinding {
                    keys: (*keys).to_owned(),
                    command: (*command).to_owned(),
                    when: None,
                })
                .collect(),
            immutable: Vec::new(),
        }
    }

    #[test]
    fn keymap_changes_list_removals_then_additions() {
        let old = keymap(&[
            ("ctrl+b", "view.toggleSidebar"),
            ("f5", "file.copyToOtherPane"),
        ]);
        let new = keymap(&[
            ("ctrl+alt+b", "view.toggleSidebar"),
            ("f5", "file.copyToOtherPane"),
        ]);
        assert_eq!(
            keymap_changes(&old, &new),
            [
                "- ctrl+b view.toggleSidebar",
                "+ ctrl+alt+b view.toggleSidebar"
            ]
        );
        assert!(keymap_changes(&new, &new).is_empty());
    }
}
