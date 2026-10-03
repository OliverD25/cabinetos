//! Tool Extensions as the core sees them: folders under the tools folder,
//! each with a `tool.json` (`docs/tool-extensions.md`). The window hosts
//! them; the core installs, lists and removes them, and builds the Quick
//! View table from their `quickView` blocks (ADR 0023, decision 1). It
//! reads the keys below and leaves the others (`accepts`, `placement`) to
//! the window, which reads the file strictly; `entry` it reads only for
//! Quick View.

use std::collections::{BTreeMap, HashSet};
use std::path::{Path, PathBuf};

use cabinetos_protocol::{
    ExtensionKind, MAX_KIND_PATTERNS, NO_VIEWER, QuickViewKind, QuickViewer, ToolInfo,
    extension_id_problem, kind_pattern_order, kind_pattern_problem,
};
use serde::Deserialize;
use serde_json::Value;

use crate::{Installed, parse_version};

/// The manifest file in a tool's folder.
pub const TOOL_MANIFEST_FILE: &str = "tool.json";

/// What the core reads of `tool.json`.
#[derive(Debug, Deserialize)]
pub(crate) struct ToolManifest {
    pub(crate) id: String,
    pub(crate) name: String,
    pub(crate) version: String,
    pub(crate) author: String,
    pub(crate) description: String,
    /// Read only for Quick View, whose page falls back to it.
    #[serde(default)]
    pub(crate) entry: Option<Value>,
    /// Checked only for Quick View: a wrong block leaves the tool out of
    /// the table and touches nothing else.
    #[serde(default, rename = "quickView")]
    pub(crate) quick_view: Option<Value>,
}

/// The longest tool ID: the window makes it a host name, whose parts have
/// at most 63 characters.
const MAX_TOOL_ID: usize = 63;

/// Reads and checks `<dir>/tool.json`.
pub(crate) fn read_manifest(dir: &Path) -> Result<ToolManifest, String> {
    let path = dir.join(TOOL_MANIFEST_FILE);
    let text =
        std::fs::read_to_string(&path).map_err(|error| format!("{}: {error}", path.display()))?;
    let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
    let manifest: ToolManifest =
        serde_json::from_str(text).map_err(|error| format!("{}: {error}", path.display()))?;
    if let Some(problem) = extension_id_problem(&manifest.id) {
        return Err(format!("{}: id: {problem}", path.display()));
    }
    if manifest.id.len() > MAX_TOOL_ID {
        return Err(format!(
            "{}: a tool's id has at most {MAX_TOOL_ID} characters",
            path.display()
        ));
    }
    for (field, value) in [
        ("name", &manifest.name),
        ("author", &manifest.author),
        ("description", &manifest.description),
    ] {
        if value.trim().is_empty() {
            return Err(format!("{}: `{field}` is empty", path.display()));
        }
    }
    if parse_version(&manifest.version).is_none() {
        return Err(format!(
            "{}: version `{}` is not major.minor.patch",
            path.display(),
            manifest.version
        ));
    }
    Ok(manifest)
}

/// Every tool in `dir` with a valid `tool.json` whose `id` is its folder's
/// name, by ID. Others are logged and left out.
#[must_use]
pub fn list_tools(dir: &Path) -> Vec<ToolInfo> {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return Vec::new();
    };
    let mut tools = Vec::new();
    for entry in entries.flatten() {
        let folder = entry.path();
        if !folder.join(TOOL_MANIFEST_FILE).is_file() {
            continue;
        }
        match read_manifest(&folder) {
            Ok(manifest) if folder.file_name().is_some_and(|name| *name == *manifest.id) => {
                tools.push(ToolInfo {
                    id: manifest.id,
                    name: manifest.name,
                    version: manifest.version,
                    author: manifest.author,
                    description: manifest.description,
                    dir: folder.display().to_string(),
                });
            }
            Ok(manifest) => tracing::warn!(
                dir = %folder.display(),
                id = %manifest.id,
                "a tool's id is not its folder's name; left out"
            ),
            Err(error) => tracing::warn!(%error, "not a valid tool; left out"),
        }
    }
    tools.sort_by(|a, b| a.id.cmp(&b.id));
    tools
}

/// A Tool Extension with a valid `quickView` block.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Viewer {
    /// What the table says of it.
    pub info: QuickViewer,
    /// The kinds it claims, in lower case, each once.
    pub kinds: Vec<String>,
}

/// The Quick View table: the viewers, the one installed first first, and
/// the kinds in the order the window tries them.
pub type QuickViewTable = (Vec<QuickViewer>, Vec<QuickViewKind>);

