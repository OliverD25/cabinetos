//! The swap. Every file of the install folder is moved into `previous\`
//! inside it (Windows lets a running program's files be renamed on the
//! same volume), then the new version's files are copied in. When a move or
//! a copy fails half way, everything goes back where it was, so a failed
//! swap leaves the old version whole. `previous\` stays until the next swap
//! or a rollback; a rollback is the same swap the other way round.
//!
//! What a swap replaces goes into `previous-old\` first and is deleted at
//! once where it can be: the files of a program that still runs cannot be
//! deleted, and go at the next start.
//!
//! The setup file's uninstaller (`unins000.exe` and its `unins000.dat`,
//! Inno Setup's) belongs to the install, not to a version: it stays where
//! it is through a swap and a rollback, so Settings > Apps can always
//! remove the install.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use crate::install::{RECORD_FILE, read_release, strip_bom};

/// The folder in the install folder that keeps the version before the last
/// swap, for a rollback.
pub const PREVIOUS_DIR: &str = "previous";

/// The folder in the install folder for what a swap replaced, until it can
/// be deleted.
pub const DISCARD_DIR: &str = "previous-old";

/// A release's files that are never installed: the installer runs from the
/// unpacked zip only, as `install.ps1` itself does it.
const NOT_INSTALLED: &[&str] = &["install.ps1"];

/// Each file moved, from and to, in order.
type Journal = Vec<(PathBuf, PathBuf)>;

/// What a swap put in place.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Placed {
    /// The files, relative to the install folder, with `\` between names.
    pub files: Vec<String>,
    /// Their sizes together.
    pub bytes: u64,
}

/// Puts the release unpacked in `staged` into `install`, which holds the
/// running version: the running version's files go into `previous\`, the
/// new ones are copied in, and the installer's record is carried over.
pub fn apply(install: &Path, staged: &Path) -> Result<Placed, String> {
    let version = read_release(staged)?.version;
    let (files, bytes) = release_files(staged)?;
    let previous = install.join(PREVIOUS_DIR);
    let discard = install.join(DISCARD_DIR);
    clear(&discard)?;
    let had_previous = previous.exists();
    if had_previous {
        fs::rename(&previous, &discard).map_err(|error| {
            format!("cannot move {} out of the way: {error}", previous.display())
        })?;
    }
    let undo_all = |moved: &Journal, copied: &[PathBuf], problem: String| {
        let problems = put_back(install, &previous, &discard, had_previous, moved, copied);
        failure(&problem, &problems)
    };

    let mut moved = Journal::new();
    if let Err(error) = move_tree(install, &previous, &stays_in_place, &mut moved) {
        return Err(undo_all(
            &moved,
            &[],
            format!(
                "cannot move the running version into {}: {error}",
                previous.display()
            ),
        ));
    }
    tracing::info!(files = moved.len(), to = %previous.display(), "the running version is moved aside");

    let mut copied = Vec::new();
    for relative in &files {
        let (from, to) = (staged.join(relative), install.join(relative));
        let result = to
            .parent()
            .map_or(Ok(()), fs::create_dir_all)
            .and_then(|()| fs::copy(&from, &to).map(|_| ()));
        if let Err(error) = result {
            return Err(undo_all(
                &moved,
                &copied,
                format!("cannot copy {relative} into {}: {error}", install.display()),
            ));
        }
        copied.push(to);
    }
    if let Err(problem) = carry_record(&previous, install, &files, &version) {
        return Err(undo_all(&moved, &copied, problem));
    }
    tracing::info!(
        files = copied.len(),
        bytes,
        version,
        "the new version is in place"
    );
    remove_now_or_later(&discard);
    Ok(Placed { files, bytes })
}

/// Brings the version in `previous\` back: the files in the install folder
/// go into `previous-old\`, those of `previous\` come back, and
/// `previous\` is gone. Returns the version back in place.
pub fn rollback(install: &Path) -> Result<String, String> {
    let previous = install.join(PREVIOUS_DIR);
    let discard = install.join(DISCARD_DIR);
    let version = read_release(&previous)?.version;
    clear(&discard)?;

    let mut out = Journal::new();
    if let Err(error) = move_tree(install, &discard, &stays_in_place, &mut out) {
        let problems = undo(&out);
        if problems.is_empty() {
            let _ = fs::remove_dir_all(&discard);
        }
        return Err(failure(
            &format!("cannot move the running version aside: {error}"),
            &problems,
        ));
    }
    let mut back = Journal::new();
    if let Err(error) = move_tree(&previous, install, &|_| false, &mut back) {
        let mut problems = undo(&back);
        problems.extend(undo(&out));
        return Err(failure(
            &format!(
                "cannot move {version} back from {}: {error}",
                previous.display()
            ),
            &problems,
        ));
    }
    // Only the folders the moves emptied are left in it.
    if let Err(error) = fs::remove_dir_all(&previous) {
        tracing::warn!(dir = %previous.display(), %error, "cannot remove the emptied folder of the previous version");
    }
    tracing::info!(
        files = back.len(),
        version,
        "the previous version is back in place"
    );
    remove_now_or_later(&discard);
    Ok(version)
}

