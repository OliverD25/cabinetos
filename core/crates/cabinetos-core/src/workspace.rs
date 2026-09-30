//! `workspace_info` (docs/ipc.md, "What the window shows"): which workspace
//! a folder belongs to, for the window's workspace pill and Quick Open.
//! Until workspaces exist it is the git repository that holds the folder.
//!
//! The window may not read files itself (brief §1, the Dumb UI Rule), so
//! the core looks for the repository: the nearest folder at or above the
//! asked one with a `.git` folder or file, and the branch its `HEAD` names.
//! It reads at most [`MAX_BYTES`] in all and never runs git.

use std::fs::File;
use std::io::Read;
use std::path::{Path, PathBuf};

use cabinetos_protocol::Response;

/// The most bytes read for one answer, a worktree's `.git` file and its
/// `HEAD` together. A branch line is far shorter.
pub(crate) const MAX_BYTES: u64 = 1024;

/// How much of a detached `HEAD`'s commit the pill shows, as git's own
/// short form does.
const SHORT_COMMIT: usize = 7;

const BRANCH_PREFIX: &str = "ref: refs/heads/";
const GITDIR_PREFIX: &str = "gitdir:";

/// The answer to `workspace_info` for an absolute `path`.
pub(crate) fn workspace_info(path: &str) -> Response {
    let (root, branch) = find(path);
    Response::WorkspaceInfo { root, branch }
}

/// The repository that holds `path`: its root and branch; outside one,
/// `path` itself (without a trailing backslash) and no branch.
fn find(path: &str) -> (String, Option<String>) {
    let start = without_trailing_separator(path);
    let mut folder = Some(Path::new(start));
    while let Some(current) = folder {
        let git = current.join(".git");
        if let Ok(metadata) = std::fs::metadata(&git) {
            let root = current.to_string_lossy().into_owned();
            let mut budget = MAX_BYTES;
            let git_dir = if metadata.is_dir() {
                Some(git)
            } else {
                linked_git_dir(current, &git, &mut budget)
            };
            let branch = git_dir.and_then(|dir| read_small(&dir.join("HEAD"), &mut budget));
            return (root, branch.as_deref().and_then(branch_from_head));
        }
        folder = current.parent();
    }
    (start.to_owned(), None)
}

/// `C:\repo\` is `C:\repo`; a drive's root keeps its backslash (`C:\`).
fn without_trailing_separator(path: &str) -> &str {
    let trimmed = path.trim_end_matches(['\\', '/']);
    if trimmed.is_empty() || trimmed.ends_with(':') {
        path
    } else {
        trimmed
    }
}

/// A worktree's or a submodule's `.git` is a file with one line,
/// `gitdir: <folder>`, that names the folder holding its `HEAD`; a relative
/// folder is relative to the one the file is in.
fn linked_git_dir(folder: &Path, git_file: &Path, budget: &mut u64) -> Option<PathBuf> {
    let text = read_small(git_file, budget)?;
    let target = text
        .lines()
        .next()?
        .trim()
        .strip_prefix(GITDIR_PREFIX)?
        .trim();
    if target.is_empty() {
        return None;
    }
    let target = Path::new(target);
    Some(if target.is_absolute() {
        target.to_path_buf()
    } else {
        folder.join(target)
    })
}

/// The branch a `HEAD` file names: the name after `ref: refs/heads/`, or
/// the short commit of a detached `HEAD` (40 or 64 hex digits).
fn branch_from_head(head: &str) -> Option<String> {
    let line = head.lines().next()?.trim();
    if let Some(name) = line.strip_prefix(BRANCH_PREFIX) {
        let name = name.trim();
        return (!name.is_empty()).then(|| name.to_owned());
    }
    (line.len() >= SHORT_COMMIT * 2 && line.bytes().all(|b| b.is_ascii_hexdigit()))
        .then(|| line[..SHORT_COMMIT].to_owned())
}

