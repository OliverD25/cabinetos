//! What differs between shells: the line that changes their folder, and how
//! a shell is started (its command line and its environment).

use std::collections::BTreeMap;
use std::ffi::{OsStr, OsString};
use std::os::windows::ffi::OsStrExt;
use std::path::Path;

/// The families of shells whose change-directory command is known, told
/// apart by the program's file name.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum ShellKind {
    /// `pwsh` (PowerShell 7) and `powershell` (Windows PowerShell 5.1).
    PowerShell,
    /// `cmd`.
    Cmd,
    /// `wsl`: a Linux shell behind WSL.
    Wsl,
    /// Anything else.
    Other,
}

impl ShellKind {
    /// The kind of shell `command` starts: `pwsh.exe`, `C:\…\pwsh.exe` and
    /// `PWSH` are all PowerShell.
    pub(crate) fn of(command: &str) -> Self {
        let name = Path::new(command)
            .file_stem()
            .map(|stem| stem.to_string_lossy().to_ascii_lowercase())
            .unwrap_or_default();
        match name.as_str() {
            "pwsh" | "powershell" => Self::PowerShell,
            "cmd" => Self::Cmd,
            "wsl" => Self::Wsl,
            _ => Self::Other,
        }
    }

    /// The line to type so the shell changes to `path`, Enter (`\r`)
    /// included. The path is quoted so the shell reads it literally.
    pub(crate) fn cd_line(self, path: &str) -> String {
        let command = match self {
            Self::PowerShell => format!("Set-Location -LiteralPath '{}'", powershell_quoted(path)),
            Self::Cmd => format!("cd /d \"{}\"", cmd_quoted(path)),
            Self::Wsl => format!("cd \"$(wslpath -a '{}')\"", path.replace('\'', r"'\''")),
            Self::Other => format!("cd \"{path}\""),
        };
        command + "\r"
    }
}

/// `path` for the inside of a PowerShell single-quoted string: every
/// single-quote character is doubled. PowerShell counts the typographic
/// quotes ‘ ’ ‚ ‛ as single quotes too, and a Windows name may contain them.
fn powershell_quoted(path: &str) -> String {
    let mut quoted = String::with_capacity(path.len() + 2);
    for c in path.chars() {
        if matches!(c, '\'' | '\u{2018}' | '\u{2019}' | '\u{201A}' | '\u{201B}') {
            quoted.push(c);
        }
        quoted.push(c);
    }
    quoted
}

/// `path` for the inside of double quotes on cmd's command line. A name
/// cannot contain `"`, but it can contain `%`, and cmd expands `%name%`
/// even inside quotes when `name` is a variable. So each `%` leaves the
/// quotes with a caret after it, `"%^x"`: the name cmd then looks up starts
/// with `^`, which no variable does, and the caret, outside the quotes,
/// only escapes the next character. cmd's `cd` drops the quotes.
fn cmd_quoted(path: &str) -> String {
    let mut quoted = String::with_capacity(path.len() + 2);
    let mut chars = path.chars();
    while let Some(c) = chars.next() {
        if c == '%' {
            quoted.push_str("\"%^");
            if let Some(next) = chars.next() {
                quoted.push(next);
            }
            quoted.push('"');
        } else {
            quoted.push(c);
        }
    }
    quoted
}

/// The command line: the program, then each argument quoted the way the
/// Microsoft C runtime splits a command line back into arguments (the same
/// rules `std::process::Command` follows).
pub(crate) fn command_line(program: &Path, args: &[String]) -> String {
    let program = program.to_string_lossy();
    let mut line = String::with_capacity(program.len() + 2);
    // A path cannot contain `"`, so quoting it needs no escapes.
    if program.contains([' ', '\t']) {
        line.push('"');
        line.push_str(&program);
        line.push('"');
    } else {
        line.push_str(&program);
    }
    for arg in args {
        line.push(' ');
        push_argument(&mut line, arg);
    }
    line
}

