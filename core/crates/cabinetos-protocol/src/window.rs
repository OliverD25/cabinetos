//! What a window shows: its panes, their tabs, the cursor and the marked
//! rows (`window_state`, `get_window_state`, protocol version 13).
//!
//! The window owns this state (Phase 12: tabs per pane) and tells the core
//! on each change. The core only stores the last state of each client, so a
//! program that has no window of its own (the command line, a plugin) can
//! ask what the user looks at.

use serde::{Deserialize, Serialize};

/// A file pane of the window.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum Pane {
    /// The left pane (the only one in single-pane mode).
    #[default]
    Left,
    /// The right pane.
    Right,
}

/// What a window shows, as its `window_state` request says it.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct WindowState {
    /// The pane that has the keyboard.
    pub active_pane: Pane,
    /// Both panes.
    pub panes: WindowPanes,
}

/// The two panes of a window.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct WindowPanes {
    /// The left pane.
    pub left: PaneState,
    /// The right pane.
    pub right: PaneState,
}

/// One pane: its tabs, the tab in front, the cursor row and the marked rows.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PaneState {
    /// The pane's tabs, left to right.
    #[serde(default)]
    pub tabs: Vec<WindowTab>,
    /// The index of the tab in front, from 0.
    #[serde(default)]
    pub active: u32,
    /// The full path of the row the cursor is on; `null` in an empty
    /// folder or a tab that shows a tool.
    #[serde(default)]
    pub cursor: Option<String>,
    /// The full paths of the marked rows, in the pane's order. A window
    /// sends at most 1,000 of them (`marked_total` says when there are
    /// more).
    #[serde(default)]
    pub marked: Vec<String>,
    /// How many rows are marked in all, when that is more than `marked`
    /// lists (protocol version 18); left out when `marked` is complete.
    /// A program that acts on the marks must not take the list for all of
    /// them when this is present.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub marked_total: Option<u32>,
}

/// One tab of a pane.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct WindowTab {
    /// The folder the tab shows, or the file a tool shows.
    pub path: String,
    /// A locked tab stays on its folder.
    #[serde(default)]
    pub locked: bool,
    /// The Tool Extension the tab shows (its `tool.json` ID); `null` for a
    /// folder.
    #[serde(default)]
    pub tool: Option<String>,
}

impl WindowState {
    /// The pane that has the keyboard.
    #[must_use]
    pub const fn active(&self) -> &PaneState {
        match self.active_pane {
            Pane::Left => &self.panes.left,
            Pane::Right => &self.panes.right,
        }
    }
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    #[test]
    fn the_wire_form_follows_the_protocol_text() {
        let state: WindowState = serde_json::from_value(json!({
            "active_pane": "right",
            "panes": {
                "left": {"tabs": [{"path": "C:\\x", "locked": false, "tool": null}], "active": 0, "cursor": "C:\\x\\a.txt", "marked": []},
                "right": {"tabs": [{"path": "D:\\y", "locked": true, "tool": "md-preview"}], "active": 0, "cursor": null, "marked": ["D:\\y\\1", "D:\\y\\2"]}
            }
        }))
        .unwrap();
        assert_eq!(state.active_pane, Pane::Right);
        assert_eq!(state.active().marked.len(), 2);
        assert_eq!(
            state.panes.right.tabs[0].tool.as_deref(),
            Some("md-preview")
        );
        let back = serde_json::to_value(&state).unwrap();
        // Nulls stay on the wire, as the protocol text writes them.
        assert!(back["panes"]["right"]["cursor"].is_null());
        assert!(back["panes"]["left"]["tabs"][0]["tool"].is_null());
        // A client may leave the optional parts out.
        let bare: PaneState = serde_json::from_value(json!({})).unwrap();
        assert_eq!(bare, PaneState::default());
        assert!(serde_json::from_value::<Pane>(json!("middle")).is_err());
    }

    #[test]
    fn the_marked_total_is_left_out_unless_the_list_is_short() {
        let complete: PaneState = serde_json::from_value(json!({"marked": ["C:\\a"]})).unwrap();
        assert_eq!(complete.marked_total, None, "an older window sends none");
        assert!(
            serde_json::to_value(&complete)
                .unwrap()
                .get("marked_total")
                .is_none()
        );
        let cut: PaneState =
            serde_json::from_value(json!({"marked": ["C:\\a"], "marked_total": 5000})).unwrap();
        assert_eq!(cut.marked_total, Some(5000));
        assert_eq!(serde_json::to_value(&cut).unwrap()["marked_total"], 5000);
    }
}
