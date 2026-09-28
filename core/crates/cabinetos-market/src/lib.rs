//! The marketplace client (`docs/marketplace.md`): the index, and installing
//! and removing Core Plugins, colour themes and Tool Extensions.
//!
//! - [`Source`] reads `marketplace.index`: an `https:` URL (plain `http:`
//!   only when the configuration allows it), a `file:` URL, or a path.
//! - [`Market::fetch`] reads the index: from disk, or from the web with
//!   `ureq` over rustls, using the Windows certificate store, and a cache
//!   with `If-None-Match`. An index item that does not follow the format is
//!   left out (and logged), so one bad item does not hide the others.
//! - [`Market::install`] downloads (or copies) the item into a temporary
//!   file, checks its SHA-256, unpacks it into a staging folder, checks it
//!   as its kind needs, and only then puts it in place. It records exactly
//!   which files it put there, in `<marketplace folder>\installed.json`.
//!   Nothing is run: a plugin arrives as `needs_review`.
//! - [`Market::uninstall`] removes exactly those files.
//!
//! The crate never touches the network on its own: only [`Market::fetch`]
//! and [`Market::install`] do, and only when the core asks for a client.
//!
//! Serves Constitution Article 2 (Free & Open Source: the marketplace is a
//! static index anyone can serve) and Article 8 (Sandboxed Extensibility:
//! users install and share extensions through a marketplace, and nothing
//! runs before the user grants it).
#![forbid(unsafe_code)]

mod index;
mod install;
mod tools;

use std::ffi::OsString;
use std::fmt;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use cabinetos_protocol::ErrorCode;

pub use index::{Index, Source, parse_index, search};
pub use install::{Dirs, Installed, Market};
pub use tools::{TOOL_MANIFEST_FILE, list_tools};

/// Environment variable naming the marketplace's own folder (the index
/// cache, downloads in progress, the record of installs).
pub const MARKETPLACE_DIR_ENV: &str = "CABINETOS_MARKETPLACE_DIR";

/// The folder of installed Tool Extensions: `explicit`, else
/// `%LOCALAPPDATA%\CabinetOS\tools`, the folder the window reads installed
/// tools from. There is no environment variable: the window's
/// `CABINETOS_TOOLS_DIR` names a folder of tools in development, and the
/// core, started by the window, inherits its environment.
#[must_use]
pub fn tools_dir(explicit: Option<PathBuf>) -> PathBuf {
    resolve_dir(explicit, None, std::env::var_os("LOCALAPPDATA"), "tools")
}

/// The marketplace's own folder: `explicit` wins, then
/// [`MARKETPLACE_DIR_ENV`], then `%LOCALAPPDATA%\CabinetOS\marketplace`.
#[must_use]
pub fn marketplace_dir(explicit: Option<PathBuf>) -> PathBuf {
    resolve_dir(
        explicit,
        std::env::var_os(MARKETPLACE_DIR_ENV),
        std::env::var_os("LOCALAPPDATA"),
        "marketplace",
    )
}

/// Without `LOCALAPPDATA` (not a normal Windows session) the default is
/// under the temp folder, as for the logs.
fn resolve_dir(
    explicit: Option<PathBuf>,
    env_dir: Option<OsString>,
    local_app_data: Option<OsString>,
    leaf: &str,
) -> PathBuf {
    if let Some(dir) = explicit {
        return dir;
    }
    if let Some(dir) = env_dir.filter(|dir| !dir.is_empty()) {
        return PathBuf::from(dir);
    }
    local_app_data
        .filter(|dir| !dir.is_empty())
        .map_or_else(std::env::temp_dir, PathBuf::from)
        .join("CabinetOS")
        .join(leaf)
}

/// Why a marketplace request failed: the protocol's error code and a
/// message for the user.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MarketError {
    /// The protocol error code.
    pub code: ErrorCode,
    /// What happened.
    pub message: String,
}

impl MarketError {
    /// An error with this code.
    pub fn new(code: ErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }

    /// A `marketplace_error`.
    pub(crate) fn market(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::MarketplaceError, message)
    }
}

impl fmt::Display for MarketError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for MarketError {}

/// `major.minor.patch`, numbers only.
pub(crate) fn parse_version(text: &str) -> Option<(u32, u32, u32)> {
    let mut parts = text.split('.');
    let version = (
        parts.next()?.parse().ok()?,
        parts.next()?.parse().ok()?,
        parts.next()?.parse().ok()?,
    );
    parts.next().is_none().then_some(version)
}

/// Milliseconds since 1970-01-01 UTC.
pub(crate) fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |since| {
            u64::try_from(since.as_millis()).unwrap_or(u64::MAX)
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn folders_come_from_the_flag_then_the_variable_then_local_app_data() {
        let local = Some(OsString::from(r"C:\Users\me\AppData\Local"));
        assert_eq!(
            resolve_dir(
                Some(PathBuf::from(r"C:\flag")),
                None,
                local.clone(),
                "tools"
            ),
            PathBuf::from(r"C:\flag")
        );
        assert_eq!(
            resolve_dir(
                None,
                Some(OsString::from(r"C:\env")),
                local.clone(),
                "tools"
            ),
            PathBuf::from(r"C:\env")
        );
        assert_eq!(
            resolve_dir(None, None, local, "marketplace"),
            PathBuf::from(r"C:\Users\me\AppData\Local\CabinetOS\marketplace")
        );
    }

    #[test]
    fn versions_are_three_numbers() {
        assert_eq!(parse_version("0.1.0"), Some((0, 1, 0)));
        assert!(parse_version("10.2.3") > parse_version("9.99.99"));
        for bad in ["1.0", "1.0.0.0", "1.x.0", "", "v1.0.0"] {
            assert_eq!(parse_version(bad), None, "{bad}");
        }
    }
}
