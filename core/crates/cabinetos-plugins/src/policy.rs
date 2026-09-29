//! What a plugin may ask of the core, decided in one place: which requests
//! `core-request` lets through, and which settings `config-get` shows
//! (`docs/plugins.md`, "Asking the core" and "Settings").

use serde_json::Value;

/// The request types a plugin never sends with `core-request`, whatever its
/// manifest lists.
///
/// - `hello` and `window_state` make a plugin a window; `shutdown` ends the
///   core; `save_log_bundle` writes the user's logs into a zip.
/// - `set_value` and `grant_capabilities` write the settings, which include
///   the grants: a plugin could give itself what the user did not.
/// - `secret_set`, `secret_get`, `secret_delete` and `secret_list` reach the
///   Credential Manager: a plugin never sees a secret, the core adds it to a
///   request (`http-request`).
/// - `execute_command` would run any command, and one of the plugin's own
///   would wait for the plugin's thread, which is waiting for the answer.
/// - `install_extension` and `uninstall_extension` change the code the core
///   runs.
pub const NEVER_ALLOWED: &[&str] = &[
    "hello",
    "shutdown",
    "window_state",
    "set_value",
    "grant_capabilities",
    "secret_set",
    "secret_get",
    "secret_delete",
    "secret_list",
    "save_log_bundle",
    "execute_command",
    "install_extension",
    "uninstall_extension",
];

/// The largest request a plugin may send with `core-request`, in bytes.
pub const MAX_REQUEST_BYTES: usize = 1024 * 1024;

/// Whether no manifest can allow this request type.
#[must_use]
pub fn never_allowed(request_type: &str) -> bool {
    NEVER_ALLOWED.contains(&request_type)
}

/// Checks the request a plugin passed to `core-request` against the types
/// its manifest lists, and returns it without the `id` and `trace` the core
/// sets itself. The error names the request type.
pub fn check_request(listed: &[String], json: &str) -> Result<Value, String> {
    if json.len() > MAX_REQUEST_BYTES {
        return Err(format!(
            "core-request: the request is {} bytes; at most {MAX_REQUEST_BYTES} are taken",
            json.len()
        ));
    }
    let mut request: Value = serde_json::from_str(json)
        .map_err(|error| format!("core-request: the request is not JSON: {error}"))?;
    let object = request
        .as_object_mut()
        .ok_or("core-request: the request must be a JSON object with a `type`")?;
    let kind = object
        .get("type")
        .and_then(Value::as_str)
        .ok_or("core-request: the request has no string `type`")?
        .to_owned();
    if never_allowed(&kind) {
        return Err(format!(
            "core-request: the request type `{kind}` is never allowed for a plugin"
        ));
    }
    if !listed.contains(&kind) {
        return Err(format!(
            "core-request: the request type `{kind}` is not in the `requests` of this plugin's `core:request` capability"
        ));
    }
    object.remove("id");
    object.remove("trace");
    Ok(request)
}

/// Whether the plugin `plugin_id` may read the configuration at `path` with
/// `config-get`. Of the `plugins` section only the plugin's own `settings`
/// (and the paths under them) are its business: what other plugins were
/// granted, or asked for, is not.
#[must_use]
pub fn may_read_config(plugin_id: &str, path: &str) -> bool {
    if path != "plugins" && !path.starts_with("plugins.") {
        return true;
    }
    let mut keys = path.split('.');
    matches!(
        (keys.next(), keys.next(), keys.next()),
        (Some("plugins"), Some(id), Some("settings")) if id == plugin_id
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn listed(types: &[&str]) -> Vec<String> {
        types.iter().map(|kind| (*kind).to_owned()).collect()
    }

    #[test]
    fn only_a_listed_type_passes_and_the_core_sets_id_and_trace() {
        let allowed = listed(&["get_window_state", "search"]);
        let request = check_request(
            &allowed,
            r#"{"type":"search","query":"cat","id":"mine","trace":"mine"}"#,
        )
        .unwrap();
        assert_eq!(
            request,
            serde_json::json!({"type": "search", "query": "cat"})
        );

        let refused = check_request(&allowed, r#"{"type":"list_jobs"}"#).unwrap_err();
        assert!(refused.contains("`list_jobs`"), "{refused}");
        assert!(refused.contains("not in the `requests`"), "{refused}");
    }

    #[test]
    fn a_request_that_is_not_a_typed_object_is_refused() {
        let allowed = listed(&["search"]);
        for bad in ["not json", "[]", "5", r#"{"query":"x"}"#, r#"{"type":7}"#] {
            assert!(check_request(&allowed, bad).is_err(), "{bad}");
        }
        let long = format!(
            r#"{{"type":"search","query":"{}"}}"#,
            "x".repeat(MAX_REQUEST_BYTES)
        );
        assert!(
            check_request(&allowed, &long)
                .unwrap_err()
                .contains("bytes")
        );
    }

    /// The list is one thing: every entry is a request type this core has,
    /// and each is refused whatever the manifest lists.
    #[test]
    fn what_no_manifest_can_allow_stays_refused_and_is_a_real_request_type() {
        assert_eq!(
            NEVER_ALLOWED.len(),
            13,
            "adding to the list is a decision: update the docs and the count"
        );
        for kind in NEVER_ALLOWED {
            assert!(
                cabinetos_protocol::Request::TYPES.contains(kind),
                "{kind} is not a request type"
            );
            let everything = listed(NEVER_ALLOWED);
            let refused =
                check_request(&everything, &format!(r#"{{"type":"{kind}"}}"#)).unwrap_err();
            assert!(refused.contains(&format!("`{kind}`")), "{refused}");
            assert!(refused.contains("never allowed"), "{refused}");
        }
        for kind in [
            "hello",
            "shutdown",
            "set_value",
            "secret_set",
            "secret_get",
            "secret_delete",
            "secret_list",
            "save_log_bundle",
            "window_state",
        ] {
            assert!(never_allowed(kind), "{kind}");
        }
        for kind in [
            "get_window_state",
            "preview_listing",
            "preview_apply",
            "preview_cancel",
            "undo_job",
            "search",
        ] {
            assert!(!never_allowed(kind), "{kind} is what the agent uses");
        }
    }

    #[test]
    fn a_plugin_reads_its_own_settings_and_nothing_else_of_the_plugins_section() {
        assert!(may_read_config("agent", "plugins.agent.settings"));
        assert!(may_read_config("agent", "plugins.agent.settings.provider"));
        assert!(may_read_config(
            "agent",
            "plugins.agent.settings.rules.0.folder"
        ));
        assert!(!may_read_config("agent", "plugins"));
        assert!(!may_read_config("agent", "plugins.agent"));
        assert!(!may_read_config("agent", "plugins.agent.granted"));
        assert!(!may_read_config("agent", "plugins.agent.enabled"));
        assert!(!may_read_config("agent", "plugins.other.settings"));
        assert!(!may_read_config("agent", "plugins.other.settings.provider"));
        assert!(!may_read_config("agent", "plugins.agentx.settings"));
        assert!(may_read_config("agent", "ui.theme"));
        assert!(may_read_config("agent", "pluginsx.y"));
    }
}
