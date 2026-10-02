//! The core's side of the terminal sessions (`cabinetos-terminal`,
//! `docs/terminal.md`): the session manager, and the profiles from the
//! `terminal` settings.

use std::path::PathBuf;
use std::sync::Arc;

use cabinetos_config::{ProfileHook, TerminalConfig};
use cabinetos_protocol::{ErrorCode, TerminalMode};
use cabinetos_terminal::{Hook, HookHost, Profile, Terminals, linkable_by_default};

use crate::events::EventHub;
use crate::listing::Failure;

/// The session manager: its sessions' pipes are served on the current
/// runtime, and their `terminal_exited`, `terminal_mode_changed` and
/// `terminal_folder_changed` events go to every connection. Every shell
/// gets the core's pipe token; a hooked one runs the command line next to
/// the core (`cabinetos-cli.exe`) at each prompt.
pub(crate) fn start(events: &Arc<EventHub>, pipe_token: &str) -> Arc<Terminals> {
    let events = Arc::clone(events);
    let cli = command_line();
    if cli.is_none() {
        tracing::warn!(
            "cabinetos-cli.exe is not next to the core: linked terminal sessions cannot follow \
             their pane (their prompt hook only reports the folder)"
        );
    }
    Arc::new(
        Terminals::new(
            tokio::runtime::Handle::current(),
            Arc::new(move |event| events.publish(event)),
        )
        .with_host(HookHost {
            pipe_token: pipe_token.to_owned(),
            cli,
        }),
    )
}

/// `cabinetos-cli.exe` in the core's own folder, when it is there.
fn command_line() -> Option<PathBuf> {
    let cli = std::env::current_exe()
        .ok()?
        .with_file_name("cabinetos-cli.exe");
    cli.is_file().then_some(cli)
}

/// The profile `name` names, or `terminal.defaultProfile` when it names
/// none; `unknown_profile` when no profile has that name. A profile that
/// does not say `hook` has CabinetOS's own; one that does not say
/// `linkable` is linkable when its program is one a prompt hook can be
/// added to and its hook is not off.
pub(crate) fn profile(config: &TerminalConfig, name: Option<&str>) -> Result<Profile, Failure> {
    let name = name.unwrap_or(&config.default_profile);
    config
        .profiles
        .iter()
        .find(|profile| profile.name == name)
        .map(|profile| {
            let hook = match &profile.hook {
                None | Some(ProfileHook::On(true)) => Hook::Builtin,
                Some(ProfileHook::On(false)) => Hook::Off,
                Some(ProfileHook::Code(code)) => Hook::Custom(code.clone()),
            };
            Profile {
                name: profile.name.clone(),
                command: profile.command.clone(),
                args: profile.args.clone(),
                linkable: profile
                    .linkable
                    .unwrap_or_else(|| hook != Hook::Off && linkable_by_default(&profile.command)),
                hook,
            }
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

/// The mode a session starts in when its client names none:
/// `terminal.defaultMode`, but `locked` for a profile that cannot be linked,
/// since `linked` there would be `not_linkable` and start no shell.
pub(crate) fn default_mode(config: &TerminalConfig, profile: &Profile) -> TerminalMode {
    if profile.linkable {
        config.default_mode
    } else {
        TerminalMode::Locked
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_new_session_takes_the_default_mode_unless_its_profile_cannot_be_linked() {
        let config: TerminalConfig = serde_json::from_str(
            r#"{"defaultProfile": "ps", "defaultMode": "linked", "profiles": [
                {"name": "ps", "command": "pwsh.exe"},
                {"name": "cmd", "command": "cmd.exe"},
                {"name": "hooked-cmd", "command": "cmd.exe", "linkable": true},
                {"name": "plain-pwsh", "command": "pwsh.exe", "linkable": false}
            ]}"#,
        )
        .unwrap();
        let mode = |name: &str| default_mode(&config, &profile(&config, Some(name)).unwrap());
        assert_eq!(mode("ps"), TerminalMode::Linked);
        assert_eq!(mode("hooked-cmd"), TerminalMode::Linked);
        assert_eq!(mode("cmd"), TerminalMode::Locked, "no hook can follow");
        assert_eq!(
            mode("plain-pwsh"),
            TerminalMode::Locked,
            "the profile says no"
        );
        // Locked is the default, and says locked for every profile.
        let plain = TerminalConfig::default();
        assert_eq!(
            default_mode(&plain, &profile(&plain, None).unwrap()),
            TerminalMode::Locked
        );
    }

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
    fn a_profile_s_hook_is_cabinetos_own_unless_it_says_otherwise() {
        let config: TerminalConfig = serde_json::from_str(
            r#"{"defaultProfile": "ps", "profiles": [
                {"name": "ps", "command": "pwsh.exe"},
                {"name": "off", "command": "pwsh.exe", "hook": false},
                {"name": "off-but-linkable", "command": "pwsh.exe", "hook": false, "linkable": true},
                {"name": "own", "command": "wsl.exe", "hook": "my_follow"},
                {"name": "on", "command": "pwsh.exe", "hook": true}
            ]}"#,
        )
        .unwrap();
        let read = |name: &str| {
            let profile = profile(&config, Some(name)).unwrap();
            (profile.hook, profile.linkable)
        };
        assert_eq!(read("ps"), (Hook::Builtin, true));
        assert_eq!(read("on"), (Hook::Builtin, true));
        assert_eq!(
            read("off"),
            (Hook::Off, false),
            "no hook: nothing to follow with"
        );
        assert_eq!(read("off-but-linkable"), (Hook::Off, true));
        assert_eq!(read("own"), (Hook::Custom("my_follow".to_owned()), true));
        assert_eq!(profile(&config, Some("ps")).unwrap().hook, Hook::Builtin);
    }

    #[test]
    fn an_unknown_name_lists_the_profiles() {
        let (code, message) = profile(&TerminalConfig::default(), Some("fish")).unwrap_err();
        assert_eq!(code, ErrorCode::UnknownProfile);
        assert!(message.contains("`fish`"), "{message}");
        assert!(message.contains("pwsh, cmd, wsl"), "{message}");
    }
}