fn push_argument(line: &mut String, arg: &str) {
    if !arg.is_empty() && !arg.contains([' ', '\t', '\n', '\u{b}', '"']) {
        line.push_str(arg);
        return;
    }
    line.push('"');
    let mut backslashes = 0;
    for c in arg.chars() {
        match c {
            '\\' => backslashes += 1,
            '"' => {
                // Backslashes before a quote are doubled, and the quote
                // itself gets one.
                line.extend(std::iter::repeat_n('\\', backslashes * 2 + 1));
                line.push('"');
                backslashes = 0;
            }
            _ => {
                line.extend(std::iter::repeat_n('\\', backslashes));
                line.push(c);
                backslashes = 0;
            }
        }
    }
    // Backslashes before the closing quote are doubled too.
    line.extend(std::iter::repeat_n('\\', backslashes * 2));
    line.push('"');
}

/// A Unicode environment block for `CreateProcessW`: `inherited` with each
/// `(name, value)` of `set` added or replaced, as `name=value` entries
/// sorted by name without regard to case (as Windows expects), each ending
/// in NUL, with one more NUL at the end.
pub(crate) fn environment_block(
    inherited: impl IntoIterator<Item = (OsString, OsString)>,
    set: &[(&str, &str)],
) -> Vec<u16> {
    let mut variables = BTreeMap::new();
    for (name, value) in inherited {
        variables.insert(sort_key(&name), (name, value));
    }
    for (name, value) in set {
        let name = OsString::from(name);
        variables.insert(sort_key(&name), (name, OsString::from(value)));
    }
    let mut block = Vec::new();
    for (name, value) in variables.values() {
        block.extend(name.encode_wide());
        block.push(u16::from(b'='));
        block.extend(value.encode_wide());
        block.push(0);
    }
    if block.is_empty() {
        block.push(0);
    }
    block.push(0);
    block
}