/// Every viewer, in the order of decision 1.2 of ADR 0023: the tools of
/// `dev` (the window's development folder) by folder name; then the tools
/// of `tools` that the marketplace installed, by their time in
/// `installed`; then the tools copied there by hand, by folder name. A tool
/// of `dev` wins over one with the same ID in `tools`. Reads the folders.
#[must_use]
pub fn quick_view_viewers(
    dev: Option<&Path>,
    tools: &Path,
    installed: &BTreeMap<String, Installed>,
) -> Vec<Viewer> {
    let mut viewers = dev.map(viewers_in).unwrap_or_default();
    let mut seen: HashSet<String> = viewers
        .iter()
        .map(|viewer| viewer.info.id.clone())
        .collect();
    let (mut from_market, mut by_hand): (Vec<Viewer>, Vec<Viewer>) = viewers_in(tools)
        .into_iter()
        .filter(|viewer| seen.insert(viewer.info.id.clone()))
        .partition(|viewer| {
            installed
                .get(&viewer.info.id)
                .is_some_and(|record| record.kind == ExtensionKind::Tool)
        });
    from_market.sort_by_key(|viewer| {
        (
            installed
                .get(&viewer.info.id)
                .map_or(u64::MAX, |record| record.installed_at_ms),
            viewer.info.id.clone(),
        )
    });
    by_hand.sort_by(|a, b| a.info.id.cmp(&b.info.id));
    viewers.extend(from_market);
    viewers.extend(by_hand);
    viewers
}

/// The table for `viewers` (in their order) and the user's
/// `quickView.viewers`. Each pattern is one kind, in lower case. A kind's
/// viewers: the user's choice first, then the viewers that claim it; a kind
/// set to `none` is `off` with no viewers. A choice that names no viewer
/// is left out. The kinds of the user's choices come first, then the
/// claimed ones; within each group a whole name before an extension and a
/// longer extension before a shorter one.
#[must_use]
pub fn quick_view_table(viewers: &[Viewer], choices: &BTreeMap<String, String>) -> QuickViewTable {
    let mut claims: BTreeMap<String, Vec<String>> = BTreeMap::new();
    for viewer in viewers {
        for kind in &viewer.kinds {
            claims
                .entry(kind.clone())
                .or_default()
                .push(viewer.info.id.clone());
        }
    }
    let known: HashSet<&str> = viewers
        .iter()
        .map(|viewer| viewer.info.id.as_str())
        .collect();
    let mut chosen: Vec<QuickViewKind> = Vec::new();
    for (pattern, choice) in choices {
        let pattern = pattern.to_lowercase();
        if chosen.iter().any(|kind| kind.pattern == pattern) {
            continue;
        }
        let claimants = claims.get(&pattern).cloned().unwrap_or_default();
        if choice == NO_VIEWER {
            chosen.push(QuickViewKind {
                pattern,
                viewers: Vec::new(),
                off: true,
            });
        } else if known.contains(choice.as_str()) {
            let mut ids = vec![choice.clone()];
            ids.extend(claimants.into_iter().filter(|id| id != choice));
            chosen.push(QuickViewKind {
                pattern,
                viewers: ids,
                off: false,
            });
        }
    }
    let mut claimed: Vec<QuickViewKind> = claims
        .into_iter()
        .filter(|(pattern, _)| !chosen.iter().any(|kind| kind.pattern == *pattern))
        .map(|(pattern, viewers)| QuickViewKind {
            pattern,
            viewers,
            off: false,
        })
        .collect();
    chosen.sort_by_key(|kind| kind_pattern_order(&kind.pattern));
    claimed.sort_by_key(|kind| kind_pattern_order(&kind.pattern));
    chosen.extend(claimed);
    (
        viewers.iter().map(|viewer| viewer.info.clone()).collect(),
        chosen,
    )
}

/// The viewers among the tools of `dir`, by folder name. A tool whose
/// `quickView` block is wrong is left out with a warning that names the
/// file; a tool without one is no viewer.
fn viewers_in(dir: &Path) -> Vec<Viewer> {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return Vec::new();
    };
    let mut folders: Vec<PathBuf> = entries
        .flatten()
        .map(|entry| entry.path())
        .filter(|folder| folder.join(TOOL_MANIFEST_FILE).is_file())
        .collect();
    folders.sort();
    let mut viewers = Vec::new();
    for folder in folders {
        let Ok(manifest) = read_manifest(&folder) else {
            // `list_tools` says why, for the tools folder.
            continue;
        };
        if folder.file_name().is_none_or(|name| *name != *manifest.id) {
            continue;
        }
        match quick_view_block(&folder, &manifest) {
            Ok(Some((kinds, entry))) => viewers.push(Viewer {
                info: QuickViewer {
                    id: manifest.id,
                    name: manifest.name,
                    version: manifest.version,
                    dir: folder.display().to_string(),
                    entry,
                },
                kinds,
            }),
            Ok(None) => {}
            Err(problem) => tracing::warn!(
                file = %folder.join(TOOL_MANIFEST_FILE).display(),
                %problem,
                "a tool's quickView block is not valid; it is no Quick View viewer, and its pane use is not touched"
            ),
        }
    }
    viewers
}

