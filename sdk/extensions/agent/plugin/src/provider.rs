//! The model providers, behind one trait. Every one reaches its service
//! through the core's `http-request`, and none holds a key: the core adds
//! the stored secret to the header, so the plugin never sees it.
//!
//! - `anthropic`: `POST https://api.anthropic.com/v1/messages`, the secret
//!   `anthropic` in `x-api-key`.
//! - `openai`: `POST <base>/v1/chat/completions`, for OpenAI and every
//!   server that speaks its form (Ollama, LM Studio); the secret `openai`
//!   in `Authorization` unless the server is on this computer, which needs
//!   no key.
//! - `fake`: canned replies from a file in the plugin's own folder, for
//!   tests. It calls nothing.

use std::cell::Cell;
use std::rc::Rc;

use serde_json::{Value, json};

use crate::host::{Host, HttpRequest};
use crate::settings::{ProviderKind, Settings};

/// The URL of Anthropic's messages endpoint.
pub const ANTHROPIC_URL: &str = "https://api.anthropic.com/v1/messages";
/// The API version header Anthropic asks for.
pub const ANTHROPIC_VERSION: &str = "2023-06-01";
/// The file of canned replies the fake provider reads when `fakeReplies`
/// is not set.
pub const DEFAULT_FAKE_REPLIES: &str = "fake-replies.json";

/// Who wrote a message.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Role {
    User,
    Assistant,
}

impl Role {
    fn word(self) -> &'static str {
        match self {
            Self::User => "user",
            Self::Assistant => "assistant",
        }
    }
}

/// One message of the conversation.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Message {
    pub role: Role,
    pub content: String,
}

impl Message {
    #[must_use]
    pub fn user(content: impl Into<String>) -> Self {
        Self {
            role: Role::User,
            content: content.into(),
        }
    }

    #[must_use]
    pub fn assistant(content: impl Into<String>) -> Self {
        Self {
            role: Role::Assistant,
            content: content.into(),
        }
    }
}

/// What one model call asks: the system prompt, and the conversation so
/// far (it starts with the user and alternates).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Completion {
    pub system: String,
    pub messages: Vec<Message>,
}

/// A model service.
pub trait Provider {
    /// The name in the settings and the audit log.
    fn name(&self) -> &'static str;
    /// The model it asks.
    fn model(&self) -> &str;
    /// Asks the model and returns its text.
    fn complete(&self, host: &dyn Host, request: &Completion) -> Result<String, String>;
}

/// The provider the settings name. `next_fake` counts the replies the fake
/// has given, so a second call gets the next one.
pub fn make(
    settings: &Settings,
    host: &dyn Host,
    next_fake: &Rc<Cell<usize>>,
) -> Result<Box<dyn Provider>, String> {
    Ok(match settings.provider {
        ProviderKind::Anthropic => Box::new(Anthropic {
            model: settings.model.clone(),
            max_tokens: settings.max_tokens,
        }),
        ProviderKind::OpenAi => Box::new(OpenAi {
            model: settings.model.clone(),
            max_tokens: settings.max_tokens,
            base: settings.base.clone(),
        }),
        ProviderKind::Fake => {
            let file = settings
                .fake_replies
                .as_deref()
                .unwrap_or(DEFAULT_FAKE_REPLIES);
            Box::new(Fake::load(
                host,
                file,
                settings.model.clone(),
                Rc::clone(next_fake),
            )?)
        }
    })
}

// ----- Anthropic -----

/// The Anthropic Messages API.
pub struct Anthropic {
    pub model: String,
    pub max_tokens: u32,
}

/// The body of a messages request.
#[must_use]
pub fn anthropic_body(model: &str, max_tokens: u32, request: &Completion) -> Value {
    json!({
        "model": model,
        "max_tokens": max_tokens,
        "system": request.system,
        "messages": request
            .messages
            .iter()
            .map(|message| json!({ "role": message.role.word(), "content": message.content }))
            .collect::<Vec<_>>(),
    })
}

