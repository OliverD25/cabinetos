//! In-app updates (`docs/ipc.md`, "Updates"; ADR 0014): `latest.json`, the
//! small file each release channel publishes, and the updater's state as
//! the core reports it. `latest.json` on the wire is the file's own format,
//! in its camelCase keys, as an index item is.

use serde::{Deserialize, Serialize};

/// The version of the `latest.json` format this core reads.
pub const UPDATE_SCHEMA_VERSION: u32 = 1;

/// The default `update.source`: a folder on the marketplace's site (GitHub
/// Pages of the public repository `cabinetos-marketplace`), with one folder
/// per channel.
pub const DEFAULT_UPDATE_SOURCE: &str = "https://oliverd25.github.io/cabinetos-marketplace/update";

/// A release channel: which `latest.json` the updater reads
/// (`<update.source>/<channel>/latest.json`).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "lowercase")]
pub enum UpdateChannel {
    /// Releases only; never a version with a pre-release tag.
    #[default]
    Stable,
    /// Releases and previews, such as `0.2.0-preview.1`.
    Preview,
}

impl UpdateChannel {
    /// The channel's name, as the folder of its `latest.json` has it.
    #[must_use]
    pub const fn name(self) -> &'static str {
        match self {
            Self::Stable => "stable",
            Self::Preview => "preview",
        }
    }
}

/// `latest.json`: the newest version of one channel, where its zip is, how
/// to check it, and where its release notes are.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "camelCase")]
pub struct UpdateRelease {
    /// The format of this file; always 1 for now.
    pub schema_version: u32,
    /// The channel this file belongs to.
    pub channel: UpdateChannel,
    /// The version, as semantic versioning writes it: `0.2.0`, or with a
    /// pre-release tag on the preview channel, `0.2.0-preview.1`.
    pub version: String,
    /// The day it was published, `YYYY-MM-DD`.
    pub published: String,
    /// The release's zip.
    pub zip: UpdateZip,
    /// The release notes: the CHANGELOG section of the version.
    pub notes: UpdateNotes,
    /// The runtimes the version needs, `major.minor`, for the user to see.
    #[serde(default)]
    pub requires: UpdateRequires,
}

/// Where a release's zip is and how to check it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "camelCase")]
pub struct UpdateZip {
    /// An absolute URL (the GitHub Release asset), or a path relative to
    /// `latest.json`.
    pub url: String,
    /// The zip's SHA-256, 64 hex digits. A download with another hash is
    /// deleted.
    pub sha256: String,
    /// The zip's size in bytes, more than 0. A download that grows beyond
    /// it is stopped and deleted.
    pub size: u64,
}

/// Where a release's notes are.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "camelCase")]
pub struct UpdateNotes {
    /// A Markdown file: an absolute URL, or a path relative to
    /// `latest.json`.
    pub url: String,
}

/// The runtimes a release needs.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "camelCase")]
pub struct UpdateRequires {
    /// The Windows App Runtime, `major.minor`, such as `2.5`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub windows_app_runtime: Option<String>,
    /// The .NET runtime, `major.minor`, such as `10.0`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub dotnet: Option<String>,
}

/// Where the updater is.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum UpdatePhase {
    /// This install cannot update itself: a development build, an
    /// all-users install, or a folder the user cannot write. `reason`
    /// says which, and what to do instead.
    NotUpdatable,
    /// No check has run yet.
    Unchecked,
    /// The newest version of the channel is the one running (or older).
    UpToDate,
    /// Reading `latest.json` now.
    Checking,
    /// A newer version is there; nothing is downloaded yet.
    Available,
    /// Downloading the newer version now; `update_progress` tells how far.
    Downloading,
    /// The newer version is downloaded, checked and unpacked; `update_apply`
    /// puts it in place.
    Downloaded,
    /// Moving the files: a swap or a rollback.
    Applying,
    /// Another version is in place (`installed`); a restart runs it.
    Ready,
    /// The last step failed; `message` says why.
    Failed,
}

/// The updater's state: the reply `update_state` and the event
/// `update_state_changed`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct UpdateStatus {
    /// Where the updater is.
    pub state: UpdatePhase,
    /// With `not_updatable`: why, and what to do instead.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    /// With `failed`: what went wrong.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
    /// The version running now.
    pub current: String,
    /// The channel `update.channel` names.
    pub channel: UpdateChannel,
    /// The channel's `latest.json` as the last check read it, in the file's
    /// own format (camelCase keys).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub latest: Option<UpdateRelease>,
    /// Where the release notes of `latest` are.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub notes_url: Option<String>,
    /// The release notes of `latest`, Markdown, when the core could read
    /// them (at most 256 KiB).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub notes: Option<String>,
    /// When the last check ended, in milliseconds since 1970-01-01 UTC.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub checked_at_ms: Option<u64>,
    /// Until when the user said Later: the window opens no dialog before
    /// it, in milliseconds since 1970-01-01 UTC.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub snoozed_until_ms: Option<u64>,
    /// The version kept in `previous\`, which `update_rollback` brings back.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub previous: Option<String>,
    /// With `ready`: the version the install folder holds now, which the
    /// next start runs.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub installed: Option<String>,
    /// The install folder, where the window starts the new `CabinetOS.exe`
    /// after a swap. Absent for a development build.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub install_dir: Option<String>,
}
