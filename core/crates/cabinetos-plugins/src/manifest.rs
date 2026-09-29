//! `plugin.json`: who a plugin is, what it asks to do, and which commands it
//! may register. Read strictly: an unknown key or a bad value refuses the
//! plugin with a message that names the problem.

use std::path::{Path, PathBuf};

use cabinetos_commands::KeySequence;
use cabinetos_protocol::CapabilityLevel;
use serde::Deserialize;

/// The version of the plugin interface (`sdk/wit`) this core implements. A
/// plugin built against another minor version is refused.
pub const API_VERSION: &str = "0.2.0";

/// The file name of the manifest in a plugin's folder.
pub const MANIFEST_FILE: &str = "plugin.json";

/// The file name of the component in a plugin's folder.
pub const COMPONENT_FILE: &str = "plugin.wasm";

/// A plugin's `plugin.json`.
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct Manifest {
    /// Lower case letters, digits and `-`, starting with a letter; also the
    /// plugin's folder name and the prefix of its command IDs.
    pub id: String,
    /// The name people see, for example in the palette's badge.
    pub name: String,
    /// `major.minor.patch`.
    pub version: String,
    /// Who made it.
    pub author: String,
    /// What it does, in a sentence or two.
    pub description: String,
    /// The plugin interface it was built against, for example `0.1.0`.
    pub api_version: String,
    /// The oldest core it runs on.
    pub min_core_version: String,
    /// What it asks to do; each one must be granted before it runs.
    #[serde(default)]
    pub capabilities: Vec<CapabilityRequest>,
    /// The only commands it may register.
    #[serde(default)]
    pub commands: Vec<CommandDeclaration>,
}

/// A capability a plugin asks for.
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct CapabilityRequest {
    /// For example `fs:read`.
    pub name: String,
    /// For `fs:read`, `fs:write` and `fs:watch`: the folders. `%NAME%` is
    /// replaced by the environment variable, as in
    /// `%USERPROFILE%\Documents`.
    #[serde(default)]
    pub roots: Vec<String>,
    /// For `net`: the hosts it may reach, as `name` or `name:port`, such as
    /// `api.anthropic.com` or `localhost:11434`.
    #[serde(default)]
    pub hosts: Vec<String>,
    /// For `net`: the secrets (`cabinetos-cli secret set <name>`) the core
    /// may put into its requests; the plugin never sees their values.
    #[serde(default)]
    pub secrets: Vec<String>,
    /// Why, in plain words; the review dialog shows it.
    pub reason: String,
}

/// A command a plugin may register.
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct CommandDeclaration {
    /// `<plugin id>.<name>`.
    pub id: String,
    /// The title in the palette.
    pub title: String,
    /// The palette group.
    pub category: String,
    /// Keys in the keybinding syntax, such as `ctrl+alt+h`; a key the core
    /// already uses is dropped with a warning.
    #[serde(default)]
    pub default_keys: Vec<String>,
}

/// The capabilities there are.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Capability {
    /// Register the commands declared in `plugin.json`.
    CmdRegister,
    /// Read the files under some folders.
    FsRead,
    /// Read and write the files under some folders.
    FsWrite,
    /// Read the settings (`cabinetos.json`).
    ConfigRead,
    /// Send events to the connected clients.
    EventsEmit,
    /// See every job before it starts, and stop it.
    JobsIntercept,
    /// Start programs. Never granted in this version.
    ProcessRun,
    /// Reach the hosts its manifest names, through the core
    /// (`http-request`).
    Net,
    /// Read stored credentials. Never granted in this version.
    Credentials,
}

impl Capability {
    /// Every capability, in the order the review dialog lists them.
    pub const ALL: [Self; 9] = [
        Self::CmdRegister,
        Self::ConfigRead,
        Self::EventsEmit,
        Self::FsRead,
        Self::FsWrite,
        Self::JobsIntercept,
        Self::ProcessRun,
        Self::Net,
        Self::Credentials,
    ];

