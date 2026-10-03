//! Quick View (ADR 0023): the parts of the messages that the panel's
//! requests share, and the grammar of the kinds a viewer claims.
//!
//! A kind is a file name pattern in the grammar of a Tool Extension's
//! `accepts`: `*.ext` (an extension, which may have dots, such as
//! `*.tar.gz`) or a whole name (`README`, `Dockerfile`), compared without
//! case. No `*` alone and no other wildcards.

use serde::{Deserialize, Serialize};

use crate::Author;

/// The sizes `get_thumbnail` takes: the shell's thumbnail cache keeps
/// these, so an answer is most often a cache read.
pub const THUMBNAIL_SIZES: [u32; 3] = [96, 256, 768];

/// The largest `max_size` of `render_image`, on the longer side.
pub const MAX_RENDER_SIZE: u32 = 2560;

/// The most patterns one viewer's `quickView.kinds` may have.
pub const MAX_KIND_PATTERNS: usize = 512;

/// The longest pattern, in characters: a file name has at most 255.
const MAX_PATTERN_CHARS: usize = 255;

/// The value of `quickView.viewers` that means "no viewer, the thumbnail
/// only".
pub const NO_VIEWER: &str = "none";

/// Why a `thumbnail` reply carries no picture.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum ThumbnailReason {
    /// The shell has no thumbnail for this file.
    None,
    /// The shell did not answer within 2 s; its thread was replaced.
    Timeout,
    /// Four of the core's thumbnail threads are stuck in the shell.
    Busy,
    /// The file is not on this disk and its thumbnail is not in the
    /// shell's cache; nothing was downloaded.
    Cloud,
    /// A newer request of the same connection took its place in the queue.
    Superseded,
}

/// One viewer of the Quick View table: a Tool Extension with a valid
/// `quickView` block.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct QuickViewer {
    /// The tool's ID.
    pub id: String,
    /// Its name, as the panel shows it ("Image Viewer cannot show this
    /// file.").
    pub name: String,
    /// Its version.
    pub version: String,
    /// Its folder, from which the window starts the page.
    pub dir: String,
    /// The page Quick View loads, relative to `dir`: `quickView.entry`, or
    /// the tool's `entry` when it has none.
    pub entry: String,
}

/// One kind of the Quick View table: a pattern and the viewers for it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct QuickViewKind {
    /// The pattern, in lower case.
    pub pattern: String,
    /// The viewers' IDs, the one to use first: the user's choice, then the
    /// viewer installed first. Empty when `off`.
    pub viewers: Vec<String>,
    /// The user set this kind to `"none"`: the thumbnail only. Left out
    /// when false.
    #[serde(default, skip_serializing_if = "is_false")]
    pub off: bool,
}

/// The marketplace item `quick_view_offer` offers: a Tool Extension of the
/// extensions catalogue whose `quickView.kinds` claim the file's name.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct QuickViewOfferItem {
    /// The item's ID, for `install_extension`.
    pub id: String,
    /// Its name.
    pub name: String,
    /// The version an install would bring: the newest this core can run.
    pub version: String,
    /// The size of the download in bytes.
    pub size: u64,
    /// Who publishes it.
    pub author: Author,
    /// One or two sentences.
    pub description: String,
}

/// Why `quick_view_offer` offers nothing.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum OfferReason {
    /// The catalogue has no item this core can run, not installed, that
    /// claims the name.
    NoItem,
    /// The catalogue cannot be read.
    Offline,
}

#[expect(
    clippy::trivially_copy_pass_by_ref,
    reason = "serde's skip_serializing_if passes a reference"
)]
fn is_false(value: &bool) -> bool {
    !*value
}

/// Why `pattern` is not a kind, or `None` when it is one.
#[must_use]
pub fn kind_pattern_problem(pattern: &str) -> Option<String> {
    if pattern.is_empty() {
        return Some("a pattern is empty".to_owned());
    }
    if pattern.chars().count() > MAX_PATTERN_CHARS {
        return Some(format!(
            "`{pattern}` is longer than {MAX_PATTERN_CHARS} characters"
        ));
    }
    if pattern.trim() != pattern {
        return Some(format!("`{pattern}` starts or ends with a space"));
    }
    if pattern.contains(['/', '\\', ':', '?']) || pattern.chars().any(char::is_control) {
        return Some(format!(
            "`{pattern}` has a folder, a drive, a `?` or a control character; a kind is `*.ext` or a whole file name"
        ));
    }
    let body = pattern.strip_prefix("*.").unwrap_or(pattern);
    if body.is_empty() || body.contains('*') {
        return Some(format!(
            "`{pattern}` is not `*.ext` or a whole file name: `*` may only start `*.ext`"
        ));
    }
    None
}

/// Whether `name`, a file's name without its folder, is of the kind
/// `pattern`, without case: `*.ext` matches a name that ends with `.ext`
/// and has something before it, a whole name matches itself. The rule of
/// the window's `accepts` matcher.
#[must_use]
pub fn kind_pattern_matches(pattern: &str, name: &str) -> bool {
    let (pattern, name) = (pattern.to_lowercase(), name.to_lowercase());
    match pattern.strip_prefix('*') {
        Some(extension) => name.len() > extension.len() && name.ends_with(extension),
        None => name == pattern,
    }
}

/// Orders kinds as decision 1.2 of ADR 0023 does: a whole name before an
/// extension, a longer extension before a shorter one (`*.tar.gz` before
/// `*.gz`), then alphabetically. Smaller sorts first.
#[must_use]
pub fn kind_pattern_order(pattern: &str) -> (u8, std::cmp::Reverse<usize>, String) {
    let lower = pattern.to_lowercase();
    match lower.strip_prefix("*.") {
        Some(extension) => (1, std::cmp::Reverse(extension.chars().count()), lower),
        None => (0, std::cmp::Reverse(0), lower),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn kinds_are_extensions_or_whole_names() {
        for good in [
            "*.jpg",
            "*.tar.gz",
            "README",
            "Dockerfile",
            ".gitignore",
            "*.JPEG",
        ] {
            assert_eq!(kind_pattern_problem(good), None, "{good}");
        }
        for bad in [
            "",
            "*",
            "*.",
            "*.*",
            "*.j*g",
            "a*",
            "**.jpg",
            "*.jp?",
            "dir/a.txt",
            r"a\b",
            "c:x",
            " *.jpg",
            "*.jpg ",
        ] {
            assert!(kind_pattern_problem(bad).is_some(), "{bad:?}");
        }
        assert!(kind_pattern_problem(&"x".repeat(256)).is_some());
    }

    #[test]
    fn kinds_match_without_case_and_need_a_name_before_the_extension() {
        assert!(kind_pattern_matches("*.jpg", "IMG_0412.JPG"));
        assert!(kind_pattern_matches("*.TAR.GZ", "backup.tar.gz"));
        assert!(kind_pattern_matches("*.gz", "backup.tar.gz"));
        assert!(!kind_pattern_matches("*.jpg", ".jpg"));
        assert!(!kind_pattern_matches("*.jpg", "a.jpeg"));
        assert!(kind_pattern_matches("readme", "README"));
        assert!(!kind_pattern_matches("README", "README.md"));
    }

    #[test]
    fn whole_names_come_first_then_longer_extensions() {
        let mut kinds = vec!["*.gz", "*.tar.gz", "README", "*.png", "*.jpeg"];
        kinds.sort_by_key(|kind| kind_pattern_order(kind));
        assert_eq!(kinds, ["README", "*.tar.gz", "*.jpeg", "*.png", "*.gz"]);
    }
}