/// The whole request: the key goes in as the secret `anthropic`, which the
/// core puts into `x-api-key`.
#[must_use]
pub fn anthropic_request(model: &str, max_tokens: u32, request: &Completion) -> HttpRequest {
    HttpRequest {
        method: "POST",
        url: ANTHROPIC_URL.to_owned(),
        headers: vec![
            ("anthropic-version".to_owned(), ANTHROPIC_VERSION.to_owned()),
            ("content-type".to_owned(), "application/json".to_owned()),
        ],
        body: anthropic_body(model, max_tokens, request)
            .to_string()
            .into_bytes(),
        secret: Some("anthropic".to_owned()),
        secret_header: Some("x-api-key".to_owned()),
        timeout_ms: 0,
    }
}

/// The text of an answer: the `text` blocks joined, or the reason it is an
/// error.
pub fn anthropic_text(status: u16, body: &[u8]) -> Result<String, String> {
    let value: Value = serde_json::from_slice(body).map_err(|error| {
        format!("Anthropic answered {status} with text that is not JSON: {error}")
    })?;
    if !(200..300).contains(&status) {
        return Err(service_error("Anthropic", status, &value));
    }
    let text: String = value["content"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|block| block["type"] == "text")
        .filter_map(|block| block["text"].as_str())
        .collect();
    if text.trim().is_empty() {
        return Err("Anthropic answered with no text".to_owned());
    }
    Ok(text)
}

impl Provider for Anthropic {
    fn name(&self) -> &'static str {
        "anthropic"
    }

    fn model(&self) -> &str {
        &self.model
    }

    fn complete(&self, host: &dyn Host, request: &Completion) -> Result<String, String> {
        let answer = host.http(&anthropic_request(&self.model, self.max_tokens, request))?;
        anthropic_text(answer.status, &answer.body)
    }
}

// ----- OpenAI-compatible -----

/// An OpenAI-compatible chat completions endpoint.
pub struct OpenAi {
    pub model: String,
    pub max_tokens: u32,
    /// Without a trailing slash, for example `http://localhost:11434`.
    pub base: String,
}

/// Whether a server is on this computer: it needs no key.
#[must_use]
pub fn is_local(base: &str) -> bool {
    let rest = base.split_once("://").map_or(base, |(_, rest)| rest);
    let host = rest.split(['/', ':']).next().unwrap_or_default();
    host.eq_ignore_ascii_case("localhost") || host == "127.0.0.1"
}

/// The body of a chat completions request.
#[must_use]
pub fn openai_body(model: &str, max_tokens: u32, request: &Completion) -> Value {
    let mut messages = vec![json!({ "role": "system", "content": request.system })];
    messages.extend(
        request
            .messages
            .iter()
            .map(|message| json!({ "role": message.role.word(), "content": message.content })),
    );
    json!({
        "model": model,
        "messages": messages,
        "max_tokens": max_tokens,
        "stream": false,
    })
}

/// The whole request. The key is the secret `openai` in `Authorization`,
/// asked for only when the server is not on this computer.
#[must_use]
pub fn openai_request(
    base: &str,
    model: &str,
    max_tokens: u32,
    request: &Completion,
) -> HttpRequest {
    let (secret, secret_header) = if is_local(base) {
        (None, None)
    } else {
        (Some("openai".to_owned()), Some("Authorization".to_owned()))
    };
    HttpRequest {
        method: "POST",
        url: format!("{base}/v1/chat/completions"),
        headers: vec![("content-type".to_owned(), "application/json".to_owned())],
        body: openai_body(model, max_tokens, request)
            .to_string()
            .into_bytes(),
        secret,
        secret_header,
        timeout_ms: 0,
    }
}

/// The text of an answer, or the reason it is an error.
pub fn openai_text(status: u16, body: &[u8]) -> Result<String, String> {
    let value: Value = serde_json::from_slice(body).map_err(|error| {
        format!("the server answered {status} with text that is not JSON: {error}")
    })?;
    if !(200..300).contains(&status) {
        return Err(service_error("The server", status, &value));
    }
    let text = value["choices"][0]["message"]["content"]
        .as_str()
        .unwrap_or_default();
    if text.trim().is_empty() {
        return Err("the server answered with no text".to_owned());
    }
    Ok(text.to_owned())
}

impl Provider for OpenAi {
    fn name(&self) -> &'static str {
        "openai"
    }

    fn model(&self) -> &str {
        &self.model
    }

    fn complete(&self, host: &dyn Host, request: &Completion) -> Result<String, String> {
        let answer = host.http(&openai_request(
            &self.base,
            &self.model,
            self.max_tokens,
            request,
        ))?;
        openai_text(answer.status, &answer.body)
    }
}

