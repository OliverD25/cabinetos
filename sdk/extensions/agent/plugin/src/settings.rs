//! The settings under `plugins.agent.settings` in `cabinetos.json`, read
//! through `config-get`:
//!
//! ```json
//! { "provider": "anthropic", "model": "claude-sonnet-5-5", "base": "http://localhost:11434",
//!   "maxTokens": 4096, "tier": 2, "rules": [{ "folder": "C:\\Inbox", "rule": "sort by year" }],
//!   "fakeReplies": "fake-replies.json" }
//! ```

use serde_json::Value;

use crate::paths;
use crate::tier::Tier;

/// The Anthropic model used when `model` is not set.
pub const DEFAULT_ANTHROPIC_MODEL: &str = "claude-sonnet-5-5";
/// The model an OpenAI-compatible server is asked for when `model` is not
/// set: a common local one.
pub const DEFAULT_OPENAI_MODEL: &str = "llama3.1";
/// Where an OpenAI-compatible server is looked for when `base` is not set:
/// Ollama on this computer.
pub const DEFAULT_OPENAI_BASE: &str = "http://localhost:11434";
/// The reply length asked for when `maxTokens` is not set.
pub const DEFAULT_MAX_TOKENS: u32 = 4096;
/// The most `maxTokens` accepts.
pub const MAX_MAX_TOKENS: u32 = 64_000;

/// Which service answers.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProviderKind {
    /// `POST https://api.anthropic.com/v1/messages`.
    Anthropic,
    /// `POST <base>/v1/chat/completions`: OpenAI, Ollama, LM Studio and
    /// everything that speaks their form.
    OpenAi,
    /// Canned replies from a file, for tests.
    Fake,
}

impl ProviderKind {
    /// The name in the settings and in the audit log.
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            Self::Anthropic => "anthropic",
            Self::OpenAi => "openai",
            Self::Fake => "fake",
        }
    }
}

/// A watch folder: what to do with each new file in it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Rule {
    /// An absolute Windows path.
    pub folder: String,
    /// What the user wants, in words; the model is given it with each file.
    pub rule: String,
}

/// The agent's settings, with the defaults filled in.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Settings {
    pub provider: ProviderKind,
    pub model: String,
    pub base: String,
    pub max_tokens: u32,
    pub tier: Tier,
    pub rules: Vec<Rule>,
    /// A file of canned replies, for the fake provider (tests only);
    /// `fake-replies.json` when the setting is left out.
    pub fake_replies: Option<String>,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            provider: ProviderKind::Anthropic,
            model: DEFAULT_ANTHROPIC_MODEL.to_owned(),
            base: DEFAULT_OPENAI_BASE.to_owned(),
            max_tokens: DEFAULT_MAX_TOKENS,
            tier: Tier::Diff,
            rules: Vec::new(),
            fake_replies: None,
        }
    }
}

impl Settings {
    /// Reads the settings object. A key that is not there has its default;
    /// a key with a wrong value is an error that names it, so a typo does
    /// not silently change what the agent does.
    pub fn from_value(value: &Value) -> Result<Self, String> {
        let Some(object) = value.as_object() else {
            return Err("plugins.agent.settings must be an object".to_owned());
        };
        let text = |key: &str| -> Result<Option<String>, String> {
            match object.get(key) {
                None | Some(Value::Null) => Ok(None),
                Some(Value::String(text)) if !text.trim().is_empty() => {
                    Ok(Some(text.trim().to_owned()))
                }
                Some(_) => Err(format!("settings.{key} must be a text that is not empty")),
            }
        };
        let provider = match text("provider")?.as_deref() {
            None | Some("anthropic") => ProviderKind::Anthropic,
            Some("openai") => ProviderKind::OpenAi,
            Some("fake") => ProviderKind::Fake,
            Some(other) => {
                return Err(format!(
                    "settings.provider is `{other}`; it must be anthropic, openai or fake"
                ));
            }
        };
        let model = text("model")?.unwrap_or_else(|| {
            match provider {
                ProviderKind::OpenAi => DEFAULT_OPENAI_MODEL,
                _ => DEFAULT_ANTHROPIC_MODEL,
            }
            .to_owned()
        });
        let base = text("base")?.unwrap_or_else(|| DEFAULT_OPENAI_BASE.to_owned());
        if !(base.starts_with("http://") || base.starts_with("https://")) {
            return Err(format!(
                "settings.base is `{base}`; it must start with http:// or https://"
            ));
        }
        let max_tokens = match object.get("maxTokens") {
            None | Some(Value::Null) => DEFAULT_MAX_TOKENS,
            Some(value) => value
                .as_u64()
                .and_then(|number| u32::try_from(number).ok())
                .filter(|number| (1..=MAX_MAX_TOKENS).contains(number))
                .ok_or_else(|| {
                    format!("settings.maxTokens must be a whole number from 1 to {MAX_MAX_TOKENS}")
                })?,
        };
        let tier = match object.get("tier") {
            None | Some(Value::Null) => Tier::Diff,
            Some(value) => value
                .as_u64()
                .and_then(Tier::from_number)
                .ok_or("settings.tier must be 1, 2 or 3")?,
        };
        Ok(Self {
            provider,
            model,
            base: base.trim_end_matches('/').to_owned(),
            max_tokens,
            tier,
            rules: rules_of(object.get("rules"))?,
            fake_replies: text("fakeReplies")?,
        })
    }
}

