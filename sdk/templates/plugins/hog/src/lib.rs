//! A test fixture: `hog.eat` asks for 1 GiB, four times what a plugin may
//! have. The memory cannot grow, the allocation fails, the plugin traps.

wit_bindgen::generate!({
    path: "../../../wit",
    world: "core-plugin",
});

use cabinetos::plugin::host;

struct Hog;

impl Guest for Hog {
    fn info() -> PluginInfo {
        PluginInfo {
            id: "hog".to_owned(),
            name: "Hog".to_owned(),
            version: env!("CARGO_PKG_VERSION").to_owned(),
            api_version: "0.2.0".to_owned(),
        }
    }

    fn activate(_ctx: Activation) -> Result<(), String> {
        host::register_command("hog.eat", "Eat Memory", "Hog", &[]);
        Ok(())
    }

    fn deactivate() {}

    fn on_command(_id: String, _args: String) -> Result<String, String> {
        let meal = std::hint::black_box(vec![1u8; 1 << 30]);
        Ok(format!(r#"{{"eaten":{}}}"#, meal.len()))
    }

    fn before_job(_job: JobSummary) -> JobVerdict {
        JobVerdict::Allow
    }

    fn on_listing_opened(_path: String, _entry_count: u32) {}

    fn on_event(_name: String, _payload: String) {}
}

export!(Hog);
