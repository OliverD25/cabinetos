//! The core's side of the terminal sessions (`cabinetos-terminal`,
//! `docs/terminal.md`): the session manager, and the profiles from the
//! `terminal` settings.

use std::sync::Arc;

use cabinetos_config::TerminalConfig;
use cabinetos_protocol::ErrorCode;
use cabinetos_terminal::{Profile, Terminals};

use crate::events::EventHub;
use crate::listing::Failure;

/// The session manager: its sessions' pipes are served on the current
/// runtime, and their `terminal_exited` events go to every connection.
pub(crate) fn start(events: &Arc<EventHub>) -> Arc<Terminals> {
    let events = Arc::clone(events);
    Arc::new(Terminals::new(
        tokio::runtime::Handle::current(),
        Arc::new(move |event| events.publish(event)),
    ))
}

/// The profile `name` names, or `terminal.defaultProfile` when it names
/// none; `unknown_profile` when no profile has that name.
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
        assert_eq!(profile(&config, Some("cmd")).unwrap().command, "cmd.exe");
    }

    #[test]
    fn an_unknown_name_lists_the_profiles() {
        let (code, message) = profile(&TerminalConfig::default(), Some("fish")).unwrap_err();
        assert_eq!(code, ErrorCode::UnknownProfile);
        assert!(message.contains("`fish`"), "{message}");
        assert!(message.contains("pwsh, cmd, wsl"), "{message}");
    }
}
