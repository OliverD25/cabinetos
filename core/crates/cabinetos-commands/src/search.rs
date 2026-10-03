//! Ranking commands for the palette. The UI does no data processing
//! (brief §1), so the core scores what the user typed.
//!
//! A command matches when the query's letters appear in order (not
//! necessarily together, case-insensitive) in `Category: Title` or in the
//! command's ID; spaces in the query are ignored. Among all ways to match,
//! the best one counts:
//!
//! - each matched letter: +16;
//! - a letter at the start of a word (after a space, `:`, `.`, `-`, `_`, `/`,
//!   or a lower-to-upper case change as in `toggleDualPane`): +24;
//! - a letter right after the previous matched one: +20;
//! - each letter skipped between two matched ones: −3 (at most −30);
//! - each letter skipped before the first match: −1 (at most −15).
//!
//! Ties go to the shorter text, then to registry order.

use cabinetos_protocol::SearchHit;

use crate::registry::CommandRegistry;

const MATCH: i32 = 16;
const WORD_START: i32 = 24;
const CONSECUTIVE: i32 = 20;
const GAP: i32 = 3;
const MAX_GAP_PENALTY: i32 = 30;
const LEADING: i32 = 1;
const MAX_LEADING_PENALTY: i32 = 15;

/// The commands matching `query`, best first, at most `limit`. An empty
/// query returns every command in registry order, with score 0.
#[must_use]
pub fn search(registry: &CommandRegistry, query: &str, limit: usize) -> Vec<SearchHit> {
    let commands = registry.commands();
    rank(commands, query, |command| {
        vec![
            format!("{}: {}", command.category, command.title),
            command.id.clone(),
        ]
    })
    .into_iter()
    .take(limit)
    .map(|(index, score)| SearchHit {
        id: commands[index].id.clone(),
        score,
    })
    .collect()
}

/// Ranks any list the way the palette ranks commands, for other searches
/// that should feel the same (the marketplace). An item matches when the
/// query matches one of its `labels`, and its best label counts; ties go to
/// the shorter first label, then to the order of `items`. Returns the
/// indexes of the matching items with their scores, best first. An empty
/// query returns every index in order, with score 0.
pub fn rank<T>(items: &[T], query: &str, labels: impl Fn(&T) -> Vec<String>) -> Vec<(usize, i32)> {
    let query: Vec<char> = query
        .chars()
        .filter(|c| !c.is_whitespace())
        .flat_map(char::to_lowercase)
        .collect();
    if query.is_empty() {
        return (0..items.len()).map(|index| (index, 0)).collect();
    }
    let mut hits: Vec<(i32, usize, usize)> = items
        .iter()
        .enumerate()
        .filter_map(|(order, item)| {
            let labels = labels(item);
            let best = labels
                .iter()
                .filter_map(|label| score(&query, label))
                .max()?;
            let length = labels.first().map_or(0, |label| label.chars().count());
            Some((best, length, order))
        })
        .collect();
    hits.sort_by(|a, b| b.0.cmp(&a.0).then(a.1.cmp(&b.1)).then(a.2.cmp(&b.2)));
    hits.into_iter()
        .map(|(score, _, order)| (order, score))
        .collect()
}

