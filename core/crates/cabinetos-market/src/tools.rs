//! Tool Extensions as the core sees them: folders under the tools folder,
//! each with a `tool.json`. The Tool Dock (the UI) hosts them; the core only
//! installs, lists and removes them. It reads the keys below and leaves
//! every other key of `tool.json` to the Tool Dock.

use std::path::Path;

use cabinetos_protocol::{ToolInfo, extension_id_problem};
use serde::Deserialize;

use crate::parse_version;

/// The manifest file in a tool's folder.
pub const TOOL_MANIFEST_FILE: &str = "tool.json";

/// What the core reads of `tool.json`.
#[derive(Debug, Deserialize)]
pub(crate) struct ToolManifest {
    pub(crate) id: String,
    pub(crate) name: String,
    pub(crate) version: String,
    #[serde(default)]
    pub(crate) author: String,
    #[serde(default)]
    pub(crate) description: String,
}

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
    if manifest.name.trim().is_empty() {
        return Err(format!("{}: `name` is empty", path.display()));
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
            r#"{"id":"md-preview","name":"Markdown Preview","version":"1.0.0","entry":"index.html"}"#,
        );
        write(
            "other",
            r#"{"id":"md-preview","name":"X","version":"1.0.0"}"#,
        );
        write("broken", "{");
        fs::create_dir_all(dir.join("empty")).unwrap();
        let tools = list_tools(dir);
        assert_eq!(tools.len(), 1);
        assert_eq!(tools[0].id, "md-preview");
        assert_eq!(tools[0].name, "Markdown Preview");
        assert!(tools[0].author.is_empty());
        assert!(tools[0].dir.ends_with("md-preview"));
        assert!(list_tools(&dir.join("nothing")).is_empty());
    }
}