/// `rules`: a list of `{ "folder", "rule" }`.
pub fn rules_of(value: Option<&Value>) -> Result<Vec<Rule>, String> {
    let list = match value {
        None | Some(Value::Null) => return Ok(Vec::new()),
        Some(Value::Array(list)) => list,
        Some(_) => return Err("settings.rules must be a list".to_owned()),
    };
    let mut rules: Vec<Rule> = Vec::new();
    for (index, entry) in list.iter().enumerate() {
        let field = |key: &str| {
            entry
                .get(key)
                .and_then(Value::as_str)
                .map(str::trim)
                .filter(|text| !text.is_empty())
        };
        let (Some(folder), Some(rule)) = (field("folder"), field("rule")) else {
            return Err(format!(
                "settings.rules[{index}] needs a `folder` and a `rule`, both text"
            ));
        };
        if !paths::is_absolute(folder) || paths::has_dot_parts(folder) {
            return Err(format!(
                "settings.rules[{index}].folder must be an absolute Windows path such as C:\\Inbox"
            ));
        }
        let folder = paths::normalize(folder);
        if rules
            .iter()
            .any(|known| known.folder.eq_ignore_ascii_case(&folder))
        {
            return Err(format!(
                "settings.rules[{index}]: the folder {folder} has a rule already; one rule per folder"
            ));
        }
        rules.push(Rule {
            folder,
            rule: rule.to_owned(),
        });
    }
    Ok(rules)
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    #[test]
    fn nothing_set_is_the_defaults() {
        let settings = Settings::from_value(&json!({})).unwrap();
        assert_eq!(settings, Settings::default());
        assert_eq!(settings.provider, ProviderKind::Anthropic);
        assert_eq!(settings.model, "claude-sonnet-5-5");
        assert_eq!(settings.max_tokens, 4096);
        assert_eq!(settings.tier, Tier::Diff, "tier 3 is never the default");
    }

    #[test]
    fn the_openai_defaults_are_ollama_on_this_computer() {
        let settings = Settings::from_value(&json!({ "provider": "openai" })).unwrap();
        assert_eq!(settings.provider, ProviderKind::OpenAi);
        assert_eq!(settings.model, "llama3.1");
        assert_eq!(settings.base, "http://localhost:11434");
        let set = Settings::from_value(&json!({
            "provider": "openai", "model": "gpt-x", "base": "http://localhost:1234/", "maxTokens": 300
        }))
        .unwrap();
        assert_eq!(
            (set.model.as_str(), set.base.as_str(), set.max_tokens),
            ("gpt-x", "http://localhost:1234", 300)
        );
    }

    #[test]
    fn every_key_is_read() {
        let settings = Settings::from_value(&json!({
            "provider": "fake", "tier": 3, "fakeReplies": "replies.json",
            "rules": [{ "folder": "C:/Inbox/", "rule": "sort by year" }]
        }))
        .unwrap();
        assert_eq!(settings.provider, ProviderKind::Fake);
        assert_eq!(settings.tier, Tier::Autonomous);
        assert_eq!(settings.fake_replies.as_deref(), Some("replies.json"));
        assert_eq!(
            settings.rules,
            [Rule {
                folder: r"C:\Inbox".to_owned(),
                rule: "sort by year".to_owned()
            }]
        );
    }

    #[test]
    fn a_wrong_value_names_its_key() {
        for (settings, key) in [
            (json!(5), "settings must be an object"),
            (
                json!({ "provider": "gemini" }),
                "settings.provider is `gemini`",
            ),
            (json!({ "provider": 3 }), "settings.provider must be a text"),
            (json!({ "tier": 4 }), "settings.tier"),
            (json!({ "tier": "2" }), "settings.tier"),
            (json!({ "maxTokens": 0 }), "settings.maxTokens"),
            (json!({ "maxTokens": 1_000_000 }), "settings.maxTokens"),
            (json!({ "base": "ftp://x" }), "settings.base"),
            (json!({ "rules": "x" }), "settings.rules must be a list"),
            (
                json!({ "rules": [{ "folder": "C:\\x" }] }),
                "settings.rules[0] needs",
            ),
            (
                json!({ "rules": [{ "folder": "x", "rule": "y" }] }),
                "settings.rules[0].folder",
            ),
            (
                json!({ "rules": [{ "folder": "C:\\a", "rule": "y" }, { "folder": "c:/a", "rule": "z" }] }),
                "one rule per folder",
            ),
        ] {
            let error = Settings::from_value(&settings).unwrap_err();
            assert!(error.contains(key), "{settings}: {error}");
        }
    }
}