/// The kinds (lower case, each once) and the page of a tool's `quickView`
/// block, `None` when it has none.
fn quick_view_block(
    dir: &Path,
    manifest: &ToolManifest,
) -> Result<Option<(Vec<String>, String)>, String> {
    let Some(block) = &manifest.quick_view else {
        return Ok(None);
    };
    let Some(block) = block.as_object() else {
        return Err("`quickView` must be an object".to_owned());
    };
    if let Some(unknown) = block
        .keys()
        .find(|key| !matches!(key.as_str(), "kinds" | "entry"))
    {
        return Err(format!(
            "unknown field `quickView.{unknown}`, expected `kinds` or `entry`"
        ));
    }
    let Some(list) = block.get("kinds").and_then(Value::as_array) else {
        return Err("`quickView.kinds` must be a list of patterns such as `*.png`".to_owned());
    };
    if list.is_empty() || list.len() > MAX_KIND_PATTERNS {
        return Err(format!(
            "`quickView.kinds` has {} patterns; a viewer claims 1 to {MAX_KIND_PATTERNS}",
            list.len()
        ));
    }
    let mut kinds: Vec<String> = Vec::new();
    for pattern in list {
        let Some(pattern) = pattern.as_str() else {
            return Err("each of `quickView.kinds` must be text".to_owned());
        };
        if let Some(problem) = kind_pattern_problem(pattern) {
            return Err(format!("quickView.kinds: {problem}"));
        }
        let lower = pattern.to_lowercase();
        if !kinds.contains(&lower) {
            kinds.push(lower);
        }
    }
    let (entry, field) = match block.get("entry") {
        Some(entry) => (Some(entry), "quickView.entry"),
        None => (manifest.entry.as_ref(), "entry"),
    };
    let Some(entry) = entry.and_then(Value::as_str) else {
        return Err(format!("`{field}` must name the page Quick View loads"));
    };
    if !is_relative_page(entry) {
        return Err(format!(
            "`{field}` must be an .html file inside the tool's folder: {entry}"
        ));
    }
    if !dir.join(entry).is_file() {
        return Err(format!("the page {entry} of `{field}` is missing"));
    }
    Ok(Some((kinds, entry.to_owned())))
}

/// A page inside the folder, as the window checks `entry`: relative, with
/// forward or back slashes, no `.` or `..`, no drive, ending in `.html` or
/// `.htm`.
fn is_relative_page(entry: &str) -> bool {
    !entry.contains(':')
        && !entry
            .split(['/', '\\'])
            .any(|part| matches!(part, "" | "." | ".."))
        && Path::new(entry).extension().is_some_and(|extension| {
            extension.eq_ignore_ascii_case("html") || extension.eq_ignore_ascii_case("htm")
        })
}

#[cfg(test)]
mod tests {
    use std::fs;

    use super::*;

    #[test]
    fn tools_are_listed_by_their_manifest() {
        let root = std::env::temp_dir().join("cabinetos-core-test");
        fs::create_dir_all(&root).unwrap();
        let scratch = tempfile::Builder::new()
            .prefix("tools")
            .tempdir_in(root)
            .unwrap();
        let dir = scratch.path();
        let write = |folder: &str, text: &str| {
            fs::create_dir_all(dir.join(folder)).unwrap();
            fs::write(dir.join(folder).join(TOOL_MANIFEST_FILE), text).unwrap();
        };
        write(
            "md-preview",
            r#"{"id":"md-preview","name":"Markdown Preview","version":"1.0.0","author":"Me","description":"Shows Markdown.","entry":"index.html"}"#,
        );
        write(
            "other",
            r#"{"id":"md-preview","name":"X","version":"1.0.0","author":"Me","description":"X."}"#,
        );
        write(
            "no-author",
            r#"{"id":"no-author","name":"X","version":"1.0.0","description":"X."}"#,
        );
        let long = "x".repeat(64);
        write(
            &long,
            &format!(
                r#"{{"id":"{long}","name":"X","version":"1.0.0","author":"Me","description":"X."}}"#
            ),
        );
        write("broken", "{");
        fs::create_dir_all(dir.join("empty")).unwrap();
        let tools = list_tools(dir);
        assert_eq!(tools.len(), 1);
        assert_eq!(tools[0].id, "md-preview");
        assert_eq!(tools[0].name, "Markdown Preview");
        assert_eq!(tools[0].author, "Me");
        assert!(tools[0].dir.ends_with("md-preview"));
        assert!(list_tools(&dir.join("nothing")).is_empty());
    }

