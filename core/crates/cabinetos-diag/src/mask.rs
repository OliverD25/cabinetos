//! What heavy mode writes of a payload: secrets masked as `"***"`, and at
//! most a fixed number of bytes per line, the rest cut and marked
//! `truncated`.

use serde_json::Value;

/// The most bytes of a request's or a reply's JSON one heavy line holds.
pub const PAYLOAD_CAP: usize = 64 * 1024;

/// What a masked value becomes.
pub const MASK: &str = "***";

/// Fields whose values are masked wherever they are, compared without case.
const SECRET_FIELDS: &[&str] = &[
    "secret",
    "password",
    "passphrase",
    "api_key",
    "apikey",
    "token",
    "access_token",
    "refresh_token",
    "client_secret",
    "authorization",
    "x-api-key",
];

/// Headers whose values are masked in a `headers` list or object.
const SECRET_HEADERS: &[&str] = &[
    "authorization",
    "proxy-authorization",
    "x-api-key",
    "cookie",
];

/// Words that make a `CABINETOS_*` environment variable a secret.
const SECRET_ENV_WORDS: &[&str] = &["KEY", "TOKEN", "SECRET", "PASSWORD"];

/// Masks the secrets in `value`, in place:
///
/// - `value` and `secret` of a message whose `type` is `secret` or starts
///   with `secret_` (the request `secret_set`, the reply `secret`);
/// - every field named like a secret (`secret`, `password`, `token`,
///   `api_key`, `authorization`, …), at any depth;
/// - the values of the headers `authorization`, `proxy-authorization`,
///   `x-api-key` and `cookie` in a `headers` object, in a list of
///   `[name, value]` pairs, or in `{"name": …, "value": …}` objects.
pub fn mask_secrets(value: &mut Value) {
    match value {
        Value::Object(object) => {
            let secret_message = object
                .get("type")
                .and_then(Value::as_str)
                .is_some_and(|kind| kind == "secret" || kind.starts_with("secret_"));
            let secret_header = object
                .get("name")
                .and_then(Value::as_str)
                .is_some_and(is_secret_header);
            for (key, field) in object.iter_mut() {
                let lower = key.to_ascii_lowercase();
                let secret = SECRET_FIELDS.contains(&lower.as_str())
                    || ((secret_message || secret_header) && lower == "value");
                if secret {
                    mask(field);
                } else if lower == "headers" {
                    mask_headers(field);
                } else {
                    mask_secrets(field);
                }
            }
        }
        Value::Array(items) => items.iter_mut().for_each(mask_secrets),
        _ => {}
    }
}

fn mask(value: &mut Value) {
    if !value.is_null() {
        *value = Value::String(MASK.to_owned());
    }
}

fn is_secret_header(name: &str) -> bool {
    SECRET_HEADERS
        .iter()
        .any(|header| header.eq_ignore_ascii_case(name))
}

fn mask_headers(headers: &mut Value) {
    match headers {
        Value::Object(object) => {
            for (name, value) in object.iter_mut() {
                if is_secret_header(name) {
                    mask(value);
                } else {
                    mask_secrets(value);
                }
            }
        }
        Value::Array(items) => {
            for item in items {
                match item {
                    Value::Array(pair)
                        if pair.len() == 2 && pair[0].as_str().is_some_and(is_secret_header) =>
                    {
                        mask(&mut pair[1]);
                    }
                    other => mask_secrets(other),
                }
            }
        }
        other => mask_secrets(other),
    }
}

/// `value` as JSON text with its secrets masked, cut to at most `cap`
/// bytes. The flag says whether it was cut.
#[must_use]
pub fn masked_json(value: &Value, cap: usize) -> (String, bool) {
    let mut masked = value.clone();
    mask_secrets(&mut masked);
    cap_text(masked.to_string(), cap)
}

/// Like [`masked_json`] for a message's bytes as they went over the pipe.
/// Bytes that are not JSON are written as text, cut the same way.
#[must_use]
pub fn masked_json_bytes(json: &[u8], cap: usize) -> (String, bool) {
    match serde_json::from_slice::<Value>(json) {
        Ok(value) => masked_json(&value, cap),
        Err(_) => cap_text(String::from_utf8_lossy(json).into_owned(), cap),
    }
}

