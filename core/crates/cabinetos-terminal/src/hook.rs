//! The prompt hook (`docs/terminal.md`, "The prompt hook"): a few lines a
//! shell runs each time it draws its prompt, added when the core starts the
//! shell, without touching the user's own profile files.
//!
//! - **Follow.** A linked session follows its pane: the hook runs
//!   `cabinetos-cli term cwd`, which asks the core for the pane's folder
//!   and prints it only when the session is linked. When that folder is
//!   new to the hook (the pane moved, or the session was just linked) and
//!   the shell is elsewhere, the hook changes to it; a `cd` of the user's
//!   own stays until the pane moves again. The hook runs only at the
//!   prompt, so a running command or a half-typed line is never touched,
//!   and nothing is ever typed into the shell.
//! - **Report.** Locked or linked, the hook then prints the shell's folder
//!   as `ESC ] 9 ; 9 ; <folder> ESC \` (OSC 9;9, what Windows Terminal
//!   reads), for the session's output thread to read; the bytes stay in
//!   the stream, and xterm.js ignores the sequence.
//! - **Never in the way.** The hook prints nothing of its own, swallows
//!   every error, and gives the user's prompt the last command's status.
//!
//! PowerShell (`pwsh`, `powershell`) gets it as `-NoExit -Command <script>`
//! after the profile's own arguments: the user's profile loads first, then
//! the script wraps the `prompt` function the profile left. A command line,
//! not a script file: Windows PowerShell's default execution policy refuses
//! script files, and nothing is written to disk. WSL gets it as
//! `PROMPT_COMMAND`, passed into Linux by `WSLENV` (bash only).

use std::path::Path;

use crate::Hook;
use crate::shell::{ShellKind, powershell_quoted};

/// What the core adds to a shell's start for its hook.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub(crate) struct HookSetup {
    /// Arguments after the profile's own.
    pub(crate) args: Vec<String>,
    /// Environment variables, added or replaced.
    pub(crate) env: Vec<(String, String)>,
}

/// The hook for a shell of `kind` started with `args`: `Ok(None)` when the
/// profile has none (`hook: false`) or no hook can be added to the program
/// (cmd and any other program); an error says why a PowerShell profile's
/// arguments leave no room for it. `cli` is the command line's program,
/// which the built-in hook runs; without it the hook only reports the
/// folder. `wslenv` is the `WSLENV` the shell would inherit.
pub(crate) fn setup(
    kind: ShellKind,
    hook: &Hook,
    cli: Option<&Path>,
    args: &[String],
    wslenv: Option<&str>,
) -> Result<Option<HookSetup>, String> {
    if matches!(kind, ShellKind::Cmd | ShellKind::Other) {
        return Ok(None);
    }
    let follow = match hook {
        Hook::Off => return Ok(None),
        Hook::Builtin => cli.map(|cli| builtin_follow(kind, cli)).unwrap_or_default(),
        Hook::Custom(code) => code.clone(),
    };
    match kind {
        ShellKind::PowerShell => {
            let mut added = Vec::new();
            if let Some(conflict) = powershell_conflict(args) {
                return Err(format!(
                    "the profile's argument `{conflict}` already gives PowerShell a command or \
                     a script, so no prompt hook can be added"
                ));
            }
            if !args.iter().any(|arg| is_switch(arg, "noexit", 3)) {
                added.push("-NoExit".to_owned());
            }
            added.push("-Command".to_owned());
            added.push(powershell_script(&follow));
            Ok(Some(HookSetup {
                args: added,
                env: Vec::new(),
            }))
        }
        ShellKind::Wsl => {
            let passed = "CABINETOS_SESSION/u:CABINETOS_PIPE/u:PROMPT_COMMAND/u";
            let wslenv = match wslenv.map(str::trim).filter(|value| !value.is_empty()) {
                Some(inherited) => format!("{inherited}:{passed}"),
                None => passed.to_owned(),
            };
            Ok(Some(HookSetup {
                args: Vec::new(),
                env: vec![
                    ("PROMPT_COMMAND".to_owned(), bash_prompt_command(&follow)),
                    ("WSLENV".to_owned(), wslenv),
                ],
            }))
        }
        ShellKind::Cmd | ShellKind::Other => unreachable!("no hook: returned above"),
    }
}