    fn scratch(prefix: &str) -> tempfile::TempDir {
        let root = std::env::temp_dir().join("cabinetos-core-test");
        fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix(prefix)
            .tempdir_in(root)
            .unwrap()
    }

    /// Writes the tool `id` into `dir` with this `quickView` block (none
    /// when `Value::Null`), and its pages.
    fn viewer(dir: &Path, id: &str, quick_view: &Value) {
        let folder = dir.join(id);
        fs::create_dir_all(&folder).unwrap();
        let mut manifest = serde_json::json!({
            "id": id, "name": format!("Viewer {id}"), "version": "1.0.0", "author": "Me",
            "description": "Shows files.", "entry": "index.html", "accepts": [], "placement": "pane"
        });
        if !quick_view.is_null() {
            manifest["quickView"] = quick_view.clone();
        }
        fs::write(folder.join(TOOL_MANIFEST_FILE), manifest.to_string()).unwrap();
        fs::write(folder.join("index.html"), "<!doctype html>").unwrap();
        fs::write(folder.join("quickview.html"), "<!doctype html>").unwrap();
    }

    fn installed_at(ms: u64) -> Installed {
        Installed {
            kind: ExtensionKind::Tool,
            version: "1.0.0".to_owned(),
            sha256: String::new(),
            source: "index.json".to_owned(),
            installed_at_ms: ms,
            files: Vec::new(),
        }
    }

    fn ids(viewers: &[Viewer]) -> Vec<&str> {
        viewers
            .iter()
            .map(|viewer| viewer.info.id.as_str())
            .collect()
    }