/// Names compare without case: ASCII letters count as upper case.
fn sort_key(name: &OsStr) -> Vec<u16> {
    name.encode_wide()
        .map(|unit| {
            if (u16::from(b'a')..=u16::from(b'z')).contains(&unit) {
                unit - 32
            } else {
                unit
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn kinds_come_from_the_program_name() {
        assert_eq!(ShellKind::of("pwsh.exe"), ShellKind::PowerShell);
        assert_eq!(
            ShellKind::of(r"C:\Program Files\PowerShell\7\pwsh.exe"),
            ShellKind::PowerShell
        );
        assert_eq!(ShellKind::of("PowerShell"), ShellKind::PowerShell);
        assert_eq!(ShellKind::of("CMD.EXE"), ShellKind::Cmd);
        assert_eq!(ShellKind::of("wsl.exe"), ShellKind::Wsl);
        assert_eq!(ShellKind::of("nu.exe"), ShellKind::Other);
        assert_eq!(ShellKind::of(""), ShellKind::Other);
    }

    #[test]
    fn powershell_gets_a_literal_single_quoted_path() {
        assert_eq!(
            ShellKind::PowerShell.cd_line(r"E:\work"),
            "Set-Location -LiteralPath 'E:\\work'\r"
        );
        assert_eq!(
            ShellKind::PowerShell.cd_line(r"D:\it's [x] John’s $home"),
            "Set-Location -LiteralPath 'D:\\it''s [x] John’’s $home'\r"
        );
    }

    #[test]
    fn cmd_gets_a_quoted_path_with_percent_signs_taken_out_of_the_quotes() {
        assert_eq!(
            ShellKind::Cmd.cd_line(r"C:\a b & c ^ (d)"),
            "cd /d \"C:\\a b & c ^ (d)\"\r"
        );
        assert_eq!(
            ShellKind::Cmd.cd_line(r"C:\100%PATH%x"),
            "cd /d \"C:\\100\"%^P\"ATH\"%^x\"\"\r"
        );
        assert_eq!(ShellKind::Cmd.cd_line("C:\\%%"), "cd /d \"C:\\\"%^%\"\"\r");
        assert_eq!(ShellKind::Cmd.cd_line("C:\\x%"), "cd /d \"C:\\x\"%^\"\"\r");
    }

    #[test]
    fn wsl_translates_the_path_inside_linux() {
        assert_eq!(
            ShellKind::Wsl.cd_line(r"E:\it's"),
            "cd \"$(wslpath -a 'E:\\it'\\''s')\"\r"
        );
        assert_eq!(ShellKind::Other.cd_line(r"E:\x y"), "cd \"E:\\x y\"\r");
    }

    #[test]
    fn names_beyond_ascii_are_quoted_like_any_other_character() {
        let path = r"E:\Звіт 'проєкт' $HOME ’x’ 100%PATH% 日本語 📁 cafe".to_owned() + "\u{301}";
        assert_eq!(
            ShellKind::PowerShell.cd_line(&path),
            "Set-Location -LiteralPath 'E:\\Звіт ''проєкт'' $HOME ’’x’’ 100%PATH% 日本語 📁 cafe\u{301}'\r"
        );
        assert_eq!(
            ShellKind::Cmd.cd_line(&path),
            "cd /d \"E:\\Звіт 'проєкт' $HOME ’x’ 100\"%^P\"ATH\"%^ \"日本語 📁 cafe\u{301}\"\r"
        );
        assert_eq!(
            ShellKind::Wsl.cd_line(&path),
            "cd \"$(wslpath -a 'E:\\Звіт '\\''проєкт'\\'' $HOME ’x’ 100%PATH% 日本語 📁 cafe\u{301}')\"\r"
        );
    }

    #[test]
    fn arguments_are_quoted_like_the_c_runtime_expects() {
        let line = |args: &[&str]| {
            let args: Vec<String> = args.iter().map(|arg| (*arg).to_owned()).collect();
            command_line(Path::new(r"C:\Windows\System32\cmd.exe"), &args)
        };
        assert_eq!(line(&[]), r"C:\Windows\System32\cmd.exe");
        assert_eq!(
            line(&["/c", "echo", "hi", "&&", "exit", "3"]),
            r"C:\Windows\System32\cmd.exe /c echo hi && exit 3"
        );
        assert_eq!(
            line(&["", "a b", r#"say "hi""#, r"C:\dir\", r#"C:\dir\" x"#]),
            r#"C:\Windows\System32\cmd.exe "" "a b" "say \"hi\"" C:\dir\ "C:\dir\\\" x""#
        );
        assert_eq!(
            command_line(
                Path::new(r"C:\Program Files\PowerShell\7\pwsh.exe"),
                &["-NoLogo".to_owned()]
            ),
            r#""C:\Program Files\PowerShell\7\pwsh.exe" -NoLogo"#
        );
    }

    fn entries(block: &[u16]) -> Vec<String> {
        assert_eq!(&block[block.len() - 2..], &[0, 0], "ends in two NULs");
        String::from_utf16(&block[..block.len() - 2])
            .unwrap()
            .split('\0')
            .map(str::to_owned)
            .collect()
    }

    #[test]
    fn the_environment_is_sorted_without_case_and_overridden() {
        let inherited = [
            ("Path", r"C:\Windows"),
            ("=C:", r"C:\work"),
            ("term", "dumb"),
            ("ALLUSERSPROFILE", r"C:\ProgramData"),
        ]
        .map(|(name, value)| (OsString::from(name), OsString::from(value)));
        let block = environment_block(
            inherited,
            &[("TERM", "xterm-256color"), ("CABINETOS_SESSION", "7")],
        );
        assert_eq!(
            entries(&block),
            [
                r"=C:=C:\work",
                r"ALLUSERSPROFILE=C:\ProgramData",
                "CABINETOS_SESSION=7",
                r"Path=C:\Windows",
                "TERM=xterm-256color",
            ]
        );
        assert_eq!(environment_block([], &[]), [0, 0]);
    }
}
