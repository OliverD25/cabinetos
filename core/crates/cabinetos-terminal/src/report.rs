//! The folder reports of a shell's prompt hook (`hook.rs`): `ESC ] 9 ; 9 ;
//! <folder> ESC \` (OSC 9;9, what Windows Terminal reads), read out of
//! the output the session's output thread already sees. The bytes stay in
//! the stream; xterm.js ignores the sequence.

/// The longest folder a report may carry, in bytes: a Windows path has at
/// most 32,767 UTF-16 units, three UTF-8 bytes each at worst.
const MAX_REPORT: usize = 3 * 32_767;

/// Reads the folder reports (OSC 9;9) out of a shell's output, across
/// reads: a report may arrive split in any place. Anything else passes by;
/// a report that is not closed within [`MAX_REPORT`] bytes, or that is
/// not valid UTF-8, is dropped.
#[derive(Debug, Default)]
pub(crate) struct FolderReports {
    state: Scan,
    payload: Vec<u8>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
enum Scan {
    /// Outside any sequence.
    #[default]
    Text,
    /// After `ESC`.
    Escape,
    /// Inside `ESC ]`: the bytes go to `payload`.
    Osc,
    /// After `ESC` inside an OSC: `\` ends it.
    OscEscape,
    /// Inside an OSC that is too long: skipped to its end.
    Skip,
    /// After `ESC` inside a skipped OSC.
    SkipEscape,
}

impl FolderReports {
    /// Reads `bytes`; returns the folders reported in them, oldest first.
    // The match is a transition table, one line per state and byte; merging the arms that land in the same
    // state would hide which states exist.
    #[allow(clippy::match_same_arms)]
    pub(crate) fn read(&mut self, bytes: &[u8]) -> Vec<String> {
        let mut folders = Vec::new();
        let mut rest = bytes;
        while !rest.is_empty() {
            if self.state == Scan::Text {
                // The common case: plain output, no escape in this chunk.
                let Some(at) = rest.iter().position(|&byte| byte == 0x1b) else {
                    return folders;
                };
                rest = &rest[at + 1..];
                self.state = Scan::Escape;
                continue;
            }
            let byte = rest[0];
            rest = &rest[1..];
            self.state = match (self.state, byte) {
                (Scan::Escape, b']') => {
                    self.payload.clear();
                    Scan::Osc
                }
                (Scan::Escape, 0x1b) => Scan::Escape,
                (Scan::Escape, _) => Scan::Text,
                (Scan::Osc, 0x07) => {
                    folders.extend(self.finish());
                    Scan::Text
                }
                (Scan::Osc, 0x1b) => Scan::OscEscape,
                (Scan::Osc, _) if self.payload.len() >= MAX_REPORT => Scan::Skip,
                (Scan::Osc, _) => {
                    self.payload.push(byte);
                    Scan::Osc
                }
                (Scan::OscEscape, b'\\') => {
                    folders.extend(self.finish());
                    Scan::Text
                }
                // An escape that does not end the sequence starts another.
                (Scan::OscEscape | Scan::SkipEscape, b']') => {
                    self.payload.clear();
                    Scan::Osc
                }
                (Scan::OscEscape | Scan::SkipEscape, 0x1b) => Scan::Escape,
                (Scan::OscEscape | Scan::SkipEscape, _) => Scan::Text,
                (Scan::Skip, 0x07) => Scan::Text,
                (Scan::Skip, 0x1b) => Scan::SkipEscape,
                (Scan::Skip, _) => Scan::Skip,
                (Scan::Text, _) => unreachable!("handled above"),
            };
        }
        folders
    }

    /// The folder of a finished OSC, when it is a report: `9;9;` and a
    /// folder, maybe in double quotes (as Windows Terminal's examples
    /// write it; a Windows path cannot contain `"`).
    fn finish(&mut self) -> Option<String> {
        let payload = std::mem::take(&mut self.payload);
        let folder = payload.strip_prefix(b"9;9;")?;
        let folder = std::str::from_utf8(folder).ok()?;
        let folder = folder
            .strip_prefix('"')
            .and_then(|inner| inner.strip_suffix('"'))
            .unwrap_or(folder);
        (!folder.is_empty() && !folder.chars().any(char::is_control)).then(|| folder.to_owned())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn report(folder: &str) -> Vec<u8> {
        format!("\x1b]9;9;{folder}\x1b\\").into_bytes()
    }

    #[test]
    fn a_report_is_read_whole_or_split_anywhere() {
        let folder = r"D:\Звіт 'проєкт' $HOME ’q’ 100%PATH% 日本 📁";
        let mut stream = b"PS C:\\> echo hi\r\nhi\r\n".to_vec();
        stream.extend(report(folder));
        stream.extend(b"\x1b[32mPS ");
        stream.extend(format!("{folder}> ").as_bytes());
        let mut whole = FolderReports::default();
        assert_eq!(whole.read(&stream), [folder]);
        for split in 0..=stream.len() {
            let mut reports = FolderReports::default();
            let mut found = reports.read(&stream[..split]);
            found.extend(reports.read(&stream[split..]));
            assert_eq!(found, [folder], "split at {split}");
        }
        // One byte at a time.
        let mut reports = FolderReports::default();
        let found: Vec<String> = stream
            .iter()
            .flat_map(|byte| reports.read(&[*byte]))
            .collect();
        assert_eq!(found, [folder]);
    }

    #[test]
    fn bel_ends_a_report_too_and_quotes_around_the_folder_are_dropped() {
        let mut reports = FolderReports::default();
        let stream = b"\x1b]9;9;C:\\a b\x07x\x1b]9;9;\"E:\\in quotes\"\x1b\\\x1b]9;9;\\\\wsl.localhost\\Ubuntu\\home\\me\x07";
        assert_eq!(
            reports.read(stream),
            [
                r"C:\a b",
                r"E:\in quotes",
                r"\\wsl.localhost\Ubuntu\home\me"
            ]
        );
    }

    #[test]
    fn other_and_malformed_sequences_are_ignored() {
        let mut reports = FolderReports::default();
        // The window title (OSC 0), a cursor move, an OSC 9;4 progress report,
        // an empty report, a report with a control character, bytes that are
        // not UTF-8, and an escape that breaks a report off.
        let stream = b"\x1b]0;C:\\Windows\\pwsh.exe\x07\x1b[2J\x1b]9;4;1;50\x1b\\\x1b]9;9;\x1b\\\
\x1b]9;9;C:\\a\tb\x07\x1b]9;9;C:\\\xff\xfe\x07\x1b]9;9;C:\\broken\x1b[0m\x1b]9;9;C:\\good\x1b\\";
        assert_eq!(reports.read(stream), [r"C:\good"]);
        // An escape inside a report that starts a new OSC.
        assert_eq!(
            reports.read(b"\x1b]9;9;C:\\cut\x1b\x1b]9;9;C:\\next\x07"),
            [r"C:\next"]
        );
    }

    #[test]
    fn a_report_that_never_ends_is_dropped_without_growing() {
        let mut reports = FolderReports::default();
        let mut stream = b"\x1b]9;9;C:\\".to_vec();
        stream.extend(std::iter::repeat_n(b'a', MAX_REPORT + 10));
        assert_eq!(reports.read(&stream), Vec::<String>::new());
        assert!(reports.payload.len() <= MAX_REPORT);
        assert_eq!(reports.read(b"\x07"), Vec::<String>::new());
        assert_eq!(reports.read(&report(r"C:\after")), [r"C:\after"]);
    }
}
