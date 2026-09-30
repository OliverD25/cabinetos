//! Where a channel's `latest.json` is, reading it, and checking it.
//!
//! `update.source` names a folder: `<source>/<channel>/latest.json` is the
//! file, and a relative address in it (the zip, the notes) is relative to
//! that file. The source is an `https:` URL (plain `http:` only with
//! `update.allowInsecure`), a `file:` URL, or an absolute path.

use crate::version::Version;
use cabinetos_market::transfer::{self, Base, Client, Fetched, Http, Location};
use cabinetos_protocol::{UPDATE_SCHEMA_VERSION, UpdateChannel, UpdateRelease};

/// The file of a channel.
pub const LATEST_FILE: &str = "latest.json";

/// The most bytes `latest.json` may have.
const MAX_LATEST_BYTES: u64 = 64 * 1024;

/// The most bytes the release notes may have.
pub const MAX_NOTES_BYTES: u64 = 256 * 1024;

/// A channel's `latest.json`: where it is, and what its relative addresses
/// are relative to.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Latest {
    /// The file.
    pub location: Location,
    /// What a relative address in it is relative to.
    pub base: Base,
}

impl Latest {
    /// The address, for messages and to tell a cached tag's file.
    pub fn address(&self) -> String {
        match &self.location {
            Location::File(path) => path.display().to_string(),
            Location::Web(url) => url.to_string(),
        }
    }
}

/// Where `<source>/<channel>/latest.json` is.
pub fn locate_latest(
    source: &str,
    channel: UpdateChannel,
    allow_insecure: bool,
) -> Result<Latest, String> {
    let source = source.trim();
    if source.is_empty() {
        return Err("update.source is empty".to_owned());
    }
    let channel = channel.name();
    match transfer::parse_location(source, allow_insecure, Client::Update)? {
        Location::File(folder) => {
            if !folder.is_absolute() {
                return Err(format!(
                    "update.source `{}` must be an absolute path or a URL",
                    folder.display()
                ));
            }
            let folder = folder.join(channel);
            Ok(Latest {
                location: Location::File(folder.join(LATEST_FILE)),
                base: Base::Folder(folder),
            })
        }
        Location::Web(mut url) => {
            if !url.path().ends_with('/') {
                let path = format!("{}/", url.path());
                url.set_path(&path);
            }
            let file = url
                .join(&format!("{channel}/{LATEST_FILE}"))
                .map_err(|error| {
                    format!("update.source `{source}` is not a folder URL: {error}")
                })?;
            Ok(Latest {
                location: Location::Web(file.clone()),
                base: Base::Web(file),
            })
        }
    }
}

/// What reading `latest.json` brought.
#[derive(Debug)]
pub enum Read {
    /// The server confirmed the tag sent: the copy the updater keeps is
    /// current.
    NotModified,
    /// The file, with its tag.
    Whole {
        /// The file, checked.
        release: Box<UpdateRelease>,
        /// The server's `ETag`.
        etag: Option<String>,
    },
}

/// Reads `latest.json`, with `If-None-Match` when a tag is known, and
/// checks it for `channel`.
pub fn read_latest(
    http: &Http,
    latest: &Latest,
    tag: Option<&str>,
    channel: UpdateChannel,
    allow_insecure: bool,
) -> Result<Read, String> {
    let (bytes, etag) = match &latest.location {
        Location::File(path) => (
            transfer::read_limited(path, MAX_LATEST_BYTES, LATEST_FILE)?,
            None,
        ),
        Location::Web(url) => match transfer::get(
            http,
            url,
            tag,
            allow_insecure,
            MAX_LATEST_BYTES,
            LATEST_FILE,
            Client::Update,
        )? {
            Fetched::NotModified => return Ok(Read::NotModified),
            Fetched::Whole { bytes, etag } => (bytes, etag),
        },
    };
    let text =
        String::from_utf8(bytes).map_err(|_| format!("{} is not UTF-8 text", latest.address()))?;
    let release = parse_release(&text, channel)
        .map_err(|problem| format!("{} is not valid: {problem}", latest.address()))?;
    Ok(Read::Whole {
        release: Box::new(release),
        etag,
    })
}

/// Reads and checks the text of `latest.json` for `channel`.
pub fn parse_release(text: &str, channel: UpdateChannel) -> Result<UpdateRelease, String> {
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    let release: UpdateRelease = serde_json::from_str(text).map_err(|error| error.to_string())?;
    if release.schema_version != UPDATE_SCHEMA_VERSION {
        return Err(format!(
            "it has schemaVersion {}; this CabinetOS reads version {UPDATE_SCHEMA_VERSION}",
            release.schema_version
        ));
    }
    if release.channel != channel {
        return Err(format!(
            "it belongs to the {} channel, not {}",
            release.channel.name(),
            channel.name()
        ));
    }
    let Some(version) = Version::parse(&release.version) else {
        return Err(format!(
            "version `{}` is not a semantic version",
            release.version
        ));
    };
    if channel == UpdateChannel::Stable && version.is_prerelease() {
        return Err(format!(
            "the stable channel never offers a pre-release, and {} is one",
            release.version
        ));
    }
    if !transfer::is_sha256(&release.zip.sha256) {
        return Err(format!(
            "zip.sha256 `{}` is not 64 hex digits",
            release.zip.sha256
        ));
    }
    if release.zip.size == 0 {
        return Err("zip.size is 0".to_owned());
    }
    for (field, value) in [
        ("zip.url", &release.zip.url),
        ("notes.url", &release.notes.url),
    ] {
        if value.trim().is_empty() {
            return Err(format!("{field} is empty"));
        }
    }
    Ok(release)
}

