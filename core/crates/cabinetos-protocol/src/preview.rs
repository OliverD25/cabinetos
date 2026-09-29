//! Previews: a listing of proposed changes, with nothing done on disk
//! (`preview_listing`, `open_preview`, `preview_apply`, `preview_cancel`,
//! protocol version 13).
//!
//! A client (or a plugin) proposes rows; the core checks them and shows
//! them as a listing in shared memory, like a folder, with each row's change
//! and target. Applying it runs the rows in order as jobs.

use serde::{Deserialize, Serialize};

/// What a row of a preview would do.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum ChangeKind {
    /// Give `path` another name in its folder: `to` is the new name, or
    /// the new full path in the same folder.
    Rename,
    /// Move `path` into the folder `to`.
    Move,
    /// Copy `path` into the folder `to`.
    Copy,
    /// Put `path` into the Recycle Bin.
    Delete,
    /// Create `path`: a folder when it ends with `\`, else an empty file.
    Create,
}

impl ChangeKind {
    /// The byte a preview section stores for this kind (see
    /// [`crate::shm::PreviewRow`]).
    #[must_use]
    pub const fn to_raw(self) -> u8 {
        match self {
            Self::Rename => 1,
            Self::Move => 2,
            Self::Copy => 3,
            Self::Delete => 4,
            Self::Create => 5,
        }
    }

    /// Reads a stored byte; `None` for a value this build does not know.
    #[must_use]
    pub const fn from_raw(raw: u8) -> Option<Self> {
        match raw {
            1 => Some(Self::Rename),
            2 => Some(Self::Move),
            3 => Some(Self::Copy),
            4 => Some(Self::Delete),
            5 => Some(Self::Create),
            _ => None,
        }
    }
}

/// One proposed change.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PreviewRow {
    /// The file or folder it concerns, as an absolute path. For `create`,
    /// the new item: a trailing `\` makes it a folder.
    pub path: String,
    /// What would happen to it.
    pub kind: ChangeKind,
    /// For `rename`, the new name (or the new full path in the same
    /// folder); for `move` and `copy`, the folder it goes into; `null` for
    /// `delete` and `create`.
    #[serde(default)]
    pub to: Option<String>,
}

/// A listing in shared memory, as `listing_opened` describes it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct OpenedListing {
    /// Names the listing in `close_listing` and `describe_entries`.
    pub listing_id: u64,
    /// A handle to the section, already valid in the client's process. The
    /// client owns it and must close it.
    pub section_handle: u64,
    /// How many bytes of the section hold the listing.
    pub section_size: u64,
    /// Rows in the listing.
    pub entry_count: u32,
    /// Always 1: a preview is never read again.
    pub generation: u32,
    /// Microseconds the core spent writing it.
    pub elapsed_us: u64,
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    #[test]
    fn rows_have_the_documented_wire_form() {
        let row: PreviewRow =
            serde_json::from_value(json!({"path": "C:\\a.txt", "kind": "rename", "to": "b.txt"}))
                .unwrap();
        assert_eq!(row.kind, ChangeKind::Rename);
        assert_eq!(row.to.as_deref(), Some("b.txt"));
        let bare: PreviewRow =
            serde_json::from_value(json!({"path": "C:\\new\\", "kind": "create"})).unwrap();
        assert_eq!(bare.to, None);
        assert!(serde_json::to_value(&bare).unwrap()["to"].is_null());
        for kind in [
            ChangeKind::Rename,
            ChangeKind::Move,
            ChangeKind::Copy,
            ChangeKind::Delete,
            ChangeKind::Create,
        ] {
            assert_eq!(ChangeKind::from_raw(kind.to_raw()), Some(kind));
        }
        assert_eq!(ChangeKind::from_raw(0), None);
        assert!(serde_json::from_value::<ChangeKind>(json!("shred")).is_err());
    }
}
