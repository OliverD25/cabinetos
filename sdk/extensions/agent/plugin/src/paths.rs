//! Windows paths as the model writes them and as the sandbox names them.
//! Plain string work: the plugin never resolves a path itself, it only
//! checks that one stays under the folders it was given.

/// `C:\a\b` as the sandbox names it: `/C:/a/b`.
#[must_use]
pub fn to_guest(path: &str) -> String {
    format!("/{}", path.replace('\\', "/"))
}

/// The Windows path of a sandbox path: `/C:/a/b` is `C:\a\b`.
#[must_use]
pub fn from_guest(guest: &str) -> String {
    guest.trim_start_matches('/').replace('/', "\\")
}

/// A path with a drive letter and a separator: `C:\` or `C:/…`. Relative
/// paths and network shares are refused: the model must be exact.
#[must_use]
pub fn is_absolute(path: &str) -> bool {
    let mut chars = path.chars();
    matches!(
        (chars.next(), chars.next(), chars.next()),
        (Some(drive), Some(':'), Some('\\' | '/')) if drive.is_ascii_alphabetic()
    )
}

/// `path` with `/` as `\`, doubled separators joined and no separator at
/// the end (except a drive's own root, `C:\`).
#[must_use]
pub fn normalize(path: &str) -> String {
    let mut out = String::with_capacity(path.len());
    for c in path.chars() {
        let c = if c == '/' { '\\' } else { c };
        if c == '\\' && out.ends_with('\\') {
            continue;
        }
        out.push(c);
    }
    while out.len() > 3 && out.ends_with('\\') {
        out.pop();
    }
    out
}

/// Whether some part of the path is `.` or `..`.
#[must_use]
pub fn has_dot_parts(path: &str) -> bool {
    path.split(['\\', '/'])
        .any(|part| part == ".." || part == ".")
}

/// Whether two paths name the same place, without regard to case, to
/// separators, or to a separator at the end.
#[must_use]
pub fn same(a: &str, b: &str) -> bool {
    normalize(a).eq_ignore_ascii_case(&normalize(b))
}

/// Whether `path` is `root` or lies under it, without regard to case.
#[must_use]
pub fn is_under(path: &str, root: &str) -> bool {
    let path = normalize(path).to_lowercase();
    let root = normalize(root).to_lowercase();
    if path == root {
        return true;
    }
    let prefix = if root.ends_with('\\') {
        root
    } else {
        format!("{root}\\")
    };
    path.starts_with(&prefix)
}

/// The path made plain, or the reason the agent may not touch it: it must
/// be absolute, have no `.` or `..` part, and lie under one of `roots`
/// (the folders the plugin was given).
pub fn check_allowed(path: &str, roots: &[String]) -> Result<String, String> {
    if !is_absolute(path) {
        return Err(format!(
            "{path}: give an absolute path with a drive letter, such as C:\\Users\\me"
        ));
    }
    if has_dot_parts(path) {
        return Err(format!("{path}: `.` and `..` are not allowed in a path"));
    }
    let plain = normalize(path);
    if roots.iter().any(|root| is_under(&plain, root)) {
        Ok(plain)
    } else {
        Err(format!(
            "{plain}: outside the folders you may use ({})",
            roots.join(", ")
        ))
    }
}

/// The folder a path is in; `None` for a drive's root.
#[must_use]
pub fn parent(path: &str) -> Option<String> {
    let path = normalize(path);
    let cut = path.rfind('\\')?;
    if cut < 3 {
        // `C:\x` has the root `C:\` as its folder.
        return (path.len() > 3).then(|| path[..=cut].to_owned());
    }
    Some(path[..cut].to_owned())
}

/// The last part of a path.
#[must_use]
pub fn file_name(path: &str) -> &str {
    path.trim_end_matches(['\\', '/'])
        .rsplit(['\\', '/'])
        .next()
        .unwrap_or_default()
}

/// `folder` and `name` joined with one separator.
#[must_use]
pub fn join(folder: &str, name: &str) -> String {
    format!("{}\\{name}", folder.trim_end_matches(['\\', '/']))
}