/// Where `reference` (the zip, the notes), named in `latest.json`, is.
pub fn locate(latest: &Latest, reference: &str, allow_insecure: bool) -> Result<Location, String> {
    transfer::locate(&latest.base, reference, allow_insecure, Client::Update)
}

/// The address of `reference` as a user can open it: a URL, or a path.
pub fn address_of(location: &Location) -> String {
    match location {
        Location::File(path) => path.display().to_string(),
        Location::Web(url) => url.to_string(),
    }
}

/// Reads the release notes at `location`, at most 256 KiB of UTF-8.
pub fn read_notes(
    http: &Http,
    location: &Location,
    allow_insecure: bool,
) -> Result<String, String> {
    let bytes = match location {
        Location::File(path) => transfer::read_limited(path, MAX_NOTES_BYTES, "the release notes")?,
        Location::Web(url) => match transfer::get(
            http,
            url,
            None,
            allow_insecure,
            MAX_NOTES_BYTES,
            "the release notes",
            Client::Update,
        )? {
            Fetched::Whole { bytes, .. } => bytes,
            Fetched::NotModified => {
                return Err(format!(
                    "the server answered 304 for {url} without being asked"
                ));
            }
        },
    };
    let text =
        String::from_utf8(bytes).map_err(|_| "the release notes are not UTF-8 text".to_owned())?;
    Ok(text.strip_prefix('\u{feff}').unwrap_or(&text).to_owned())
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use url::Url;

    use super::*;

    fn release(channel: &str, version: &str) -> String {
        serde_json::json!({
            "schemaVersion": 1,
            "channel": channel,
            "version": version,
            "published": "2026-09-30",
            "zip": {"url": "https://example.org/a.zip", "sha256": "ab".repeat(32), "size": 10},
            "notes": {"url": "notes.md"},
            "requires": {"windowsAppRuntime": "2.5", "dotnet": "10.0"},
            "addedLater": true
        })
        .to_string()
    }

    #[test]
    fn the_channel_folder_is_under_the_source() {
        let web = locate_latest(
            "https://example.org/cabinetos/update",
            UpdateChannel::Stable,
            false,
        )
        .unwrap();
        assert_eq!(
            web.address(),
            "https://example.org/cabinetos/update/stable/latest.json"
        );
        let slash =
            locate_latest("https://example.org/update/", UpdateChannel::Preview, false).unwrap();
        assert_eq!(
            slash.address(),
            "https://example.org/update/preview/latest.json"
        );
        let local = locate_latest(r"C:\feed", UpdateChannel::Stable, false).unwrap();
        assert_eq!(
            local.location,
            Location::File(PathBuf::from(r"C:\feed\stable\latest.json"))
        );
        assert_eq!(
            locate(&local, "notes-0.2.0.md", false).unwrap(),
            Location::File(PathBuf::from(r"C:\feed\stable\notes-0.2.0.md"))
        );
        assert_eq!(
            locate(&web, "notes-0.2.0.md", false).unwrap(),
            Location::Web(
                Url::parse("https://example.org/cabinetos/update/stable/notes-0.2.0.md").unwrap()
            )
        );
        let refused =
            locate_latest("http://example.org/update", UpdateChannel::Stable, false).unwrap_err();
        assert!(refused.contains("update.allowInsecure"), "{refused}");
        for bad in ["", "update", "ftp://example.org/update"] {
            assert!(
                locate_latest(bad, UpdateChannel::Stable, true).is_err(),
                "{bad}"
            );
        }
    }

    #[test]
    fn latest_json_is_checked_for_its_channel() {
        let stable = parse_release(&release("stable", "0.2.0"), UpdateChannel::Stable).unwrap();
        assert_eq!(stable.requires.dotnet.as_deref(), Some("10.0"));
        let error = parse_release(&release("stable", "0.2.0-preview.1"), UpdateChannel::Stable)
            .unwrap_err();
        assert!(error.contains("never offers a pre-release"), "{error}");
        assert!(
            parse_release(
                &release("preview", "0.2.0-preview.1"),
                UpdateChannel::Preview
            )
            .is_ok()
        );
        assert!(parse_release(&release("preview", "0.2.0"), UpdateChannel::Preview).is_ok());
        let error = parse_release(&release("preview", "0.2.0"), UpdateChannel::Stable).unwrap_err();
        assert!(error.contains("preview channel"), "{error}");
        let error = parse_release(&release("stable", "v0.2"), UpdateChannel::Stable).unwrap_err();
        assert!(error.contains("not a semantic version"), "{error}");
        let mut bad_hash: serde_json::Value =
            serde_json::from_str(&release("stable", "0.2.0")).unwrap();
        bad_hash["zip"]["sha256"] = "xyz".into();
        let error = parse_release(&bad_hash.to_string(), UpdateChannel::Stable).unwrap_err();
        assert!(error.contains("64 hex digits"), "{error}");
        let mut newer: serde_json::Value =
            serde_json::from_str(&release("stable", "0.2.0")).unwrap();
        newer["schemaVersion"] = 2.into();
        let error = parse_release(&newer.to_string(), UpdateChannel::Stable).unwrap_err();
        assert!(error.contains("schemaVersion 2"), "{error}");
    }
}
