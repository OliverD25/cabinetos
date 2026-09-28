//! The sample Core Plugin, and the template for new ones. It registers
//! `hello.say`, which answers with a greeting, prints a line (the core logs
//! it), and sends an event to the connected clients.

wit_bindgen::generate!({
    path: "../../../wit",
    world: "core-plugin",
});

use cabinetos::plugin::host::{self, LogLevel};

struct Hello;

impl Guest for Hello {
    fn info() -> PluginInfo {
        PluginInfo {
            id: "hello".to_owned(),
            name: "Hello".to_owned(),
            version: env!("CARGO_PKG_VERSION").to_owned(),
            api_version: "0.1.0".to_owned(),
        }
    }

    fn activate(ctx: Activation) -> Result<(), String> {
        host::register_command("hello.say", "Say Hello", "Hello", &[]);
        host::log(
            LogLevel::Info,
            &format!("Hello is active; its own folder is {}", ctx.data_dir),
        );
        Ok(())
    }

    fn deactivate() {}

    fn on_command(id: String, _args: String) -> Result<String, String> {
        match id.as_str() {
            "hello.say" => {
                println!("Hello says hello");
                host::emit("hello.said", r#"{"greeting":"hello"}"#);
                Ok(r#"{"message":"hello from Hello"}"#.to_owned())
            }
            other => Err(format!("Hello has no command {other}")),
        }
    }

    fn before_job(_job: JobSummary) -> JobVerdict {
        JobVerdict::Allow
    }

    fn on_listing_opened(_path: String, _entry_count: u32) {}
}

export!(Hello);
