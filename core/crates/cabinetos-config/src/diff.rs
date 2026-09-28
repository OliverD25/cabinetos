//! Which settings changed between two configurations, as dotted paths.

use serde_json::Value;

use crate::Config;

/// The settings that differ, as dotted paths in file order, such as
/// `ui.layout`, `panes.sort` or `keybindings`. Objects are compared member by
/// member; an array or a plain value is one setting. `$schema` is not a
/// setting.
#[must_use]
pub fn changed_paths(old: &Config, new: &Config) -> Vec<String> {
    let (Ok(old), Ok(new)) = (serde_json::to_value(old), serde_json::to_value(new)) else {
        return Vec::new();
    };
    let mut changed = Vec::new();
    compare("", &old, &new, &mut changed);
    changed.retain(|path| path != "$schema");
    changed
}

fn compare(path: &str, old: &Value, new: &Value, changed: &mut Vec<String>) {
    match (old, new) {
        (Value::Object(old_members), Value::Object(new_members)) => {
            let mut keys: Vec<&String> = new_members.keys().collect();
            keys.extend(
                old_members
                    .keys()
                    .filter(|key| !new_members.contains_key(*key)),
            );
            for key in keys {
                let child = if path.is_empty() {
                    key.clone()
                } else {
                    format!("{path}.{key}")
                };
                match (old_members.get(key), new_members.get(key)) {
                    (Some(old_value), Some(new_value)) => {
                        compare(&child, old_value, new_value, changed);
                    }
                    _ => changed.push(child),
                }
            }
        }
        _ if old != new => changed.push(path.to_owned()),
        _ => {}
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{KeybindingEntry, Keys, Layout};

    #[test]
    fn names_each_changed_setting() {
        let old = Config::default();
        let mut new = old.clone();
        assert!(changed_paths(&old, &new).is_empty());

        new.ui.layout = Layout::Rail;
        new.panes.sort.descending = true;
        new.keybindings.push(KeybindingEntry {
            command: "view.toggleSidebar".to_owned(),
            keys: Keys(Some("ctrl+alt+b".parse().unwrap())),
            when: None,
        });
        new.terminal.profiles.pop();
        new.schema = Some("./cabinetos.schema.json".to_owned());
        let mut changed = changed_paths(&old, &new);
        changed.sort();
        assert_eq!(
            changed,
            [
                "keybindings",
                "panes.sort.descending",
                "terminal.profiles",
                "ui.layout"
            ]
        );
    }
}
