//! The core's side of the terminal sessions (`cabinetos-terminal`,
//! `docs/terminal.md`): the session manager, and the profiles from the
//! `terminal` settings.

use std::sync::Arc;

use cabinetos_config::TerminalConfig;
use cabinetos_protocol::ErrorCode;
use cabinetos_terminal::{Profile, Terminals, linkable_by_default};

use crate::events::EventHub;
use crate::listing::Failure;

/// The session manager: its sessions' pipes are served on the current
/// runtime, and their `terminal_exited` and `terminal_mode_changed` events
/// go to every connection.
pub(crate) fn start(events: &Arc<EventHub>) -> Arc<Terminals> {
    let events = Arc::clone(events);
    Arc::new(Terminals::new(
        tokio::runtime::Handle::current(),
        Arc::new(move |event| events.publish(event)),
    ))
}

/// The profile `name` names, or `terminal.defaultProfile` when it names
/// none; `unknown_profile` when no profile has that name. A profile that
/// does not say `linkable` is linkable when its program is one a prompt
/// hook can be added to.
pub(crate) fn profile(config: &TerminalConfig, name: Option<&str>) -> Result<Profile, Failure> {
    let name = name.unwrap_or(&config.default_profile);
    config
        .profiles
        .iter()
        .find(|profile| profile.name == name)
        .map(|profile| Profile {
            name: profile.name.clone(),
            command: profile.command.clone(),
            args: profile.args.clone(),
            linkable: profile
                .linkable
                .unwrap_or_else(|| linkable_by_default(&profile.command)),
        })
        .ok_or_else(|| {
            let known: Vec<&str> = config
                .profiles
                .iter()
                .map(|profile| profile.name.as_str())
                .collect();
            (
                ErrorCode::UnknownProfile,
                format!(
                    "no terminal profile `{name}`; the profiles are: {}",
                    known.join(", ")
                ),
            )
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_missing_name_means_the_default_profile() {
        let config = TerminalConfig::default();
        let default = profile(&config, None).unwrap();
        assert_eq!(default.name, "pwsh");
        assert_eq!(default.command, "pwsh.exe");
        assert_eq!(default.args, ["-NoLogo"]);
        assert!(default.linkable);
        let cmd = profile(&config, Some("cmd")).unwrap();
        assert_eq!(cmd.command, "cmd.exe");
        assert!(!cmd.linkable);
        assert!(!profile(&config, Some("claude")).unwrap().linkable);
    }

    #[test]
    fn a_profile_without_linkable_is_linkable_when_its_program_is() {
        let config: TerminalConfig = serde_json::from_str(
            r#"{"defaultProfile": "ps", "profiles": [
                {"name": "ps", "command": "C:\\PowerShell\\pwsh.exe"},
                {"name": "bash", "command": "wsl.exe"},
                {"name": "nu", "command": "nu.exe"},
                {"name": "hooked-cmd", "command": "cmd.exe", "linkable": true},
                {"name": "plain-pwsh", "command": "pwsh.exe", "linkable": false}
            ]}"#,
        )
        .unwrap();
        let linkable = |name: &str| profile(&config, Some(name)).unwrap().linkable;
        assert!(linkable("ps"));
        assert!(linkable("bash"));
        assert!(!linkable("nu"));
        assert!(linkable("hooked-cmd"), "the profile's own word wins");
        assert!(!linkable("plain-pwsh"));
    }

    #[test]
    fn an_unknown_name_lists_the_profiles() {
        let (code, message) = profile(&TerminalConfig::default(), Some("fish")).unwrap_err();
        assert_eq!(code, ErrorCode::UnknownProfile);
        assert!(message.contains("`fish`"), "{message}");
        assert!(message.contains("pwsh, cmd, wsl"), "{message}");
    }
}