    /// The name in `plugin.json` and in the configuration.
    #[must_use]
    pub const fn name(self) -> &'static str {
        match self {
            Self::CmdRegister => "cmd:register",
            Self::FsRead => "fs:read",
            Self::FsWrite => "fs:write",
            Self::ConfigRead => "config:read",
            Self::EventsEmit => "events:emit",
            Self::JobsIntercept => "jobs:intercept",
            Self::ProcessRun => "process:run",
            Self::Net => "net",
            Self::Credentials => "credentials",
        }
    }

    /// The capability with this name.
    #[must_use]
    pub fn parse(name: &str) -> Option<Self> {
        Self::ALL
            .into_iter()
            .find(|capability| capability.name() == name)
    }

    /// How much it lets a plugin do.
    #[must_use]
    pub const fn level(self) -> CapabilityLevel {
        match self {
            Self::CmdRegister | Self::ConfigRead | Self::EventsEmit => CapabilityLevel::Low,
            Self::FsRead | Self::FsWrite | Self::JobsIntercept | Self::ProcessRun => {
                CapabilityLevel::Medium
            }
            Self::Net | Self::Credentials => CapabilityLevel::High,
        }
    }

    /// Whether it names folders (`roots`).
    #[must_use]
    pub const fn takes_roots(self) -> bool {
        matches!(self, Self::FsRead | Self::FsWrite)
    }

    /// Whether it names hosts (`hosts`, and optionally `secrets`).
    #[must_use]
    pub const fn takes_hosts(self) -> bool {
        matches!(self, Self::Net)
    }

    /// Whether this version refuses it whatever the user grants: the
    /// sandbox has no way to allow it safely yet.
    #[must_use]
    pub const fn never_granted(self) -> bool {
        matches!(self, Self::ProcessRun | Self::Credentials)
    }
}

/// Reads and checks `<dir>/plugin.json`. `core_version` is this core's
/// version, for `minCoreVersion`.
pub fn read(dir: &Path, core_version: &str) -> Result<Manifest, String> {
    let path = dir.join(MANIFEST_FILE);
    let text =
        std::fs::read_to_string(&path).map_err(|error| format!("{}: {error}", path.display()))?;
    let manifest: Manifest =
        serde_json::from_str(&text).map_err(|error| format!("{}: {error}", path.display()))?;
    let folder = dir
        .file_name()
        .map(|name| name.to_string_lossy().into_owned());
    check(&manifest, folder.as_deref(), core_version)
        .map_err(|problem| format!("{}: {problem}", path.display()))?;
    Ok(manifest)
}

/// The checks beyond the shape of the file.
pub fn check(manifest: &Manifest, folder: Option<&str>, core_version: &str) -> Result<(), String> {
    check_id(&manifest.id)?;
    if let Some(folder) = folder
        && folder != manifest.id
    {
        return Err(format!(
            "the id `{}` must be the folder's name, `{folder}`",
            manifest.id
        ));
    }
    for (field, value) in [
        ("name", &manifest.name),
        ("author", &manifest.author),
        ("description", &manifest.description),
    ] {
        if value.trim().is_empty() {
            return Err(format!("`{field}` is empty"));
        }
    }
    parse_version(&manifest.version)
        .ok_or_else(|| format!("version `{}` is not major.minor.patch", manifest.version))?;
    let api = parse_version(&manifest.api_version).ok_or_else(|| {
        format!(
            "apiVersion `{}` is not major.minor.patch",
            manifest.api_version
        )
    })?;
    let supported = parse_version(API_VERSION).expect("the API version is well formed");
    if (api.0, api.1) != (supported.0, supported.1) {
        return Err(format!(
            "apiVersion {} does not match this core's plugin interface {API_VERSION}",
            manifest.api_version
        ));
    }
    let needed = parse_version(&manifest.min_core_version).ok_or_else(|| {
        format!(
            "minCoreVersion `{}` is not major.minor.patch",
            manifest.min_core_version
        )
    })?;
    if parse_version(core_version).is_some_and(|core| core < needed) {
        return Err(format!(
            "it needs CabinetOS {} or newer; this is {core_version}",
            manifest.min_core_version
        ));
    }
    check_capabilities(manifest)?;
    check_commands(manifest)
}