/// The best score of `query` (lower case) as a subsequence of `text`, or
/// `None` if it is not one.
fn score(query: &[char], text: &str) -> Option<i32> {
    let chars: Vec<char> = text.chars().collect();
    let lower: Vec<char> = chars
        .iter()
        .map(|c| c.to_lowercase().next().unwrap_or(*c))
        .collect();
    let word_start: Vec<bool> = (0..chars.len())
        .map(|j| {
            j == 0 || {
                let (before, here) = (chars[j - 1], chars[j]);
                matches!(before, ' ' | ':' | '.' | '-' | '_' | '/')
                    || (before.is_lowercase() && here.is_uppercase())
            }
        })
        .collect();
    let gain = |j: usize| MATCH + if word_start[j] { WORD_START } else { 0 };

    // best[j]: the best score with the current query letter matched at j.
    let mut best: Vec<Option<i32>> = (0..chars.len())
        .map(|j| {
            let leading = i32::try_from(j).unwrap_or(i32::MAX).saturating_mul(LEADING);
            (lower[j] == query[0]).then(|| gain(j) - leading.min(MAX_LEADING_PENALTY))
        })
        .collect();
    for &letter in &query[1..] {
        let mut next = vec![None; chars.len()];
        for j in 0..chars.len() {
            if lower[j] != letter {
                continue;
            }
            let previous = (0..j)
                .filter_map(|k| {
                    let before = best[k]?;
                    let link = if k + 1 == j {
                        CONSECUTIVE
                    } else {
                        let skipped = i32::try_from(j - k - 1).unwrap_or(i32::MAX);
                        -(skipped.saturating_mul(GAP)).min(MAX_GAP_PENALTY)
                    };
                    Some(before + link)
                })
                .max();
            next[j] = previous.map(|previous| previous + gain(j));
        }
        best = next;
    }
    best.into_iter().flatten().max()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ids(query: &str) -> Vec<String> {
        search(&CommandRegistry::core(), query, 20)
            .into_iter()
            .map(|hit| hit.id)
            .collect()
    }

    #[test]
    fn a_word_finds_its_command_first() {
        assert_eq!(ids("dual")[0], "view.toggleDualPane");
        // The six sidebar commands lead; the five whose category is the
        // word itself come first.
        let sidebar = ids("sidebar");
        for id in [
            "sidebar.pin",
            "sidebar.unpin",
            "sidebar.lock",
            "sidebar.locate",
            "sidebar.toggleFollow",
            "view.toggleSidebar",
        ] {
            assert!(sidebar[..6].contains(&id.to_owned()), "{sidebar:?}");
        }
        assert_eq!(ids("toggle sidebar")[0], "view.toggleSidebar");
        // Every terminal command leads, the ones whose category is the word
        // first; toggling the panel comes right after them.
        let terminal = ids("terminal");
        let mut leading = terminal[..11].to_vec();
        leading.sort();
        assert_eq!(
            leading,
            [
                "terminal.close",
                "terminal.insertPath",
                "terminal.insertSelectedPaths",
                "terminal.new",
                "terminal.nextTab",
                "terminal.previousTab",
                "terminal.reload",
                "terminal.runTask",
                "terminal.setMode",
                "terminal.show",
                "terminal.toggleSplit",
            ]
        );
        assert_eq!(
            terminal[..5],
            [
                "terminal.runTask",
                "terminal.new",
                "terminal.show",
                "terminal.close",
                "terminal.reload"
            ]
        );
        // The layout command "Terminal on the Right" (Phase 23) also has the
        // word in its title; both come right after the terminal commands.
        assert!(
            terminal[11..13].contains(&"view.toggleTerminal".to_owned()),
            "{terminal:?}"
        );
        assert_eq!(ids("toggle term")[0], "view.toggleTerminal");
        assert_eq!(ids("about")[0], "help.about");
        assert_eq!(ids("plugins")[0], "plugins.list");
        assert_eq!(ids("markdown")[0], "editor.openMarkdownPreview");
    }

    #[test]
    fn initials_and_camel_case_match() {
        assert_eq!(ids("tdp")[0], "view.toggleDualPane");
        assert_eq!(ids("new fold")[0], "file.newFolder");
        assert_eq!(ids("keys")[0], "keys.open");
        assert_eq!(ids("rename")[0], "file.rename");
        assert_eq!(ids("back")[0], "go.back");
        assert_eq!(ids("delete perm")[0], "file.deletePermanently");
    }

    #[test]
    fn letters_out_of_order_do_not_match() {
        assert!(ids("zzq").is_empty());
        assert!(!ids("laud").contains(&"view.toggleDualPane".to_owned()));
    }

    #[test]
    fn scores_are_ranked_and_limited() {
        let hits = search(&CommandRegistry::core(), "o", 3);
        assert_eq!(hits.len(), 3);
        assert!(hits.windows(2).all(|pair| pair[0].score >= pair[1].score));
    }

    #[test]
    fn an_empty_query_lists_everything_in_order() {
        let hits = search(&CommandRegistry::core(), "  ", 1000);
        assert_eq!(hits.len(), CommandRegistry::core().commands().len());
        assert_eq!(hits[0].id, "palette.show");
        assert!(hits.iter().all(|hit| hit.score == 0));
    }

    #[test]
    fn any_list_ranks_like_the_palette() {
        let names = ["Rosé Pine Moon", "Nord", "Hello", "Nord Light"];
        let ranked = rank(&names, "nord", |name| vec![(*name).to_owned()]);
        assert_eq!(
            ranked.iter().map(|(index, _)| *index).collect::<Vec<_>>(),
            [1, 3]
        );
        assert!(ranked[0].1 > 0);
        // Only the second label has the `-`.
        let by_second_label = rank(&names, "-m", |name| {
            vec![(*name).to_owned(), name.to_lowercase().replace(' ', "-")]
        });
        assert_eq!(by_second_label.first().map(|(index, _)| *index), Some(0));
        assert_eq!(rank(&names, " ", |name| vec![(*name).to_owned()]).len(), 4);
    }

    #[test]
    fn word_starts_beat_letters_inside_words() {
        let start = score(&['p'], "Toggle Dual Pane").unwrap();
        let inside = score(&['p'], "Copy").unwrap();
        assert!(start > inside);
        let together = score(&['d', 'u'], "Dual").unwrap();
        let apart = score(&['d', 'u'], "Drop Up").unwrap();
        assert!(together > apart);
    }
}
