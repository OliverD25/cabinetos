//! Embeds the Windows version resource (version, description, copyright, icon).
//! Explorer's Details page shows it and the code signing service requires it.

use std::env;

const ICON: &str = "../../../ui/CabinetOS/Assets/CabinetOS.ico";

fn main() {
    println!("cargo::rerun-if-changed=build.rs");
    println!("cargo::rerun-if-changed={ICON}");

    // The target of the build, not the machine it runs on, which `cfg!` would test.
    if !env::var("CARGO_CFG_TARGET_OS").is_ok_and(|os| os == "windows") {
        return;
    }

    let mut resource = winresource::WindowsResource::new();
    resource
        .set("ProductName", "CabinetOS")
        .set("CompanyName", "the CabinetOS authors")
        .set(
            "LegalCopyright",
            "Copyright (c) 2026 the CabinetOS authors. MIT license.",
        )
        .set("FileDescription", "CabinetOS indexer service")
        .set("OriginalFilename", "cabinetos-indexer.exe")
        .set("InternalName", "cabinetos-indexer.exe")
        .set_icon(ICON);

    // A program without the resource must never be built, so a failure stops here.
    if let Err(error) = resource.compile() {
        panic!(
            "cannot embed the Windows version resource: {error}. The Rust build needs the \
             Windows SDK (rc.exe), which comes with the \"Desktop development with C++\" \
             workload of Visual Studio Build Tools; see docs/dev-setup.md."
        );
    }
}