fn check_id(id: &str) -> Result<(), String> {
    let good = !id.is_empty()
        && id.len() <= 64
        && id.starts_with(|c: char| c.is_ascii_lowercase())
        && id
            .chars()
            .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-');
    if good {
        Ok(())
    } else {
        Err(format!(
            "id `{id}` must be 1 to 64 lower case letters, digits and `-`, starting with a letter"
        ))
    }
}

fn check_capabilities(manifest: &Manifest) -> Result<(), String> {
    let mut seen = Vec::new();
    for request in &manifest.capabilities {
        let capability = Capability::parse(&request.name).ok_or_else(|| {
            let known: Vec<&str> = Capability::ALL.iter().map(|known| known.name()).collect();
            format!(
                "unknown capability `{}`; known: {}",
                request.name,
                known.join(", ")
            )
        })?;
        if seen.contains(&capability) {
            return Err(format!("capability `{}` is listed twice", request.name));
        }
        seen.push(capability);
        if request.reason.trim().is_empty() {
            return Err(format!("capability `{}` needs a reason", request.name));
        }
        match (capability.takes_roots(), request.roots.is_empty()) {
            (true, true) => {
                return Err(format!("capability `{}` needs `roots`", request.name));
            }
            (false, false) => {
                return Err(format!("capability `{}` takes no `roots`", request.name));
            }
            _ => {}
        }
        check_hosts(capability, request)?;
    }
    if !manifest.commands.is_empty() && !seen.contains(&Capability::CmdRegister) {
        return Err("it declares commands but does not ask for `cmd:register`".to_owned());
    }
    Ok(())
}

/// `hosts` and `secrets` belong to `net` alone; `net` needs at least one
/// host, each a plain host name or address with an optional port.
fn check_hosts(capability: Capability, request: &CapabilityRequest) -> Result<(), String> {
    if !capability.takes_hosts() {
        if !request.hosts.is_empty() || !request.secrets.is_empty() {
            return Err(format!(
                "capability `{}` takes no `hosts` or `secrets`",
                request.name
            ));
        }
        return Ok(());
    }
    if request.hosts.is_empty() {
        return Err(format!("capability `{}` needs `hosts`", request.name));
    }
    for host in &request.hosts {
        HostRule::parse(host)?;
    }
    for secret in &request.secrets {
        let good = !secret.is_empty()
            && secret.len() <= 128
            && secret
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_' | '.'));
        if !good {
            return Err(format!(
                "`{secret}` is not a secret name: use letters, digits, `-`, `_` and `.`"
            ));
        }
    }
    Ok(())
}

/// A host a plugin may reach: a name or an address, and a port. Without a
/// port it matches only the scheme's own port (443 for `https`, 80 for
/// `http`).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HostRule {
    /// Lower case.
    pub host: String,
    /// `None`: the scheme's own port.
    pub port: Option<u16>,
}

impl HostRule {
    /// Reads `name` or `name:port`; no scheme, path, user or wildcard.
    pub fn parse(text: &str) -> Result<Self, String> {
        let bad = || {
            format!(
                "host `{text}` must be a host name or address, with an optional `:port`, such as `api.example.com` or `localhost:11434`"
            )
        };
        let (host, port) = match text.rsplit_once(':') {
            Some((host, port)) => (host, Some(port.parse::<u16>().map_err(|_| bad())?)),
            None => (text, None),
        };
        let good = !host.is_empty()
            && host.len() <= 253
            && host
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '.'));
        if !good || port == Some(0) {
            return Err(bad());
        }
        Ok(Self {
            host: host.to_ascii_lowercase(),
            port,
        })
    }

    /// Whether a URL's host and port (the scheme's own when it names none)
    /// fall under this rule.
    #[must_use]
    pub fn matches(&self, host: &str, port: u16, default_port: u16) -> bool {
        self.host.eq_ignore_ascii_case(host) && self.port.unwrap_or(default_port) == port
    }
}

