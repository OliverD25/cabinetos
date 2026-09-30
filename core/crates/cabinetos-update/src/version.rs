//! Versions as semantic versioning compares them: `major.minor.patch`, and
//! a pre-release tag after `-` that sorts before the release itself
//! (`0.2.0-preview.1` < `0.2.0-preview.2` < `0.2.0`). Build metadata after
//! `+` is ignored.

use std::cmp::Ordering;
use std::fmt;

/// A semantic version.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Version {
    major: u64,
    minor: u64,
    patch: u64,
    pre: Vec<Identifier>,
}

/// One dot-separated part of a pre-release tag.
#[derive(Clone, Debug, PartialEq, Eq)]
enum Identifier {
    Number(u64),
    Text(String),
}

impl Ord for Identifier {
    fn cmp(&self, other: &Self) -> Ordering {
        match (self, other) {
            (Self::Number(a), Self::Number(b)) => a.cmp(b),
            // Numbers sort before words (semantic versioning, rule 11).
            (Self::Number(_), Self::Text(_)) => Ordering::Less,
            (Self::Text(_), Self::Number(_)) => Ordering::Greater,
            (Self::Text(a), Self::Text(b)) => a.cmp(b),
        }
    }
}

impl PartialOrd for Identifier {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Version {
    /// Reads `1.2.3`, `1.2.3-preview.1` or `1.2.3+build`; `None` for
    /// anything else.
    #[must_use]
    pub fn parse(text: &str) -> Option<Self> {
        let text = text.split_once('+').map_or(text, |(version, _)| version);
        let (release, pre) = match text.split_once('-') {
            Some((release, pre)) => (release, Some(pre)),
            None => (text, None),
        };
        let mut numbers = release.split('.').map(number);
        let (major, minor, patch) = (numbers.next()??, numbers.next()??, numbers.next()??);
        if numbers.next().is_some() {
            return None;
        }
        let pre = match pre {
            None => Vec::new(),
            Some(pre) => pre
                .split('.')
                .map(|part| {
                    if part.is_empty()
                        || !part.chars().all(|c| c.is_ascii_alphanumeric() || c == '-')
                    {
                        None
                    } else if part.chars().all(|c| c.is_ascii_digit()) {
                        part.parse().ok().map(Identifier::Number)
                    } else {
                        Some(Identifier::Text(part.to_owned()))
                    }
                })
                .collect::<Option<Vec<_>>>()?,
        };
        Some(Self {
            major,
            minor,
            patch,
            pre,
        })
    }

    /// Whether it carries a pre-release tag, which only the preview channel
    /// offers.
    #[must_use]
    pub fn is_prerelease(&self) -> bool {
        !self.pre.is_empty()
    }
}

/// One of `major.minor.patch`: digits only, at least one.
fn number(part: &str) -> Option<u64> {
    if part.is_empty() || !part.chars().all(|c| c.is_ascii_digit()) {
        return None;
    }
    part.parse().ok()
}

impl Ord for Version {
    fn cmp(&self, other: &Self) -> Ordering {
        (self.major, self.minor, self.patch)
            .cmp(&(other.major, other.minor, other.patch))
            .then_with(|| match (self.pre.is_empty(), other.pre.is_empty()) {
                (true, true) => Ordering::Equal,
                // A release sorts after its pre-releases.
                (true, false) => Ordering::Greater,
                (false, true) => Ordering::Less,
                (false, false) => self.pre.cmp(&other.pre),
            })
    }
}

impl PartialOrd for Version {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl fmt::Display for Version {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}.{}.{}", self.major, self.minor, self.patch)?;
        for (position, part) in self.pre.iter().enumerate() {
            f.write_str(if position == 0 { "-" } else { "." })?;
            match part {
                Identifier::Number(number) => write!(f, "{number}")?,
                Identifier::Text(text) => f.write_str(text)?,
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn v(text: &str) -> Version {
        Version::parse(text).unwrap_or_else(|| panic!("{text} should parse"))
    }

    #[test]
    fn releases_compare_by_number_not_by_text() {
        assert!(v("0.10.0") > v("0.9.9"));
        assert!(v("1.0.0") > v("0.99.99"));
        assert_eq!(v("0.1.0"), v("0.1.0+build.7"));
        assert_eq!(v("0.1.0").cmp(&v("0.1.0")), Ordering::Equal);
    }

    #[test]
    fn a_pre_release_sorts_before_its_release() {
        let ordered = [
            "0.2.0-alpha",
            "0.2.0-alpha.1",
            "0.2.0-alpha.beta",
            "0.2.0-preview.1",
            "0.2.0-preview.2",
            "0.2.0-preview.10",
            "0.2.0-rc.1",
            "0.2.0",
            "0.2.1-preview.1",
        ];
        for pair in ordered.windows(2) {
            assert!(v(pair[0]) < v(pair[1]), "{} < {}", pair[0], pair[1]);
        }
        assert!(v("0.2.0-preview.1").is_prerelease());
        assert!(!v("0.2.0").is_prerelease());
        assert_eq!(v("0.2.0-preview.1").to_string(), "0.2.0-preview.1");
    }

    #[test]
    fn anything_else_is_refused() {
        for bad in [
            "",
            "1",
            "1.0",
            "1.0.0.0",
            "v1.0.0",
            "1.x.0",
            "1.0.0-",
            "1.0.0-a..b",
            "1.0.0-a_b",
            " 1.0.0",
        ] {
            assert_eq!(Version::parse(bad), None, "{bad:?}");
        }
    }
}