/// Removes what an earlier swap left in `previous-old\`: files of a program
/// that ran then, which can go now. Best effort, at each start.
pub fn clean_up(install: &Path) {
    let discard = install.join(DISCARD_DIR);
    if discard.exists() {
        remove_now_or_later(&discard);
    }
}

/// The size of the files in the install folder, `previous\` and
/// `previous-old\` left out: what Settings > Apps shows.
#[must_use]
pub fn install_size(install: &Path) -> u64 {
    fn walk(dir: &Path, skip: &[&str]) -> u64 {
        let Ok(entries) = fs::read_dir(dir) else {
            return 0;
        };
        entries
            .flatten()
            .filter(|entry| {
                let name = entry.file_name();
                !skip
                    .iter()
                    .any(|skipped| name.to_string_lossy().eq_ignore_ascii_case(skipped))
            })
            .map(|entry| match entry.file_type() {
                Ok(kind) if kind.is_dir() => walk(&entry.path(), &[]),
                Ok(_) => entry.metadata().map_or(0, |metadata| metadata.len()),
                Err(_) => 0,
            })
            .sum()
    }
    walk(install, &[PREVIOUS_DIR, DISCARD_DIR])
}

/// Every file of the release in `staged`, relative, sorted, and their size
/// together; the installer and the installer's record are left out.
fn release_files(staged: &Path) -> Result<(Vec<String>, u64), String> {
    fn walk(root: &Path, dir: &Path, files: &mut Vec<String>, bytes: &mut u64) -> io::Result<()> {
        for entry in fs::read_dir(dir)? {
            let entry = entry?;
            let path = entry.path();
            if entry.file_type()?.is_dir() {
                walk(root, &path, files, bytes)?;
            } else {
                let relative = path
                    .strip_prefix(root)
                    .map_err(io::Error::other)?
                    .display()
                    .to_string();
                let top = dir == root;
                let name = entry.file_name().to_string_lossy().into_owned();
                if top
                    && (NOT_INSTALLED
                        .iter()
                        .any(|skipped| name.eq_ignore_ascii_case(skipped))
                        || name.eq_ignore_ascii_case(RECORD_FILE))
                {
                    continue;
                }
                *bytes += entry.metadata()?.len();
                files.push(relative);
            }
        }
        Ok(())
    }
    let mut files = Vec::new();
    let mut bytes = 0;
    walk(staged, staged, &mut files, &mut bytes).map_err(|error| {
        format!(
            "cannot read the staged release {}: {error}",
            staged.display()
        )
    })?;
    files.sort();
    Ok((files, bytes))
}

/// The entries at the top of the install folder that no swap moves: the
/// version kept for a rollback, what a swap replaced, and the setup file's
/// uninstaller (`unins000.exe`, `unins000.dat`, and `unins000.msg` where
/// Inno Setup writes one; the number grows when another setup used it).
fn stays_in_place(name: &str) -> bool {
    let name = name.to_ascii_lowercase();
    if name == PREVIOUS_DIR || name == DISCARD_DIR {
        return true;
    }
    let bytes = name.as_bytes();
    bytes.len() == 12
        && name.starts_with("unins")
        && bytes[5..8].iter().all(u8::is_ascii_digit)
        && matches!(&name[8..], ".exe" | ".dat" | ".msg")
}

/// Moves every file under `from` to the same place under `to`, except the
/// entries at the top of `from` that `skip` names, and records each move;
/// folders it empties are removed. On an error, the journal holds the moves
/// done so far.
fn move_tree(
    from: &Path,
    to: &Path,
    skip: &dyn Fn(&str) -> bool,
    journal: &mut Journal,
) -> io::Result<()> {
    fs::create_dir_all(to)?;
    let mut entries = fs::read_dir(from)?.collect::<io::Result<Vec<_>>>()?;
    entries.sort_by_key(fs::DirEntry::file_name);
    for entry in entries {
        let name = entry.file_name();
        if skip(&name.to_string_lossy()) {
            continue;
        }
        let (source, target) = (entry.path(), to.join(&name));
        if entry.file_type()?.is_dir() {
            move_tree(&source, &target, &|_| false, journal)?;
            // Fails while something is left in it, which then stays.
            let _ = fs::remove_dir(&source);
        } else {
            fs::rename(&source, &target).map_err(|error| {
                io::Error::new(error.kind(), format!("{}: {error}", source.display()))
            })?;
            journal.push((source, target));
        }
    }
    Ok(())
}