/// The extension of a file name without its dot, in lower case; empty for
/// none. A leading dot is part of the name (`.gitignore` has none).
#[must_use]
pub fn extension(name: &str) -> String {
    match name.rsplit_once('.') {
        Some((stem, ext)) if !stem.is_empty() && !ext.is_empty() => ext.to_lowercase(),
        _ => String::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_windows_path_and_its_sandbox_form_go_back_and_forth() {
        assert_eq!(to_guest(r"C:\Users\me"), "/C:/Users/me");
        assert_eq!(from_guest("/C:/Users/me"), r"C:\Users\me");
        assert_eq!(from_guest(&to_guest(r"D:\a b\c.txt")), r"D:\a b\c.txt");
    }

    #[test]
    fn only_a_drive_path_is_absolute() {
        assert!(is_absolute(r"C:\x"));
        assert!(is_absolute("d:/x"));
        assert!(is_absolute(r"C:\"));
        for bad in [
            "x",
            r"..\x",
            r"\x",
            r"\\server\share",
            "C:",
            "C:x",
            "1:\\x",
            "",
        ] {
            assert!(!is_absolute(bad), "{bad}");
        }
    }

    #[test]
    fn a_path_is_made_plain() {
        assert_eq!(normalize("C:/a//b\\\\c\\"), r"C:\a\b\c");
        assert_eq!(normalize(r"C:\"), r"C:\");
        assert_eq!(normalize(r"C:\\"), r"C:\");
    }

    #[test]
    fn dot_parts_are_found() {
        assert!(has_dot_parts(r"C:\a\..\b"));
        assert!(has_dot_parts(r"C:\a\.\b"));
        assert!(!has_dot_parts(r"C:\a\b..c\.hidden"));
    }

    #[test]
    fn under_is_by_whole_parts_and_ignores_case() {
        let root = r"C:\Users\Me";
        assert!(is_under(r"c:\users\me", root));
        assert!(is_under(r"C:\Users\Me\Pictures\a.jpg", root));
        assert!(is_under("C:/Users/Me/x", root));
        assert!(!is_under(r"C:\Users\Meow\a", root));
        assert!(!is_under(r"C:\Users", root));
        assert!(!is_under(r"D:\Users\Me", root));
        assert!(is_under(r"C:\anything", r"C:\"));
    }

    #[test]
    fn a_path_is_allowed_only_absolute_plain_and_under_a_root() {
        let roots = vec![r"C:\Users\Me".to_owned()];
        assert_eq!(
            check_allowed("c:/users/me//Pictures/", &roots).unwrap(),
            r"c:\users\me\Pictures"
        );
        assert!(
            check_allowed("Pictures", &roots)
                .unwrap_err()
                .contains("absolute")
        );
        assert!(
            check_allowed(r"C:\Users\Me\..\Other", &roots)
                .unwrap_err()
                .contains("`..`")
        );
        let outside = check_allowed(r"D:\x", &roots).unwrap_err();
        assert!(
            outside.contains("outside the folders you may use"),
            "{outside}"
        );
        assert!(outside.contains(r"C:\Users\Me"), "{outside}");
    }

    #[test]
    fn parts_of_a_path() {
        assert_eq!(parent(r"C:\a\b.txt").as_deref(), Some(r"C:\a"));
        assert_eq!(parent(r"C:\a").as_deref(), Some(r"C:\"));
        assert_eq!(parent(r"C:\"), None);
        assert_eq!(file_name(r"C:\a\b.txt"), "b.txt");
        assert_eq!(file_name(r"C:\a\"), "a");
        assert_eq!(join(r"C:\a\", "b"), r"C:\a\b");
        assert_eq!(join(r"C:\a", "b"), r"C:\a\b");
        assert_eq!(extension("Photo.JPG"), "jpg");
        assert_eq!(extension(".gitignore"), "");
        assert_eq!(extension("noext"), "");
        assert_eq!(extension("a.tar.gz"), "gz");
    }
}