/// `text` cut to at most `cap` bytes, at a character boundary. The flag
/// says whether it was cut.
#[must_use]
pub fn cap_text(mut text: String, cap: usize) -> (String, bool) {
    if text.len() <= cap {
        return (text, false);
    }
    let mut end = cap;
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    text.truncate(end);
    (text, true)
}

/// Whether the environment variable `name` holds a secret: a `CABINETOS_`
/// variable whose name has `KEY`, `TOKEN`, `SECRET` or `PASSWORD` in it.
#[must_use]
pub fn is_secret_env(name: &str) -> bool {
    let upper = name.to_ascii_uppercase();
    upper.starts_with("CABINETOS_") && SECRET_ENV_WORDS.iter().any(|word| upper.contains(word))
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    fn masked(value: Value) -> Value {
        let mut value = value;
        mask_secrets(&mut value);
        value
    }

    #[test]
    fn a_secret_request_s_value_is_masked() {
        assert_eq!(
            masked(json!({"id": "01M", "type": "secret_set", "name": "openai", "value": "sk-123"})),
            json!({"id": "01M", "type": "secret_set", "name": "openai", "value": "***"})
        );
        assert_eq!(
            masked(json!({"id": "01M", "type": "secret", "value": "sk-123"})),
            json!({"id": "01M", "type": "secret", "value": "***"}),
            "the reply to secret_get"
        );
        assert_eq!(
            masked(json!({"type": "set_value", "path": "ui.theme", "value": "nord"})),
            json!({"type": "set_value", "path": "ui.theme", "value": "nord"}),
            "the value of any other request stays"
        );
    }

    #[test]
    fn fields_named_like_secrets_are_masked_at_any_depth() {
        assert_eq!(
            masked(
                json!({"args": {"Secret": "s", "nested": [{"api_key": "k", "keep": 1}]}, "token": null})
            ),
            json!({"args": {"Secret": "***", "nested": [{"api_key": "***", "keep": 1}]}, "token": null})
        );
    }

    #[test]
    fn secret_headers_are_masked_in_every_shape() {
        assert_eq!(
            masked(json!({"headers": {"Authorization": "Bearer x", "Accept": "text/plain"}})),
            json!({"headers": {"Authorization": "***", "Accept": "text/plain"}})
        );
        assert_eq!(
            masked(json!({"headers": [["x-api-key", "k"], ["accept", "a"]]})),
            json!({"headers": [["x-api-key", "***"], ["accept", "a"]]})
        );
        assert_eq!(
            masked(
                json!({"headers": [{"name": "Authorization", "value": "Basic y"}, {"name": "Host", "value": "h"}]})
            ),
            json!({"headers": [{"name": "Authorization", "value": "***"}, {"name": "Host", "value": "h"}]})
        );
    }

    #[test]
    fn long_payloads_are_cut_at_a_character_boundary() {
        let (text, cut) = masked_json(&json!({"a": "ж".repeat(100)}), 20);
        assert!(cut);
        assert!(text.len() <= 20);
        assert!(text.starts_with(r#"{"a":"ж"#), "{text}");
        let (whole, cut) = masked_json(&json!({"a": 1}), PAYLOAD_CAP);
        assert!(!cut);
        assert_eq!(whole, r#"{"a":1}"#);
        let (text, cut) = masked_json_bytes(b"not json \xff", 100);
        assert!(!cut);
        assert!(text.starts_with("not json"));
    }

    #[test]
    fn cabinetos_variables_that_look_like_keys_are_secrets() {
        assert!(is_secret_env("CABINETOS_OPENAI_API_KEY"));
        assert!(is_secret_env("cabinetos_github_token"));
        assert!(!is_secret_env("CABINETOS_LOG_DIR"));
        assert!(
            !is_secret_env("OPENAI_API_KEY"),
            "only the CabinetOS variables"
        );
    }
}