/// The built-in follow step: ask `cli` for the folder to follow. Nothing
/// printed (locked, or no window): forget the folder followed last, so the
/// next link follows at once. A folder that differs from the one followed
/// last: remember it, and change to it when the shell is elsewhere. A run
/// of `cli` that failed changes nothing.
fn builtin_follow(kind: ShellKind, cli: &Path) -> String {
    let cli = cli.to_string_lossy();
    match kind {
        // Through .NET's process API rather than `& cli`: PowerShell decodes
        // a program's output by the console's code page, which would garble
        // a folder named in Cyrillic; this reads it as UTF-8, and leaves
        // $LASTEXITCODE alone.
        ShellKind::PowerShell => format!(
            "$__cabinetos_start = [Diagnostics.ProcessStartInfo]::new('{cli}', 'term cwd'); \
             $__cabinetos_start.UseShellExecute = $false; \
             $__cabinetos_start.RedirectStandardOutput = $true; \
             $__cabinetos_start.RedirectStandardError = $true; \
             $__cabinetos_start.StandardOutputEncoding = [Text.Encoding]::UTF8; \
             $__cabinetos_run = [Diagnostics.Process]::Start($__cabinetos_start); \
             $__cabinetos_folder = $__cabinetos_run.StandardOutput.ReadToEnd().TrimEnd([char]13, [char]10); \
             $__cabinetos_run.WaitForExit(); $__cabinetos_code = $__cabinetos_run.ExitCode; \
             $__cabinetos_run.Dispose(); \
             if ($__cabinetos_code -eq 0) {{ \
             if (-not $__cabinetos_folder) {{ $global:__CabinetOSFollowed = '' }} \
             elseif ($__cabinetos_folder -ne $global:__CabinetOSFollowed) {{ \
             $global:__CabinetOSFollowed = $__cabinetos_folder; \
             if ($__cabinetos_folder -ne $executionContext.SessionState.Path.CurrentLocation.ProviderPath) \
             {{ Set-Location -LiteralPath $__cabinetos_folder }} }} }}",
            cli = powershell_quoted(&cli),
        ),
        // The Windows path of the command line becomes a Linux one once, at
        // the first prompt; the pane's folder only when it is new. Its input
        // is /dev/null: WSL hands a Windows program the terminal's input,
        // and keys typed ahead of the prompt were lost to it (seen
        // 2026-10-02).
        _ => format!(
            "[ -n \"$__cabinetos_cli\" ] || __cabinetos_cli=$(wslpath -u '{cli}'); \
             if __cabinetos_folder=$(\"$__cabinetos_cli\" term cwd </dev/null); then \
             __cabinetos_folder=${{__cabinetos_folder%$'\\r'}}; \
             if [ -z \"$__cabinetos_folder\" ]; then __cabinetos_followed=; \
             elif [ \"$__cabinetos_folder\" != \"$__cabinetos_followed\" ]; then \
             __cabinetos_followed=$__cabinetos_folder; \
             __cabinetos_folder=$(wslpath -u \"$__cabinetos_folder\") && \
             [ \"$__cabinetos_folder\" != \"$PWD\" ] && builtin cd -- \"$__cabinetos_folder\"; fi; fi",
            cli = cli.replace('\'', r"'\''"),
        ),
    }
}

/// The PowerShell script after `-Command`: it keeps the `prompt` function
/// the user's profile left and wraps it. One line, single quotes only:
/// Windows PowerShell's reading of a command line mangles double quotes.
fn powershell_script(follow: &str) -> String {
    let follow = if follow.is_empty() {
        String::new()
    } else {
        format!("try {{ {follow} }} catch {{}}; ")
    };
    format!(
        "$global:__CabinetOSPrompt = $function:prompt; $global:__CabinetOSFollowed = ''; \
         function global:prompt {{ \
         $__cabinetos_ok = $global:?; $__cabinetos_exit = $global:LASTEXITCODE; \
         {follow}\
         $global:LASTEXITCODE = $__cabinetos_exit; \
         if (-not $__cabinetos_ok) {{ Write-Error '' -ErrorAction Ignore }}; \
         $__cabinetos_text = & $global:__CabinetOSPrompt; \
         $__cabinetos_report = ''; \
         try {{ $__cabinetos_here = $executionContext.SessionState.Path.CurrentLocation; \
         if ($__cabinetos_here.Provider.Name -eq 'FileSystem') \
         {{ $__cabinetos_report = [char]27 + ']9;9;' + $__cabinetos_here.ProviderPath + [char]27 + '\\' }} }} \
         catch {{}}; \
         $__cabinetos_report + $__cabinetos_text }}"
    )
}