/// The message of a service's error answer, as one line.
fn service_error(who: &str, status: u16, value: &Value) -> String {
    let message = value["error"]["message"]
        .as_str()
        .or_else(|| value["error"].as_str())
        .or_else(|| value["message"].as_str())
        .unwrap_or("no reason given");
    let message: String = message.chars().take(300).collect();
    let hint = match status {
        401 | 403 => " (is the key stored? cabinetos-cli secret set <name>)",
        _ => "",
    };
    format!("{who} answered {status}: {message}{hint}")
}

// ----- The fake, for tests -----

/// Canned replies, one for each call in order, from a file in the plugin's
/// own folder: a JSON list of texts, or `{ "replies": [...] }`. Each
/// request it gets is appended to `fake-requests.jsonl` there, so a test
/// can see what the agent sent.
pub struct Fake {
    replies: Vec<String>,
    next: Rc<Cell<usize>>,
    model: String,
}

impl Fake {
    /// Reads the replies file.
    pub fn load(
        host: &dyn Host,
        file: &str,
        model: String,
        next: Rc<Cell<usize>>,
    ) -> Result<Self, String> {
        if file.contains(['\\', '/', ':']) {
            return Err(format!(
                "settings.fakeReplies is `{file}`; it names a file in the plugin's own folder, without a path"
            ));
        }
        let bytes = host.read_data(file)?.ok_or_else(|| {
            format!("the fake provider's file {file} is not in the plugin's folder")
        })?;
        let value: Value = serde_json::from_slice(&bytes)
            .map_err(|error| format!("{file} is not JSON: {error}"))?;
        let list = value
            .get("replies")
            .unwrap_or(&value)
            .as_array()
            .ok_or_else(|| format!("{file} must be a list of texts"))?;
        let replies = list
            .iter()
            .map(|reply| {
                reply
                    .as_str()
                    .map(str::to_owned)
                    .ok_or_else(|| format!("{file} must be a list of texts"))
            })
            .collect::<Result<_, _>>()?;
        Ok(Self {
            replies,
            next,
            model,
        })
    }
}