/// At most `budget` bytes of a small file, as text; the budget shrinks by
/// what was read. None when the file cannot be read or is not UTF-8.
fn read_small(path: &Path, budget: &mut u64) -> Option<String> {
    let mut bytes = Vec::new();
    let read = File::open(path)
        .ok()?
        .take(*budget)
        .read_to_end(&mut bytes)
        .ok()?;
    *budget = budget.saturating_sub(u64::try_from(read).unwrap_or(u64::MAX));
    String::from_utf8(bytes).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    const COMMIT: &str = "3f5a1c9e2b7d4f6a8c0e1b3d5f7a9c2e4b6d8f0a";

    fn text(path: &Path) -> String {
        path.to_string_lossy().into_owned()
    }

    fn repository(root: &Path, head: &str) -> PathBuf {
        let repo = root.join("repo");
        std::fs::create_dir_all(repo.join(".git")).unwrap();
        std::fs::write(repo.join(".git").join("HEAD"), head).unwrap();
        repo
    }

    #[test]
    fn a_branch_ref_names_the_branch_from_any_folder_inside() {
        let dir = tempfile::tempdir().unwrap();
        let repo = repository(dir.path(), "ref: refs/heads/phase-16\n");
        let deep = repo.join("src").join("ui");
        std::fs::create_dir_all(&deep).unwrap();

        let expected = (text(&repo), Some("phase-16".to_owned()));
        assert_eq!(find(&text(&deep)), expected);
        assert_eq!(find(&text(&repo)), expected);
        assert_eq!(find(&format!(r"{}\", text(&repo))), expected);
    }

    #[test]
    fn a_detached_head_shows_its_short_commit() {
        let dir = tempfile::tempdir().unwrap();
        let repo = repository(dir.path(), &format!("{COMMIT}\n"));
        assert_eq!(
            find(&text(&repo)),
            (text(&repo), Some("3f5a1c9".to_owned()))
        );
        // Anything else in HEAD names no branch.
        std::fs::write(repo.join(".git").join("HEAD"), "not a head").unwrap();
        assert_eq!(find(&text(&repo)), (text(&repo), None));
    }

    #[test]
    fn a_worktree_s_git_file_leads_to_its_own_head() {
        let dir = tempfile::tempdir().unwrap();
        let main = repository(dir.path(), "ref: refs/heads/main\n");
        let linked = main.join(".git").join("worktrees").join("feature");
        std::fs::create_dir_all(&linked).unwrap();
        std::fs::write(linked.join("HEAD"), "ref: refs/heads/feature\n").unwrap();

        // An absolute gitdir, as git writes it for a worktree.
        let worktree = dir.path().join("feature");
        std::fs::create_dir_all(worktree.join("docs")).unwrap();
        std::fs::write(
            worktree.join(".git"),
            format!("gitdir: {}\n", text(&linked)),
        )
        .unwrap();
        assert_eq!(
            find(&text(&worktree.join("docs"))),
            (text(&worktree), Some("feature".to_owned()))
        );

        // A relative gitdir, as git writes it for a submodule.
        let module = main.join(".git").join("modules").join("lib");
        std::fs::create_dir_all(&module).unwrap();
        std::fs::write(module.join("HEAD"), format!("{COMMIT}\n")).unwrap();
        let submodule = main.join("lib");
        std::fs::create_dir_all(&submodule).unwrap();
        std::fs::write(submodule.join(".git"), "gitdir: ../.git/modules/lib\n").unwrap();
        assert_eq!(
            find(&text(&submodule)),
            (text(&submodule), Some("3f5a1c9".to_owned()))
        );

        // A gitdir that points nowhere: the repository is there, its branch is not.
        std::fs::write(
            worktree.join(".git"),
            "gitdir: Z:\\gone\\.git\\worktrees\\x\n",
        )
        .unwrap();
        assert_eq!(find(&text(&worktree)), (text(&worktree), None));
    }

    #[test]
    fn outside_a_repository_the_folder_is_its_own_workspace_without_a_branch() {
        // The temp folder is not inside a repository on the machines that run the tests.
        let dir = tempfile::tempdir().unwrap();
        let plain = dir.path().join("plain");
        std::fs::create_dir_all(&plain).unwrap();
        assert_eq!(find(&text(&plain)), (text(&plain), None));
        assert_eq!(find(&format!(r"{}\", text(&plain))), (text(&plain), None));
        // A folder that is not there, and a drive's root, walk up without an error.
        let missing = plain.join("gone").join("deeper");
        assert_eq!(find(&text(&missing)), (text(&missing), None));
        assert_eq!(without_trailing_separator(r"C:\"), r"C:\");
    }

    #[test]
    fn at_most_1_kb_is_read() {
        let dir = tempfile::tempdir().unwrap();
        // A HEAD far longer than a branch line: only the first 1 KB is read, which holds no newline.
        let long = format!("ref: refs/heads/{}", "x".repeat(4096));
        let repo = repository(dir.path(), &long);
        let (_, branch) = find(&text(&repo));
        assert_eq!(branch.map(|b| b.len()), Some(1024 - BRANCH_PREFIX.len()));
        let mut budget = 10;
        assert_eq!(
            read_small(&repo.join(".git").join("HEAD"), &mut budget).as_deref(),
            Some("ref: refs/")
        );
        assert_eq!(budget, 0);
    }

    #[test]
    fn the_reply_carries_the_root_and_the_branch() {
        let dir = tempfile::tempdir().unwrap();
        let repo = repository(dir.path(), "ref: refs/heads/main\n");
        assert_eq!(
            workspace_info(&text(&repo)),
            Response::WorkspaceInfo {
                root: text(&repo),
                branch: Some("main".to_owned())
            }
        );
    }
}