/// bash's `PROMPT_COMMAND`: it defines the hook's function at the first
/// prompt and calls it at each, giving the prompt the last command's
/// status back. The folder report converts `$PWD` to a Windows path only
/// when the folder changed.
fn bash_prompt_command(follow: &str) -> String {
    let follow = if follow.is_empty() {
        String::new()
    } else {
        format!("{{ {follow}; }} 2>/dev/null; ")
    };
    format!(
        "__cabinetos_status=$?; \
         declare -F __cabinetos_prompt >/dev/null || __cabinetos_prompt() {{ \
         {follow}\
         if [ \"$PWD\" != \"$__cabinetos_dir\" ]; then __cabinetos_dir=$PWD; \
         __cabinetos_win=$(wslpath -w \"$PWD\" 2>/dev/null); fi; \
         [ -z \"$__cabinetos_win\" ] || printf '\\033]9;9;%s\\033\\\\' \"$__cabinetos_win\"; \
         return \"$__cabinetos_status\"; }}; \
         __cabinetos_prompt"
    )
}

/// Whether `arg` is the switch `name` (without its dash), or a prefix of it
/// at least `shortest` letters long, as PowerShell accepts one; `-`, `--`
/// and `/` all start a switch.
fn is_switch(arg: &str, name: &str, shortest: usize) -> bool {
    switch_name(arg)
        .is_some_and(|given| given.len() >= shortest.min(name.len()) && name.starts_with(&given))
}

/// The name of a switch, lower case and without its dash; `None` for a word
/// that is not a switch.
fn switch_name(arg: &str) -> Option<String> {
    let name = arg
        .strip_prefix("--")
        .or_else(|| arg.strip_prefix('-'))
        .or_else(|| arg.strip_prefix('/'))?;
    let name = name.split(':').next().unwrap_or(name);
    (!name.is_empty()).then(|| name.to_ascii_lowercase())
}