/// Moves the journal's files back, the last one first. Returns what could
/// not be moved back.
fn undo(journal: &[(PathBuf, PathBuf)]) -> Vec<String> {
    let mut problems = Vec::new();
    for (from, to) in journal.iter().rev() {
        if let Some(parent) = from.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if let Err(error) = fs::rename(to, from) {
            problems.push(format!(
                "{} stays in {}: {error}",
                from.display(),
                to.display()
            ));
        }
    }
    problems
}

/// Undoes a swap that failed half way: removes the files copied, moves the
/// running version's files back from `previous\`, and puts the earlier
/// `previous\` back. Returns what could not be undone.
fn put_back(
    install: &Path,
    previous: &Path,
    discard: &Path,
    had_previous: bool,
    moved: &Journal,
    copied: &[PathBuf],
) -> Vec<String> {
    let mut problems = Vec::new();
    for file in copied.iter().rev() {
        match fs::remove_file(file) {
            Ok(()) => {}
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => {
                problems.push(format!("cannot remove the new {}: {error}", file.display()));
            }
        }
        // The folders the copy made, now empty; one the old version had
        // comes back with its files.
        let mut parent = file.parent();
        while let Some(folder) = parent.filter(|folder| *folder != install) {
            if fs::remove_dir(folder).is_err() {
                break;
            }
            parent = folder.parent();
        }
    }
    problems.extend(undo(moved));
    if problems.is_empty() {
        // Only the folders the moves emptied are left in it.
        if let Err(error) = fs::remove_dir_all(previous) {
            problems.push(format!("cannot remove {}: {error}", previous.display()));
        }
    }
    if had_previous
        && problems.is_empty()
        && let Err(error) = fs::rename(discard, previous)
    {
        problems.push(format!(
            "the version kept for a rollback stays in {}: {error}",
            discard.display()
        ));
    }
    if problems.is_empty() {
        tracing::info!(dir = %install.display(), "the swap was undone; the running version is whole");
    }
    problems
}

/// The message of a failed swap, with what could not be undone.
fn failure(problem: &str, problems: &[String]) -> String {
    if problems.is_empty() {
        format!("{problem}; nothing was changed")
    } else {
        tracing::error!(problems = ?problems, "a failed swap could not be undone completely");
        format!(
            "{problem}; and it could not be undone completely: {}. Install the release again with install.ps1",
            problems.join("; ")
        )
    }
}

/// Carries the installer's record over from `previous\`: the same install,
/// with the new version and its files, so `uninstall.ps1` removes what is
/// there now. An install without a record (copied by hand, or by winget)
/// gets none.
fn carry_record(
    previous: &Path,
    install: &Path,
    files: &[String],
    version: &str,
) -> Result<(), String> {
    let Ok(bytes) = fs::read(previous.join(RECORD_FILE)) else {
        return Ok(());
    };
    let mut record: serde_json::Value = serde_json::from_slice(strip_bom(&bytes))
        .map_err(|error| format!("the installer's record {RECORD_FILE} cannot be read: {error}"))?;
    let Some(fields) = record.as_object_mut() else {
        return Err(format!(
            "the installer's record {RECORD_FILE} is not an object"
        ));
    };
    fields.insert("version".to_owned(), version.into());
    fields.insert("files".to_owned(), files.into());
    let text = serde_json::to_string_pretty(&record).map_err(|error| error.to_string())?;
    fs::write(install.join(RECORD_FILE), text)
        .map_err(|error| format!("cannot write the installer's record: {error}"))
}

/// Removes `dir` if it is there; what cannot go yet (a running program's
/// files) goes at the next start.
fn clear(dir: &Path) -> Result<(), String> {
    match fs::remove_dir_all(dir) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(format!(
            "cannot remove {}, which an earlier update left ({error}); close every CabinetOS window started before that update, and try again",
            dir.display()
        )),
    }
}

fn remove_now_or_later(dir: &Path) {
    if let Err(error) = fs::remove_dir_all(dir)
        && error.kind() != io::ErrorKind::NotFound
    {
        tracing::debug!(dir = %dir.display(), %error, "what the swap replaced goes at the next start");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_kept_folders_and_the_setup_uninstaller_stay_in_place() {
        for name in [
            "previous",
            "Previous-Old",
            "unins000.exe",
            "UNINS000.DAT",
            "unins001.msg",
        ] {
            assert!(stays_in_place(name), "{name}");
        }
        for name in [
            "CabinetOS.exe",
            "uninstall.ps1",
            "unins000.exe.bak",
            "unins00x.exe",
            "unins0000.exe",
            "unins000.dll",
        ] {
            assert!(!stays_in_place(name), "{name}");
        }
    }
}
