//! A test fixture. `spinner.spin` loops forever while reading the clock:
//! little work per second, so the wall-clock deadline stops it.
//! `spinner.burn` loops forever on pure arithmetic, so the fuel limit may
//! stop it first.

wit_bindgen::generate!({
    path: "../../../wit",
    world: "core-plugin",
});

use cabinetos::plugin::host;

struct Spinner;

impl Guest for Spinner {
    fn info() -> PluginInfo {
        PluginInfo {
            id: "spinner".to_owned(),
            name: "Spinner".to_owned(),
            version: env!("CARGO_PKG_VERSION").to_owned(),
            api_version: "0.2.0".to_owned(),
        }
    }

    fn activate(_ctx: Activation) -> Result<(), String> {
        host::register_command("spinner.spin", "Spin", "Spinner", &[]);
        host::register_command("spinner.burn", "Burn", "Spinner", &[]);
        Ok(())
    }

    fn deactivate() {}

    fn on_command(id: String, _args: String) -> Result<String, String> {
        if id == "spinner.burn" {
            let mut value: u64 = 1;
            loop {
                value = std::hint::black_box(value.wrapping_mul(6_364_136_223_846_793_005).wrapping_add(1));
            }
        }
        loop {
            std::hint::black_box(std::time::Instant::now());
        }
    }

    fn before_job(_job: JobSummary) -> JobVerdict {
        JobVerdict::Allow
    }

    fn on_listing_opened(_path: String, _entry_count: u32) {}
}

export!(Spinner);