fn check_commands(manifest: &Manifest) -> Result<(), String> {
    let prefix = format!("{}.", manifest.id);
    let mut seen: Vec<&str> = Vec::new();
    for command in &manifest.commands {
        let rest = command.id.strip_prefix(&prefix).unwrap_or_default();
        if rest.is_empty()
            || !rest
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || "._-".contains(c))
        {
            return Err(format!(
                "command id `{}` must be `{prefix}` followed by letters, digits, `.`, `_` or `-`",
                command.id
            ));
        }
        if seen.contains(&command.id.as_str()) {
            return Err(format!("command `{}` is declared twice", command.id));
        }
        seen.push(&command.id);
        if command.title.trim().is_empty() || command.category.trim().is_empty() {
            return Err(format!(
                "command `{}` needs a title and a category",
                command.id
            ));
        }
        for keys in &command.default_keys {
            keys.parse::<KeySequence>()
                .map_err(|error| format!("command `{}`: {error}", command.id))?;
        }
    }
    Ok(())
}

/// `major.minor.patch`, numbers only.
fn parse_version(text: &str) -> Option<(u32, u32, u32)> {
    let mut parts = text.split('.');
    let version = (
        parts.next()?.parse().ok()?,
        parts.next()?.parse().ok()?,
        parts.next()?.parse().ok()?,
    );
    parts.next().is_none().then_some(version)
}

