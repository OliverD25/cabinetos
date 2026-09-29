//! A test fixture for `core:request` and for `config-get` of the plugin's
//! own settings. `requester.ask {"request": {...}}` passes the request to the
//! core's `core-request` and answers `{"reply": <the core's reply>}`; a
//! refusal comes back as the command's error. `requester.setting {"path"}`
//! answers `{"value": <config-get of the path, or null>}`. Every `on-event`
//! it gets (`settings-changed`) goes on through `emit`, so a test sees it as
//! a `plugin_event`.

wit_bindgen::generate!({
    path: "../../../wit",
    world: "core-plugin",
});

use cabinetos::plugin::host;

struct Requester;

impl Guest for Requester {
    fn info() -> PluginInfo {
        PluginInfo {
            id: "requester".to_owned(),
            name: "Requester".to_owned(),
            version: env!("CARGO_PKG_VERSION").to_owned(),
            api_version: "0.2.0".to_owned(),
        }
    }

    fn activate(_ctx: Activation) -> Result<(), String> {
        host::register_command("requester.ask", "Ask the Core", "Requester", &[]);
        host::register_command("requester.setting", "Read a Setting", "Requester", &[]);
        Ok(())
    }

    fn deactivate() {}

    fn on_command(id: String, args: String) -> Result<String, String> {
        let args: serde_json::Value =
            serde_json::from_str(&args).map_err(|error| format!("args are not JSON: {error}"))?;
        match id.as_str() {
            "requester.ask" => {
                let request = match &args["request"] {
                    serde_json::Value::String(text) => text.clone(),
                    other => other.to_string(),
                };
                let reply = host::core_request(&request)?;
                let reply: serde_json::Value = serde_json::from_str(&reply)
                    .map_err(|error| format!("the reply is not JSON: {error}"))?;
                Ok(serde_json::json!({ "reply": reply }).to_string())
            }
            "requester.setting" => {
                let path = args["path"].as_str().ok_or("args need a \"path\"")?;
                let value = host::config_get(path)
                    .map(|text| serde_json::from_str(&text).unwrap_or(serde_json::Value::Null));
                Ok(serde_json::json!({ "value": value }).to_string())
            }
            other => Err(format!("Requester has no command {other}")),
        }
    }

    fn before_job(_job: JobSummary) -> JobVerdict {
        JobVerdict::Allow
    }

    fn on_listing_opened(_path: String, _entry_count: u32) {}

    fn on_event(name: String, payload: String) {
        host::emit(&name, &payload);
    }
}

export!(Requester);