impl Provider for Fake {
    fn name(&self) -> &'static str {
        "fake"
    }

    fn model(&self) -> &str {
        &self.model
    }

    fn complete(&self, host: &dyn Host, request: &Completion) -> Result<String, String> {
        let asked = json!({ "system": request.system, "messages": request
            .messages
            .iter()
            .map(|message| json!({ "role": message.role.word(), "content": message.content }))
            .collect::<Vec<_>>() });
        host.append_data("fake-requests.jsonl", format!("{asked}\n").as_bytes())?;
        let index = self.next.get();
        let reply = self.replies.get(index).ok_or_else(|| {
            format!(
                "the fake provider has no more replies: it was asked {} times and the file has {}",
                index + 1,
                self.replies.len()
            )
        })?;
        self.next.set(index + 1);
        Ok(reply.clone())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn completion() -> Completion {
        Completion {
            system: "You are an agent.".to_owned(),
            messages: vec![
                Message::user("rename these"),
                Message::assistant("```\nls C:\\x\n```"),
                Message::user("output"),
            ],
        }
    }

    #[test]
    fn the_anthropic_request_is_what_the_service_asks_for() {
        let request = anthropic_request("claude-sonnet-5-5", 4096, &completion());
        assert_eq!(
            (request.method, request.url.as_str()),
            ("POST", "https://api.anthropic.com/v1/messages")
        );
        assert!(
            request
                .headers
                .contains(&("anthropic-version".to_owned(), "2023-06-01".to_owned()))
        );
        assert!(
            request
                .headers
                .contains(&("content-type".to_owned(), "application/json".to_owned()))
        );
        // The key is the core's to add: the plugin only names it.
        assert_eq!(request.secret.as_deref(), Some("anthropic"));
        assert_eq!(request.secret_header.as_deref(), Some("x-api-key"));
        assert!(
            request
                .headers
                .iter()
                .all(|(name, _)| !name.eq_ignore_ascii_case("x-api-key"))
        );
        let body: Value = serde_json::from_slice(&request.body).unwrap();
        assert_eq!(
            body,
            json!({
                "model": "claude-sonnet-5-5",
                "max_tokens": 4096,
                "system": "You are an agent.",
                "messages": [
                    { "role": "user", "content": "rename these" },
                    { "role": "assistant", "content": "```\nls C:\\x\n```" },
                    { "role": "user", "content": "output" },
                ],
            })
        );
    }

    #[test]
    fn the_openai_request_puts_the_system_prompt_first_and_asks_a_key_only_of_a_remote_server() {
        let local = openai_request("http://localhost:11434", "llama3.1", 300, &completion());
        assert_eq!(local.url, "http://localhost:11434/v1/chat/completions");
        assert_eq!(
            (local.secret.as_deref(), local.secret_header.as_deref()),
            (None, None)
        );
        let body: Value = serde_json::from_slice(&local.body).unwrap();
        assert_eq!(body["model"], "llama3.1");
        assert_eq!(body["max_tokens"], 300);
        assert_eq!(body["stream"], false);
        assert_eq!(
            body["messages"][0],
            json!({ "role": "system", "content": "You are an agent." })
        );
        assert_eq!(
            body["messages"][1],
            json!({ "role": "user", "content": "rename these" })
        );
        assert_eq!(body["messages"].as_array().unwrap().len(), 4);

        let remote = openai_request("https://api.openai.com", "gpt", 300, &completion());
        assert_eq!(remote.url, "https://api.openai.com/v1/chat/completions");
        assert_eq!(remote.secret.as_deref(), Some("openai"));
        assert_eq!(remote.secret_header.as_deref(), Some("Authorization"));
        assert!(is_local("http://127.0.0.1:1234"));
        assert!(is_local("http://LOCALHOST/x"));
        assert!(!is_local("https://localhost.evil.com"));
    }

    #[test]
    fn the_fake_reads_fake_replies_json_unless_the_settings_name_another_file() {
        let host = crate::testing::FakeHost::new();
        host.put_file("fake-replies.json", r#"["first", "second"]"#);
        host.put_file("other.json", r#"{"replies": ["from other"]}"#);
        let next = Rc::new(Cell::new(0));
        let plain = Settings::from_value(&json!({ "provider": "fake" })).unwrap();
        let fake = make(&plain, &host, &next).unwrap();
        assert_eq!(fake.complete(&host, &completion()).unwrap(), "first");
        assert_eq!(fake.complete(&host, &completion()).unwrap(), "second");
        assert!(
            fake.complete(&host, &completion())
                .unwrap_err()
                .contains("no more replies")
        );
        let named =
            Settings::from_value(&json!({ "provider": "fake", "fakeReplies": "other.json" }))
                .unwrap();
        let fake = make(&named, &host, &Rc::new(Cell::new(0))).unwrap();
        assert_eq!(fake.complete(&host, &completion()).unwrap(), "from other");
        let with_path =
            Settings::from_value(&json!({ "provider": "fake", "fakeReplies": "C:/x/other.json" }))
                .unwrap();
        assert!(
            make(&with_path, &host, &next)
                .err()
                .unwrap()
                .contains("without a path")
        );
    }

    #[test]
    fn answers_are_read_and_errors_say_why() {
        let ok = br#"{"content":[{"type":"text","text":"Hello "},{"type":"tool_use"},{"type":"text","text":"there"}]}"#;
        assert_eq!(anthropic_text(200, ok).unwrap(), "Hello there");
        let refused = br#"{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"#;
        let error = anthropic_text(401, refused).unwrap_err();
        assert!(
            error.contains("401") && error.contains("invalid x-api-key"),
            "{error}"
        );
        assert!(error.contains("secret set"), "{error}");
        assert!(
            anthropic_text(200, br#"{"content":[]}"#)
                .unwrap_err()
                .contains("no text")
        );
        assert!(
            anthropic_text(200, b"<html>")
                .unwrap_err()
                .contains("not JSON")
        );

        let ok = br#"{"choices":[{"message":{"role":"assistant","content":"Done."}}]}"#;
        assert_eq!(openai_text(200, ok).unwrap(), "Done.");
        let error =
            openai_text(404, br#"{"error":{"message":"model 'x' not found"}}"#).unwrap_err();
        assert!(
            error.contains("404") && error.contains("not found"),
            "{error}"
        );
        assert!(
            openai_text(200, br#"{"choices":[]}"#)
                .unwrap_err()
                .contains("no text")
        );
    }
}