/// `root` with `%NAME%` replaced by environment variables; it must then be
/// an absolute path.
pub fn expand_root(root: &str) -> Result<PathBuf, String> {
    let mut expanded = String::new();
    let mut rest = root;
    while let Some(start) = rest.find('%') {
        expanded.push_str(&rest[..start]);
        let after = &rest[start + 1..];
        let end = after
            .find('%')
            .ok_or_else(|| format!("root `{root}` has an unclosed `%`"))?;
        let name = &after[..end];
        let value = std::env::var(name)
            .map_err(|_| format!("root `{root}` uses %{name}%, which is not set"))?;
        expanded.push_str(&value);
        rest = &after[end + 1..];
    }
    expanded.push_str(rest);
    let path = PathBuf::from(&expanded);
    if path.is_absolute() {
        Ok(path)
    } else {
        Err(format!("root `{root}` is not an absolute path"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn manifest() -> Manifest {
        serde_json::from_str(
            r#"{
                "id": "hello",
                "name": "Hello",
                "version": "1.2.3",
                "author": "Someone",
                "description": "Says hello.",
                "apiVersion": "0.2.0",
                "minCoreVersion": "0.1.0",
                "capabilities": [
                    {"name": "cmd:register", "reason": "Adds a command."},
                    {"name": "fs:read", "roots": ["C:\\data"], "reason": "Reads."}
                ],
                "commands": [
                    {"id": "hello.say", "title": "Say Hello", "category": "Hello", "defaultKeys": ["ctrl+alt+h"]}
                ]
            }"#,
        )
        .unwrap()
    }

    fn problem(change: impl FnOnce(&mut Manifest)) -> String {
        let mut manifest = manifest();
        change(&mut manifest);
        check(&manifest, Some(&manifest.id.clone()), "0.1.0").unwrap_err()
    }

    #[test]
    fn a_good_manifest_passes() {
        check(&manifest(), Some("hello"), "0.1.0").unwrap();
    }

    #[test]
    fn unknown_keys_and_missing_fields_are_refused() {
        let unknown = serde_json::from_str::<Manifest>(
            r#"{"id":"a","name":"A","version":"1.0.0","author":"x","description":"d","apiVersion":"0.2.0","minCoreVersion":"0.1.0","colour":"red"}"#,
        )
        .unwrap_err();
        assert!(unknown.to_string().contains("colour"), "{unknown}");
        let missing = serde_json::from_str::<Manifest>(r#"{"id":"a","name":"A"}"#).unwrap_err();
        assert!(missing.to_string().contains("missing field"), "{missing}");
    }

    #[test]
    fn bad_values_name_the_problem() {
        assert!(problem(|m| m.id = "Hello".to_owned()).contains("lower case"));
        assert!(
            check(&manifest(), Some("other"), "0.1.0")
                .unwrap_err()
                .contains("folder")
        );
        assert!(problem(|m| m.version = "1.2".to_owned()).contains("major.minor.patch"));
        assert!(problem(|m| m.api_version = "0.1.0".to_owned()).contains("apiVersion"));
        assert!(problem(|m| m.min_core_version = "9.0.0".to_owned()).contains("9.0.0 or newer"));
        assert!(
            problem(|m| m.capabilities[0].name = "fs:everything".to_owned())
                .contains("unknown capability")
        );
        assert!(problem(|m| m.capabilities[1].roots.clear()).contains("needs `roots`"));
        assert!(
            problem(|m| m.capabilities[0].roots = vec!["C:\\".to_owned()])
                .contains("takes no `roots`")
        );
        assert!(problem(|m| m.capabilities[0].reason = " ".to_owned()).contains("reason"));
        assert!(
            problem(|m| m.commands[0].id = "other.say".to_owned()).contains("must be `hello.`")
        );
        assert!(
            problem(|m| m.commands[0].default_keys = vec!["ctrl+nope".to_owned()]).contains("nope")
        );
        assert!(
            problem(|m| {
                m.capabilities.remove(0);
            })
            .contains("cmd:register")
        );
    }

    #[test]
    fn capabilities_have_names_and_levels() {
        for capability in Capability::ALL {
            assert_eq!(Capability::parse(capability.name()), Some(capability));
        }
        assert_eq!(Capability::FsRead.level(), CapabilityLevel::Medium);
        assert_eq!(Capability::Net.level(), CapabilityLevel::High);
        assert!(Capability::ProcessRun.never_granted());
        assert!(!Capability::FsWrite.never_granted());
        assert!(!Capability::Net.never_granted(), "net is granted since 0.2");
    }

    #[test]
    fn net_names_its_hosts_and_its_secrets() {
        let with_net = |hosts: &[&str], secrets: &[&str]| {
            let mut manifest = manifest();
            manifest.capabilities.push(CapabilityRequest {
                name: "net".to_owned(),
                roots: Vec::new(),
                hosts: hosts.iter().map(|host| (*host).to_owned()).collect(),
                secrets: secrets.iter().map(|secret| (*secret).to_owned()).collect(),
                reason: "Asks a model.".to_owned(),
            });
            check(&manifest, Some("hello"), "0.1.0")
        };
        with_net(&["api.anthropic.com", "localhost:11434"], &["anthropic"]).unwrap();
        assert!(with_net(&[], &[]).unwrap_err().contains("needs `hosts`"));
        for bad in [
            "https://api.anthropic.com",
            "*.example.com",
            "host:99999",
            "host:0",
            "a/b",
        ] {
            assert!(
                with_net(&[bad], &[]).unwrap_err().contains("host `"),
                "{bad}"
            );
        }
        assert!(
            with_net(&["localhost"], &["a/b"])
                .unwrap_err()
                .contains("secret name")
        );
        assert!(
            problem(|m| m.capabilities[0].hosts = vec!["x.org".to_owned()])
                .contains("takes no `hosts`")
        );
        let rule = HostRule::parse("LocalHost:11434").unwrap();
        assert!(rule.matches("localhost", 11434, 80));
        assert!(!rule.matches("localhost", 80, 80));
        let rule = HostRule::parse("api.anthropic.com").unwrap();
        assert!(rule.matches("API.anthropic.com", 443, 443));
        assert!(!rule.matches("api.anthropic.com", 8443, 443));
        assert!(!rule.matches("evil.anthropic.com", 443, 443));
    }

    #[test]
    fn roots_expand_environment_variables() {
        let temp = std::env::var("TEMP").unwrap();
        assert_eq!(
            expand_root(r"%TEMP%\cabinetos").unwrap(),
            PathBuf::from(format!(r"{temp}\cabinetos"))
        );
        assert!(expand_root(r"%CABINETOS_NO_SUCH_VARIABLE%\x").is_err());
        assert!(expand_root("relative").is_err());
        assert!(expand_root("%TEMP").is_err());
    }
}