    fn kind<'a>(table: &'a QuickViewTable, pattern: &str) -> &'a QuickViewKind {
        table.1.iter().find(|kind| kind.pattern == pattern).unwrap()
    }

    #[test]
    fn two_viewers_on_one_kind_give_the_earlier_install_first() {
        let scratch = scratch("viewers");
        let tools = scratch.path();
        viewer(tools, "a-viewer", &serde_json::json!({"kinds": ["*.png"]}));
        viewer(
            tools,
            "b-viewer",
            &serde_json::json!({"kinds": ["*.PNG", "*.jpg"], "entry": "quickview.html"}),
        );
        viewer(tools, "by-hand", &serde_json::json!({"kinds": ["*.png"]}));
        viewer(tools, "no-viewer", &Value::Null);
        // b was installed before a, by-hand was copied in.
        let installed = BTreeMap::from([
            ("a-viewer".to_owned(), installed_at(2_000)),
            ("b-viewer".to_owned(), installed_at(1_000)),
        ]);
        let viewers = quick_view_viewers(None, tools, &installed);
        assert_eq!(ids(&viewers), ["b-viewer", "a-viewer", "by-hand"]);
        assert_eq!(viewers[0].info.entry, "quickview.html");
        assert_eq!(
            viewers[1].info.entry, "index.html",
            "the tool's entry without quickView.entry"
        );
        assert_eq!(viewers[0].kinds, ["*.png", "*.jpg"]);
        let table = quick_view_table(&viewers, &BTreeMap::new());
        assert_eq!(
            kind(&table, "*.png").viewers,
            ["b-viewer", "a-viewer", "by-hand"]
        );
        assert_eq!(kind(&table, "*.jpg").viewers, ["b-viewer"]);
        assert_eq!(table.0.len(), 3);
    }

    #[test]
    fn a_whole_name_comes_before_an_extension_and_a_longer_extension_first() {
        let scratch = scratch("viewers");
        let tools = scratch.path();
        viewer(
            tools,
            "archives",
            &serde_json::json!({"kinds": ["*.gz", "*.tar.gz", "Dockerfile", "*.png"]}),
        );
        let viewers = quick_view_viewers(None, tools, &BTreeMap::new());
        let table = quick_view_table(&viewers, &BTreeMap::new());
        let order: Vec<&str> = table.1.iter().map(|kind| kind.pattern.as_str()).collect();
        assert_eq!(order, ["dockerfile", "*.tar.gz", "*.png", "*.gz"]);
    }

    #[test]
    fn the_users_choice_comes_first_and_none_turns_a_kind_off() {
        let scratch = scratch("viewers");
        let tools = scratch.path();
        viewer(
            tools,
            "image-viewer",
            &serde_json::json!({"kinds": ["*.png", "*.svg", "*.gz"]}),
        );
        viewer(
            tools,
            "photo-pro",
            &serde_json::json!({"kinds": ["*.png", "*.tar.gz"]}),
        );
        let viewers = quick_view_viewers(None, tools, &BTreeMap::new());
        let choices = BTreeMap::from([
            ("*.PNG".to_owned(), "photo-pro".to_owned()),
            ("*.svg".to_owned(), "none".to_owned()),
            ("*.gz".to_owned(), "image-viewer".to_owned()),
            ("*.txt".to_owned(), "gone-viewer".to_owned()),
        ]);
        let table = quick_view_table(&viewers, &choices);
        assert_eq!(kind(&table, "*.png").viewers, ["photo-pro", "image-viewer"]);
        let svg = kind(&table, "*.svg");
        assert!(svg.off && svg.viewers.is_empty());
        assert!(!kind(&table, "*.png").off);
        assert!(
            table.1.iter().all(|kind| kind.pattern != "*.txt"),
            "a choice of no viewer is left out"
        );
        // The user's kinds are tried first: a.tar.gz goes to the chosen *.gz.
        let order: Vec<&str> = table.1.iter().map(|kind| kind.pattern.as_str()).collect();
        assert_eq!(order, ["*.png", "*.svg", "*.gz", "*.tar.gz"]);
    }

    #[test]
    fn a_bad_block_leaves_the_viewer_out_and_keeps_its_pane_use() {
        let scratch = scratch("viewers");
        let tools = scratch.path();
        viewer(tools, "bad-pattern", &serde_json::json!({"kinds": ["*"]}));
        viewer(
            tools,
            "bad-entry",
            &serde_json::json!({"kinds": ["*.png"], "entry": "../x.html"}),
        );
        viewer(
            tools,
            "missing-page",
            &serde_json::json!({"kinds": ["*.png"], "entry": "gone.html"}),
        );
        viewer(tools, "no-kinds", &serde_json::json!({"kinds": []}));
        viewer(
            tools,
            "extra-key",
            &serde_json::json!({"kinds": ["*.png"], "mime": ["image/png"]}),
        );
        viewer(tools, "good", &serde_json::json!({"kinds": ["*.png"]}));
        let viewers = quick_view_viewers(None, tools, &BTreeMap::new());
        assert_eq!(ids(&viewers), ["good"]);
        let listed: Vec<String> = list_tools(tools).into_iter().map(|tool| tool.id).collect();
        assert_eq!(listed.len(), 6, "{listed:?}");
        let too_many: Vec<String> = (0..=MAX_KIND_PATTERNS).map(|n| format!("*.e{n}")).collect();
        viewer(tools, "too-many", &serde_json::json!({ "kinds": too_many }));
        assert_eq!(
            ids(&quick_view_viewers(None, tools, &BTreeMap::new())),
            ["good"]
        );
    }

    #[test]
    fn the_development_folder_comes_first_and_wins_over_an_installed_copy() {
        let installed_dir = scratch("viewers");
        let dev_dir = scratch("dev-tools");
        viewer(
            installed_dir.path(),
            "image-viewer",
            &serde_json::json!({"kinds": ["*.png"]}),
        );
        viewer(
            installed_dir.path(),
            "older",
            &serde_json::json!({"kinds": ["*.png"]}),
        );
        viewer(
            dev_dir.path(),
            "image-viewer",
            &serde_json::json!({"kinds": ["*.png", "*.webp"]}),
        );
        let installed = BTreeMap::from([
            ("image-viewer".to_owned(), installed_at(1)),
            ("older".to_owned(), installed_at(2)),
        ]);
        let viewers = quick_view_viewers(Some(dev_dir.path()), installed_dir.path(), &installed);
        assert_eq!(ids(&viewers), ["image-viewer", "older"]);
        assert!(Path::new(&viewers[0].info.dir).starts_with(dev_dir.path()));
        assert_eq!(viewers[0].kinds, ["*.png", "*.webp"]);
    }

    #[test]
    fn pages_must_be_html_inside_the_folder() {
        for good in ["index.html", "pages/quick.htm", r"pages\quick.HTML"] {
            assert!(is_relative_page(good), "{good}");
        }
        for bad in [
            "",
            "/index.html",
            r"C:\x.html",
            "../x.html",
            "a//b.html",
            "./a.html",
            "x.js",
        ] {
            assert!(!is_relative_page(bad), "{bad}");
        }
    }
}