/// The first of a PowerShell profile's arguments that gives it a command or
/// a script (`-Command`, `-File`, `-EncodedCommand`, `-CommandWithArgs`, or
/// a word that is not the value of a switch, which PowerShell runs as one):
/// the hook's `-Command` cannot be added then.
fn powershell_conflict(args: &[String]) -> Option<&str> {
    // The switches that take a value: the word after them is not a script.
    const WITH_VALUE: [(&str, usize); 10] = [
        ("executionpolicy", 2),
        ("workingdirectory", 2),
        ("configurationname", 7),
        ("configurationfile", 14),
        ("custompipename", 2),
        ("settingsfile", 2),
        ("outputformat", 1),
        ("inputformat", 2),
        ("windowstyle", 1),
        ("psconsolefile", 2),
    ];
    const SHORT_WITH_VALUE: [&str; 4] = ["ep", "wd", "of", "if"];
    const COMMANDS: [&str; 4] = ["command", "file", "encodedcommand", "commandwithargs"];
    const SHORT_COMMANDS: [&str; 5] = ["c", "f", "e", "ec", "cwa"];
    let mut value_next = false;
    for arg in args {
        if value_next {
            value_next = false;
            continue;
        }
        let Some(name) = switch_name(arg) else {
            return Some(arg);
        };
        if SHORT_COMMANDS.contains(&name.as_str())
            || (name.len() >= 3 && COMMANDS.iter().any(|command| command.starts_with(&name)))
        {
            return Some(arg);
        }
        value_next = !arg.contains(':')
            && (SHORT_WITH_VALUE.contains(&name.as_str())
                || WITH_VALUE
                    .iter()
                    .any(|(switch, shortest)| is_switch(arg, switch, *shortest)));
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    fn strings(words: &[&str]) -> Vec<String> {
        words.iter().map(|word| (*word).to_owned()).collect()
    }

    fn powershell(hook: &Hook, args: &[&str]) -> Result<Option<HookSetup>, String> {
        setup(
            ShellKind::PowerShell,
            hook,
            Some(Path::new(r"C:\Apps\Cabinet OS\cabinetos-cli.exe")),
            &strings(args),
            None,
        )
    }

    #[test]
    fn powershell_gets_a_command_line_script_that_wraps_the_user_s_prompt() {
        let added = powershell(&Hook::Builtin, &["-NoLogo"]).unwrap().unwrap();
        assert_eq!(added.env, []);
        assert_eq!(&added.args[..2], ["-NoExit", "-Command"]);
        let script = &added.args[2];
        assert!(!script.contains(['"', '\n', '\r']), "{script}");
        assert!(
            script.starts_with(
                "$global:__CabinetOSPrompt = $function:prompt; $global:__CabinetOSFollowed = ''; \
                 function global:prompt {"
            ),
            "{script}"
        );
        // The folder step first, then the user's own prompt, then the report.
        let follow = script.find("'term cwd'").unwrap();
        let users = script.find("& $global:__CabinetOSPrompt").unwrap();
        let report = script.find("']9;9;'").unwrap();
        assert!(follow < users && users < report, "{script}");
        assert!(script.contains(r"::new('C:\Apps\Cabinet OS\cabinetos-cli.exe', 'term cwd')"));
        assert!(script.contains("Set-Location -LiteralPath $__cabinetos_folder"));
        // Only a folder new to the hook moves the shell, after a run that worked.
        assert!(script.contains("if ($__cabinetos_code -eq 0) {"));
        assert!(script.contains("elseif ($__cabinetos_folder -ne $global:__CabinetOSFollowed) {"));
        assert!(
            script.contains("try { $__cabinetos_start"),
            "errors are swallowed"
        );
        assert!(
            script.ends_with("$__cabinetos_report + $__cabinetos_text }"),
            "{script}"
        );
        // The script's braces match.
        let depth = script.chars().fold(0i32, |depth, c| match c {
            '{' => depth + 1,
            '}' => depth - 1,
            _ => depth,
        });
        assert_eq!(depth, 0, "{script}");
    }

    #[test]
    fn a_quote_in_the_command_line_s_path_is_doubled() {
        let added = setup(
            ShellKind::PowerShell,
            &Hook::Builtin,
            Some(Path::new(r"C:\Users\O'Neil\cabinetos-cli.exe")),
            &[],
            None,
        )
        .unwrap()
        .unwrap();
        assert!(added.args[2].contains(r"::new('C:\Users\O''Neil\cabinetos-cli.exe', 'term cwd')"));
        let bash = setup_wsl(&Hook::Builtin, Some(r"C:\Users\O'Neil\cabinetos-cli.exe"));
        assert!(
            bash.contains(r"wslpath -u 'C:\Users\O'\''Neil\cabinetos-cli.exe'"),
            "{bash}"
        );
    }

    #[test]
    fn without_the_command_line_the_hook_only_reports_and_a_custom_one_replaces_the_follow() {
        let reporting = setup(ShellKind::PowerShell, &Hook::Builtin, None, &[], None)
            .unwrap()
            .unwrap();
        assert!(!reporting.args[2].contains("term cwd"));
        assert!(!reporting.args[2].contains("try {  }"));
        assert!(reporting.args[2].contains("']9;9;'"));
        let custom = powershell(&Hook::Custom("Write-Host -NoNewline ''".to_owned()), &[])
            .unwrap()
            .unwrap();
        assert!(custom.args[2].contains("try { Write-Host -NoNewline '' } catch {}"));
        assert!(!custom.args[2].contains("term cwd"));
        assert_eq!(powershell(&Hook::Off, &["-NoLogo"]), Ok(None));
    }

    #[test]
    fn a_profile_that_already_runs_a_command_or_a_script_gets_no_hook() {
        for args in [
            &["-NoLogo", "-Command", "Get-Date"][..],
            &["-c", "x"],
            &["-NoLogo", "-File", "a.ps1"],
            &["/f", "a.ps1"],
            &["-EncodedCommand", "AAAA"],
            &["-ec", "AAAA"],
            &["-e", "AAAA"],
            &["-comm", "x"],
            &["-cwa", "x"],
            &["script.ps1"],
            &["-NoLogo", "script.ps1"],
        ] {
            let refused = powershell(&Hook::Builtin, args).unwrap_err();
            assert!(
                refused.contains("no prompt hook can be added"),
                "{args:?}: {refused}"
            );
        }
        for args in [
            &["-NoLogo", "-NoProfile"][..],
            &["-ExecutionPolicy", "Bypass"],
            &["-ep", "Bypass", "-wd", r"C:\x y"],
            &["-WorkingDirectory", "~"],
            &["-ConfigurationName", "x"],
            &["-ex", "RemoteSigned"],
            &["-ExecutionPolicy:Bypass", "-NoLogo"],
            &["-NoExit"],
        ] {
            assert!(powershell(&Hook::Builtin, args).is_ok(), "{args:?}");
        }
        // A profile that keeps the shell open already gets no second -NoExit.
        let open = powershell(&Hook::Builtin, &["-noe"]).unwrap().unwrap();
        assert_eq!(open.args[0], "-Command");
    }

    fn setup_wsl(hook: &Hook, cli: Option<&str>) -> String {
        let added = setup(ShellKind::Wsl, hook, cli.map(Path::new), &[], None)
            .unwrap()
            .unwrap();
        assert_eq!(added.args, Vec::<String>::new());
        added
            .env
            .iter()
            .find(|(name, _)| name == "PROMPT_COMMAND")
            .unwrap()
            .1
            .clone()
    }

    #[test]
    fn wsl_gets_a_prompt_command_passed_in_by_wslenv() {
        let added = setup(
            ShellKind::Wsl,
            &Hook::Builtin,
            Some(Path::new(r"E:\x\cabinetos-cli.exe")),
            &strings(&["-d", "Ubuntu"]),
            Some("USERPROFILE/p"),
        )
        .unwrap()
        .unwrap();
        assert_eq!(added.args, Vec::<String>::new(), "the profile's args stay");
        let env: std::collections::BTreeMap<_, _> = added.env.into_iter().collect();
        assert_eq!(
            env["WSLENV"],
            "USERPROFILE/p:CABINETOS_SESSION/u:CABINETOS_PIPE/u:PROMPT_COMMAND/u"
        );
        let prompt = &env["PROMPT_COMMAND"];
        assert!(!prompt.contains('\n'), "{prompt}");
        assert!(prompt.starts_with("__cabinetos_status=$?; declare -F __cabinetos_prompt >/dev/null || __cabinetos_prompt() {"));
        assert!(
            prompt.ends_with("return \"$__cabinetos_status\"; }; __cabinetos_prompt"),
            "{prompt}"
        );
        assert!(prompt.contains(r"wslpath -u 'E:\x\cabinetos-cli.exe'"));
        assert!(prompt.contains("\"$__cabinetos_cli\" term cwd"));
        assert!(prompt.contains("builtin cd -- \"$__cabinetos_folder\""));
        assert!(
            prompt.contains(
                "if __cabinetos_folder=$(\"$__cabinetos_cli\" term cwd </dev/null); then"
            )
        );
        assert!(
            prompt.contains("elif [ \"$__cabinetos_folder\" != \"$__cabinetos_followed\" ]; then")
        );
        assert!(prompt.contains(r"printf '\033]9;9;%s\033\\' "), "{prompt}");
        let follow = prompt.find("term cwd").unwrap();
        let report = prompt.find("]9;9;").unwrap();
        assert!(follow < report);
        let alone = setup_wsl(&Hook::Builtin, None);
        assert!(!alone.contains("term cwd") && alone.contains("]9;9;"));
        let own = setup_wsl(&Hook::Custom("my_follow".to_owned()), None);
        assert!(own.contains("{ my_follow; } 2>/dev/null; "), "{own}");
        let first = setup(ShellKind::Wsl, &Hook::Builtin, None, &[], Some("  "))
            .unwrap()
            .unwrap();
        assert!(first.env.contains(&(
            "WSLENV".to_owned(),
            "CABINETOS_SESSION/u:CABINETOS_PIPE/u:PROMPT_COMMAND/u".to_owned()
        )));
    }

    #[test]
    fn cmd_and_other_programs_get_no_hook() {
        for kind in [ShellKind::Cmd, ShellKind::Other] {
            assert_eq!(
                setup(kind, &Hook::Builtin, Some(Path::new("cab.exe")), &[], None),
                Ok(None)
            );
            assert_eq!(
                setup(kind, &Hook::Custom("x".to_owned()), None, &[], None),
                Ok(None)
            );
        }
    }
}
