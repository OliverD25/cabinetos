//! In-app updates (ADR 0014; `docs/ipc.md`, "Updates"): the core checks a
//! channel's `latest.json`, downloads the release's zip and checks its
//! SHA-256, unpacks it into a staging folder, and at the user's word swaps
//! it into the install folder, keeping the version before for a rollback.
//!
//! - [`Updater::check`] reads `<update.source>/<channel>/latest.json`, with
//!   its `ETag`, and compares its version with the running one (semantic
//!   versioning; the stable channel never offers a pre-release).
//! - [`Updater::download`] downloads the zip into
//!   `staging\<version>\download.zip` with progress (at most 4 notices a
//!   second), checks its SHA-256, unpacks it into `staging\<version>\files`
//!   and checks that its `release.json` names the same version.
//! - [`Updater::apply`] swaps: the install folder's files go into
//!   `previous\` inside it, the staged ones are copied in, the installer's
//!   record and the Settings > Apps entry follow. A failed copy puts
//!   everything back. The window then restarts itself.
//! - [`Updater::rollback`] swaps back from `previous\`.
//! - [`Updater::run_daily`] is the quiet check once a day: check, and
//!   download what is newer.
//!
//! The install folder is the running core's folder. Only a release (with
//! `release.json` there) that this user may change without administrator
//! rights updates itself; anything else is `not_updatable`, and the updater
//! then reads and writes nothing at all. It writes only in the install
//! folder, in its own folder (`%LOCALAPPDATA%\CabinetOS\update`, the
//! variable `CABINETOS_UPDATE_DIR`), and in the Apps entry under the user's
//! own registry hive. Every step is blocking: the core runs them on its
//! blocking pool, inside the span of the request or of the daily check, so
//! each log line carries the action's trace id.
//!
//! Serves Constitution Article 2 (the releases come from the public
//! repository), Article 6 (`update.*` in `cabinetos.json`) and Article 12
//! (every step logged with its trace id).
#![forbid(unsafe_code)]

mod apps;
mod install;
mod source;
mod state;
mod swap;
mod version;

use std::ffi::OsString;
use std::fs::{self, File};
use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, MutexGuard, PoisonError, TryLockError};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use cabinetos_market::transfer::{self, Client, DownloadError, Http, ZipLimits};
use cabinetos_protocol::{UpdateChannel, UpdatePhase, UpdateRelease, UpdateStatus};

pub use apps::{APPS_KEY, APPS_KEY_ENV, apps_key};
pub use install::{RECORD_FILE, RELEASE_FILE, RELEASE_PAGE, not_updatable, read_release};
pub use source::{LATEST_FILE, parse_release};
pub use state::{STATE_FILE, Saved, Swap, SwapKind};
pub use swap::{DISCARD_DIR, PREVIOUS_DIR};
pub use version::Version;

/// Environment variable naming the updater's own folder.
pub const UPDATE_DIR_ENV: &str = "CABINETOS_UPDATE_DIR";

/// The default `update.source` (ADR 0014).
pub use cabinetos_protocol::DEFAULT_UPDATE_SOURCE as DEFAULT_SOURCE;

/// A day, in milliseconds: the daily check's rule and the snooze.
pub const DAY_MS: u64 = 24 * 60 * 60 * 1000;

/// At most 4 progress notices a second.
const PROGRESS_INTERVAL: Duration = Duration::from_millis(250);

/// The most bytes a release's zip may have, whatever `latest.json` says.
const MAX_DOWNLOAD: u64 = 2 * 1024 * 1024 * 1024;

/// What a release's zip may unpack to (0.1.0: 83 files, 246 MB).
const ZIP_LIMITS: ZipLimits = ZipLimits {
    entries: 20_000,
    bytes: 4 * 1024 * 1024 * 1024,
};

/// The programs a release must carry.
const REQUIRED_PROGRAMS: [&str; 2] = ["CabinetOS.exe", "cabinetos-core.exe"];

/// The updater's folder: `explicit` wins, then [`UPDATE_DIR_ENV`], then
/// `%LOCALAPPDATA%\CabinetOS\update`.
#[must_use]
pub fn update_dir(explicit: Option<PathBuf>) -> PathBuf {
    resolve_dir(
        explicit,
        std::env::var_os(UPDATE_DIR_ENV),
        std::env::var_os("LOCALAPPDATA"),
    )
}

/// Without `LOCALAPPDATA` (not a normal Windows session) the default is
/// under the temp folder, as for the logs.
fn resolve_dir(
    explicit: Option<PathBuf>,
    env_dir: Option<OsString>,
    local_app_data: Option<OsString>,
) -> PathBuf {
    if let Some(dir) = explicit {
        return dir;
    }
    if let Some(dir) = env_dir.filter(|dir| !dir.is_empty()) {
        return PathBuf::from(dir);
    }
    local_app_data
        .filter(|dir| !dir.is_empty())
        .map_or_else(std::env::temp_dir, PathBuf::from)
        .join("CabinetOS")
        .join("update")
}

/// The update settings (`update.*` in `cabinetos.json`), read when each step
/// starts, so a change applies to the next one.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Settings {
    /// The daily check.
    pub check: bool,
    /// Which `latest.json`.
    pub channel: UpdateChannel,
    /// The folder of the channels' folders.
    pub source: String,
    /// Plain `http:` too.
    pub allow_insecure: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            check: true,
            channel: UpdateChannel::Stable,
            source: DEFAULT_SOURCE.to_owned(),
            allow_insecure: false,
        }
    }
}

/// What the updater tells the clients.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Notice {
    /// The state changed.
    State(Box<UpdateStatus>),
    /// How far a download has come.
    Progress(Progress),
}

/// How far a download has come.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Progress {
    /// The version downloading.
    pub version: String,
    /// Bytes so far.
    pub bytes: u64,
    /// The zip's size, as `latest.json` gives it.
    pub total: u64,
    /// The speed since the download began.
    pub bytes_per_second: u64,
}

/// Why a step was refused, or failed.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct UpdateError {
    /// What kind of failure.
    pub kind: ErrorKind,
    /// What happened, for the user.
    pub message: String,
}

/// What kind of failure.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ErrorKind {
    /// The step cannot run now (nothing to download, nothing staged, not
    /// updatable, another step running); nothing changed.
    Refused,
    /// The download's SHA-256 is not the one `latest.json` gives; it was
    /// deleted.
    HashMismatch,
    /// The step ran and failed; the state says `failed`.
    Failed,
}

impl UpdateError {
    fn refused(message: impl Into<String>) -> Self {
        Self {
            kind: ErrorKind::Refused,
            message: message.into(),
        }
    }
}

impl std::fmt::Display for UpdateError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for UpdateError {}

/// Where the updater works.
#[derive(Clone, Debug)]
pub struct Paths {
    /// The install folder: the running core's folder.
    pub install: PathBuf,
    /// The updater's own folder (`state.json`, `staging\`, the notes).
    pub dir: PathBuf,
    /// The Apps entry's key under `HKEY_CURRENT_USER`.
    pub apps_key: String,
}

/// What the updater knows now.
#[derive(Debug)]
struct Live {
    phase: UpdatePhase,
    message: Option<String>,
    channel: UpdateChannel,
    notes_url: Option<String>,
    notes: Option<String>,
    previous: Option<String>,
    installed: Option<String>,
    saved: Saved,
}

/// The file whose lock one update step holds, among every core that shares
/// the update folder (two windows): two downloads into one staging folder,
/// or two swaps at once, would break each other.
const STEP_LOCK: &str = ".step.lock";

/// The file whose lock a read, change and write of `state.json` holds, so
/// two cores never write over each other's change.
const STATE_LOCK: &str = ".state.lock";

/// The locks one step holds until it is dropped.
struct Step<'a> {
    _process: MutexGuard<'a, ()>,
    /// `None` when the lock file could not be opened: the step goes on,
    /// as it would with one core.
    _cores: Option<File>,
}

/// The updater of one install.
pub struct Updater {
    paths: Paths,
    current: String,
    /// Why the install cannot update itself; `None` when it can.
    blocked: Option<String>,
    /// Whether the install folder is a release (it has `release.json`).
    release: bool,
    http: Http,
    live: Mutex<Live>,
    /// One step at a time in this process.
    busy: Mutex<()>,
    /// Set when the core stops: a download in progress ends.
    stopping: AtomicBool,
    notify: Box<dyn Fn(Notice) + Send + Sync>,
}

impl std::fmt::Debug for Updater {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Updater")
            .field("paths", &self.paths)
            .field("current", &self.current)
            .field("blocked", &self.blocked)
            .finish_non_exhaustive()
    }
}

impl Updater {
    /// The updater of the install in `paths.install`, running `current`.
    /// For an install that can update itself, it removes what an earlier
    /// swap left, confirms a swap that this start runs, and forgets a
    /// download that is gone or no longer newer (unless another core is in
    /// the middle of a step). Blocking: it reads the state and the folders.
    pub fn open(
        paths: Paths,
        current: &str,
        settings: &Settings,
        notify: impl Fn(Notice) + Send + Sync + 'static,
    ) -> Self {
        let blocked = not_updatable(&paths.install);
        let release = paths.install.join(RELEASE_FILE).is_file();
        let updater = Self {
            current: current.to_owned(),
            blocked,
            release,
            http: Http::default(),
            live: Mutex::new(Live {
                phase: UpdatePhase::NotUpdatable,
                message: None,
                channel: settings.channel,
                notes_url: None,
                notes: None,
                previous: None,
                installed: None,
                saved: Saved::default(),
            }),
            busy: Mutex::new(()),
            stopping: AtomicBool::new(false),
            notify: Box::new(notify),
            paths,
        };
        if let Some(reason) = &updater.blocked {
            tracing::info!(install = %updater.paths.install.display(), reason, "this install does not update itself");
            return updater;
        }
        match updater.try_step_lock() {
            Ok(Some(_step)) => {
                swap::clean_up(&updater.paths.install);
                let (dir, current) = (&updater.paths.dir, updater.current.as_str());
                updater.edit_saved(|saved| {
                    confirm_swap(saved, current);
                    tidy(dir, saved, current);
                });
            }
            _ => {
                tracing::info!(
                    "another core is in an update step; the leftovers stay until the next start"
                );
            }
        }
        updater.reload();
        {
            let mut live = updater.lock();
            if let Some(latest) = live
                .saved
                .latest
                .clone()
                .filter(|latest| latest.channel == settings.channel)
            {
                live.notes_url = source::locate_latest(&settings.source, settings.channel, true)
                    .and_then(|file| source::locate(&file, &latest.notes.url, true))
                    .ok()
                    .map(|location| source::address_of(&location));
                live.notes =
                    fs::read_to_string(notes_file(&updater.paths.dir, &latest.version)).ok();
            }
            live.phase = resting_phase(&live, current, &updater.paths.dir);
            tracing::info!(
                install = %updater.paths.install.display(),
                current,
                state = ?live.phase,
                previous = live.previous.as_deref(),
                "the updater is ready"
            );
        }
        updater
    }

    /// Whether this install can update itself.
    #[must_use]
    pub fn updatable(&self) -> bool {
        self.blocked.is_none()
    }

    /// Ends a download in progress, and refuses the steps after it: the
    /// core is stopping. What the download left is removed.
    pub fn stop(&self) {
        self.stopping.store(true, Ordering::SeqCst);
    }

    /// The state now. Not blocking: it reads only what the updater keeps in
    /// memory.
    #[must_use]
    pub fn status(&self) -> UpdateStatus {
        let live = self.lock();
        let latest = live
            .saved
            .latest
            .clone()
            .filter(|latest| latest.channel == live.channel);
        UpdateStatus {
            state: live.phase,
            reason: self.blocked.clone(),
            message: live.message.clone(),
            current: self.current.clone(),
            channel: live.channel,
            notes_url: latest.as_ref().and(live.notes_url.clone()),
            notes: latest.as_ref().and(live.notes.clone()),
            latest,
            checked_at_ms: live.saved.last_check_ms,
            snoozed_until_ms: live.saved.snoozed_until_ms,
            previous: live.previous.clone(),
            installed: live.installed.clone(),
            install_dir: self
                .release
                .then(|| self.paths.install.display().to_string()),
        }
    }

    /// Whether the daily check is due at `now_ms`: the check is on, the
    /// install can update itself, and no check has worked in the last day.
    #[must_use]
    pub fn due(&self, settings: &Settings, now_ms: u64) -> bool {
        if !settings.check || self.blocked.is_some() || self.stopping.load(Ordering::SeqCst) {
            return false;
        }
        let live = self.lock();
        live.saved.last_check_ms.is_none_or(|last| {
            // A clock set back counts as due too.
            last > now_ms || now_ms - last >= DAY_MS
        })
    }

    /// The quiet check once a day: when it is due, checks, and downloads a
    /// newer version. `None` when it was not due. Blocking.
    pub fn run_daily(&self, settings: &Settings) -> Option<Result<UpdateStatus, UpdateError>> {
        if !self.due(settings, now_ms()) {
            return None;
        }
        tracing::info!(channel = settings.channel.name(), "the daily update check");
        Some(match self.check(settings) {
            Ok(status) if status.state == UpdatePhase::Available => self.download(settings),
            other => other,
        })
    }

    /// Follows a change of `update.channel`: what was read for the other
    /// channel is forgotten, and the next daily check is due at once.
    /// Blocking: it writes the state.
    pub fn set_channel(&self, channel: UpdateChannel) {
        {
            let mut live = self.lock();
            if live.channel == channel {
                return;
            }
            live.channel = channel;
            if self.blocked.is_some() {
                return;
            }
            live.notes = None;
            live.notes_url = None;
        }
        self.edit_saved(|saved| {
            saved.latest = None;
            saved.etag = None;
            saved.latest_source = None;
            saved.last_check_ms = None;
        });
        {
            let mut live = self.lock();
            if matches!(
                live.phase,
                UpdatePhase::Unchecked
                    | UpdatePhase::UpToDate
                    | UpdatePhase::Available
                    | UpdatePhase::Failed
            ) {
                live.phase = resting_phase(&live, &self.current, &self.paths.dir);
                live.message = None;
            }
        }
        tracing::info!(channel = channel.name(), "the update channel changed");
        self.publish();
    }

    /// Reads the channel's `latest.json` and compares its version with the
    /// running one. Blocking: it may wait for the network.
    pub fn check(&self, settings: &Settings) -> Result<UpdateStatus, UpdateError> {
        let _step = self.begin(true)?;
        self.lock().channel = settings.channel;
        self.enter(UpdatePhase::Checking);
        match self.try_check(settings) {
            Ok(()) => {
                self.publish();
                Ok(self.status())
            }
            Err(message) => Err(self.fail(ErrorKind::Failed, message)),
        }
    }

    fn try_check(&self, settings: &Settings) -> Result<(), String> {
        let latest =
            source::locate_latest(&settings.source, settings.channel, settings.allow_insecure)?;
        let address = latest.address();
        let (tag, known) = {
            let live = self.lock();
            let same = live.saved.latest_source.as_deref() == Some(address.as_str());
            match (&live.saved.latest, same) {
                (Some(known), true) => (live.saved.etag.clone(), Some(known.clone())),
                _ => (None, None),
            }
        };
        let (release, etag) = match source::read_latest(
            &self.http,
            &latest,
            tag.as_deref(),
            settings.channel,
            settings.allow_insecure,
        )? {
            source::Read::Whole { release, etag } => (*release, etag),
            source::Read::NotModified => match known {
                Some(known) => (known, tag),
                None => {
                    return Err(format!(
                        "the server answered 304 for {address} without being asked"
                    ));
                }
            },
        };
        let newer = is_newer(&release.version, &self.current);
        let notes_location = source::locate(&latest, &release.notes.url, settings.allow_insecure);
        let notes_url = notes_location.as_ref().ok().map(source::address_of);
        // The notes of the release read before are kept: a 304 for
        // latest.json means they did not change either.
        let kept = {
            let live = self.lock();
            let same = live.saved.latest.as_ref() == Some(&release)
                && live.notes_url.as_ref() == notes_url.as_ref();
            same.then(|| live.notes.clone()).flatten()
        };
        let notes = match (&notes_location, newer) {
            (_, true) if kept.is_some() => kept,
            (Ok(location), true) => {
                match source::read_notes(&self.http, location, settings.allow_insecure) {
                    Ok(notes) => {
                        if let Err(error) = fs::create_dir_all(&self.paths.dir).and_then(|()| {
                            fs::write(notes_file(&self.paths.dir, &release.version), &notes)
                        }) {
                            tracing::debug!(%error, "cannot keep the release notes");
                        }
                        Some(notes)
                    }
                    Err(problem) => {
                        tracing::warn!(
                            problem,
                            "the release notes cannot be read; the dialog links to them"
                        );
                        None
                    }
                }
            }
            _ => None,
        };
        tracing::info!(
            source = %address,
            latest = %release.version,
            current = %self.current,
            newer,
            "update check done"
        );
        self.edit_saved(|saved| {
            saved.last_check_ms = Some(now_ms());
            saved.latest_source = Some(address);
            saved.etag = etag;
            saved.latest = Some(release);
        });
        let mut live = self.lock();
        live.notes_url = notes_url;
        live.notes = notes;
        live.message = None;
        live.phase = resting_phase(&live, &self.current, &self.paths.dir);
        Ok(())
    }

    /// Downloads the newer version the last check found, checks its
    /// SHA-256, and unpacks it into the staging folder. Blocking.
    pub fn download(&self, settings: &Settings) -> Result<UpdateStatus, UpdateError> {
        let _step = self.begin(true)?;
        let release = {
            let live = self.lock();
            let Some(release) = live
                .saved
                .latest
                .clone()
                .filter(|latest| latest.channel == settings.channel)
            else {
                return Err(UpdateError::refused(
                    "no newer version is known; check for updates first",
                ));
            };
            if !is_newer(&release.version, &self.current) {
                return Err(UpdateError::refused(format!(
                    "CabinetOS {} is the newest version of the {} channel; there is nothing to download",
                    self.current,
                    settings.channel.name()
                )));
            }
            if live.saved.staged.as_deref() == Some(release.version.as_str())
                && staged_release(&self.paths.dir, &release.version).is_some()
            {
                drop(live);
                self.enter(UpdatePhase::Downloaded);
                return Ok(self.status());
            }
            release
        };
        self.enter(UpdatePhase::Downloading);
        let folder = staging_dir(&self.paths.dir, &release.version);
        match self.try_download(settings, &release, &folder) {
            Ok(()) => {
                let version = release.version.clone();
                self.edit_saved(|saved| saved.staged = Some(version));
                {
                    let mut live = self.lock();
                    live.phase = UpdatePhase::Downloaded;
                    live.message = None;
                }
                tracing::info!(version = %release.version, folder = %folder.display(), "the update is downloaded, checked and unpacked");
                self.publish();
                Ok(self.status())
            }
            Err(error) => {
                remove_folder(&folder);
                Err(self.fail(error.kind, error.message))
            }
        }
    }

    fn try_download(
        &self,
        settings: &Settings,
        release: &UpdateRelease,
        folder: &Path,
    ) -> Result<(), UpdateError> {
        let failed = |message: String| UpdateError {
            kind: ErrorKind::Failed,
            message,
        };
        let version = &release.version;
        let latest =
            source::locate_latest(&settings.source, settings.channel, settings.allow_insecure)
                .map_err(failed)?;
        let location =
            source::locate(&latest, &release.zip.url, settings.allow_insecure).map_err(failed)?;
        remove_folder(folder);
        fs::create_dir_all(folder)
            .map_err(|error| failed(format!("cannot prepare {}: {error}", folder.display())))?;
        let zip = folder.join("download.zip");
        let total = release.zip.size;
        let started = Instant::now();
        let mut last_sent: Option<Instant> = None;
        let mut tell = |bytes: u64, last: bool| {
            if last || last_sent.is_none_or(|sent| sent.elapsed() >= PROGRESS_INTERVAL) {
                last_sent = Some(Instant::now());
                let millis = u64::try_from(started.elapsed().as_millis())
                    .unwrap_or(u64::MAX)
                    .max(1);
                (self.notify)(Notice::Progress(Progress {
                    version: version.clone(),
                    bytes,
                    total,
                    bytes_per_second: bytes.saturating_mul(1000) / millis,
                }));
            }
        };
        tell(0, false);
        let downloaded = transfer::download(
            &self.http,
            &location,
            &zip,
            total.min(MAX_DOWNLOAD),
            settings.allow_insecure,
            Client::Update,
            &mut |bytes| {
                tell(bytes, false);
                !self.stopping.load(Ordering::SeqCst)
            },
        )
        .map_err(|error| match error {
            DownloadError::TooLarge => failed(format!(
                "the download of CabinetOS {version} is larger than the {total} bytes latest.json gives; it was deleted"
            )),
            DownloadError::Failed(problem) => failed(format!("cannot download CabinetOS {version}: {problem}")),
        })?;
        tell(downloaded.bytes, true);
        if !downloaded.sha256.eq_ignore_ascii_case(&release.zip.sha256) {
            return Err(UpdateError {
                kind: ErrorKind::HashMismatch,
                message: format!(
                    "the download of CabinetOS {version} has the SHA-256 {}, but latest.json gives {}; it was deleted",
                    downloaded.sha256,
                    release.zip.sha256.to_ascii_lowercase()
                ),
            });
        }
        tracing::info!(
            version,
            bytes = downloaded.bytes,
            "the download's SHA-256 is the one latest.json gives"
        );
        let files = folder.join("files");
        transfer::extract_zip(&zip, &files, ZIP_LIMITS)
            .map_err(|problem| failed(format!("the download of CabinetOS {version} {problem}")))?;
        let facts = read_release(&files).map_err(|problem| {
            failed(format!(
                "the download of CabinetOS {version} is not a release: {problem}"
            ))
        })?;
        if facts.version != *version {
            return Err(failed(format!(
                "the download holds CabinetOS {}, not {version}; it was deleted",
                facts.version
            )));
        }
        for program in REQUIRED_PROGRAMS {
            if !files.join(program).is_file() {
                return Err(failed(format!(
                    "the download of CabinetOS {version} has no {program}; it was deleted"
                )));
            }
        }
        if let Err(error) = fs::remove_file(&zip) {
            tracing::debug!(%error, "cannot remove the unpacked zip");
        }
        Ok(())
    }

    /// Swaps the downloaded version into the install folder. Blocking.
    pub fn apply(&self) -> Result<UpdateStatus, UpdateError> {
        let _step = self.begin(true)?;
        let Some(version) = self.lock().saved.staged.clone() else {
            return Err(UpdateError::refused(
                "nothing is downloaded; download the update first",
            ));
        };
        let Some(files) = staged_release(&self.paths.dir, &version) else {
            self.edit_saved(|saved| saved.staged = None);
            let mut live = self.lock();
            live.phase = resting_phase(&live, &self.current, &self.paths.dir);
            return Err(UpdateError::refused(format!(
                "the downloaded files of CabinetOS {version} are gone; download it again"
            )));
        };
        let from = self.installed_version();
        self.enter(UpdatePhase::Applying);
        tracing::info!(from, to = %version, install = %self.paths.install.display(), "swapping the update into the install folder");
        match swap::apply(&self.paths.install, &files) {
            Ok(placed) => {
                self.refresh_apps_entry(&version, placed.bytes);
                let swapped = Swap {
                    kind: SwapKind::Apply,
                    from: from.clone(),
                    to: version.clone(),
                    at_ms: now_ms(),
                    confirmed_at_ms: None,
                };
                self.edit_saved(|saved| {
                    saved.staged = None;
                    saved.swap = Some(swapped);
                });
                {
                    let mut live = self.lock();
                    live.previous = Some(from);
                    live.installed = Some(version.clone());
                    live.phase = UpdatePhase::Ready;
                    live.message = None;
                }
                remove_folder(&staging_dir(&self.paths.dir, &version));
                self.publish();
                Ok(self.status())
            }
            Err(message) => Err(self.fail(ErrorKind::Failed, message)),
        }
    }

    /// Brings the version in `previous\` back. Blocking.
    pub fn rollback(&self) -> Result<UpdateStatus, UpdateError> {
        let _step = self.begin(false)?;
        if self.lock().previous.is_none() {
            return Err(UpdateError::refused(format!(
                "there is no previous version to go back to in {}",
                self.paths.install.join(PREVIOUS_DIR).display()
            )));
        }
        let from = self.installed_version();
        self.enter(UpdatePhase::Applying);
        tracing::info!(from, install = %self.paths.install.display(), "rolling back to the previous version");
        match swap::rollback(&self.paths.install) {
            Ok(version) => {
                self.refresh_apps_entry(&version, swap::install_size(&self.paths.install));
                let swapped = Swap {
                    kind: SwapKind::Rollback,
                    from,
                    to: version.clone(),
                    at_ms: now_ms(),
                    confirmed_at_ms: None,
                };
                self.edit_saved(|saved| saved.swap = Some(swapped));
                {
                    let mut live = self.lock();
                    live.previous = None;
                    live.phase = if version == self.current {
                        live.installed = None;
                        resting_phase(&live, &self.current, &self.paths.dir)
                    } else {
                        live.installed = Some(version);
                        UpdatePhase::Ready
                    };
                    live.message = None;
                }
                self.publish();
                Ok(self.status())
            }
            Err(message) => Err(self.fail(ErrorKind::Failed, message)),
        }
    }

    /// Records "not before a day from now": the window opens no dialog
    /// until then (the user said Later). Blocking: it writes the state.
    pub fn snooze(&self) -> Result<UpdateStatus, UpdateError> {
        self.refuse_if_blocked()?;
        let until = now_ms().saturating_add(DAY_MS);
        let _state = self.state_lock();
        let mut saved = state::load(&self.paths.dir);
        saved.snoozed_until_ms = Some(until);
        state::save(&self.paths.dir, &saved).map_err(|error| UpdateError {
            kind: ErrorKind::Failed,
            message: format!("cannot save the snooze: {error}"),
        })?;
        self.lock().saved = saved;
        tracing::info!(until_ms = until, "the update waits: Later");
        self.publish();
        Ok(self.status())
    }

    /// The locks of one step, after the checks every step makes: the
    /// install can update itself, the core is not stopping, no other step
    /// runs (here or in another core). What another core wrote is read
    /// again. With `needs_current`, a step that another core's swap made
    /// pointless (the install folder holds another version than the one
    /// running) is refused: a restart runs that version.
    fn begin(&self, needs_current: bool) -> Result<Step<'_>, UpdateError> {
        self.refuse_if_blocked()?;
        if self.stopping.load(Ordering::SeqCst) {
            return Err(UpdateError::refused("CabinetOS is closing"));
        }
        let step = match self.try_step_lock() {
            Ok(Some(step)) => step,
            Ok(None) => {
                return Err(UpdateError::refused(
                    "another CabinetOS window is in an update step now; wait until it ends",
                ));
            }
            Err(error) => return Err(error),
        };
        self.reload();
        let installed = self.installed_version();
        if needs_current && installed != self.current {
            {
                let mut live = self.lock();
                live.installed = Some(installed.clone());
                live.phase = UpdatePhase::Ready;
                live.message = None;
            }
            self.publish();
            return Err(UpdateError::refused(format!(
                "CabinetOS {installed} is in place already; restart CabinetOS to run it"
            )));
        }
        Ok(step)
    }

    /// Takes this process's step lock and the step lock file, or answers
    /// `Ok(None)` when another step holds either.
    fn try_step_lock(&self) -> Result<Option<Step<'_>>, UpdateError> {
        let process = match self.busy.try_lock() {
            Ok(guard) => guard,
            Err(TryLockError::Poisoned(poisoned)) => poisoned.into_inner(),
            Err(TryLockError::WouldBlock) => {
                return Err(UpdateError::refused(
                    "another update step is running; wait until it ends",
                ));
            }
        };
        let cores = match lock_file(&self.paths.dir, STEP_LOCK) {
            Ok(file) => match file.try_lock() {
                Ok(()) => Some(file),
                Err(fs::TryLockError::WouldBlock) => return Ok(None),
                Err(fs::TryLockError::Error(error)) => {
                    tracing::warn!(%error, "cannot lock the update folder; another core may update at the same time");
                    None
                }
            },
            Err(error) => {
                tracing::warn!(%error, "cannot lock the update folder; another core may update at the same time");
                None
            }
        };
        Ok(Some(Step {
            _process: process,
            _cores: cores,
        }))
    }

    /// Waits for the state lock file: a read, change and write of
    /// `state.json` is short.
    fn state_lock(&self) -> Option<File> {
        let locked = lock_file(&self.paths.dir, STATE_LOCK).and_then(|file| {
            file.lock()?;
            Ok(file)
        });
        match locked {
            Ok(file) => Some(file),
            Err(error) => {
                tracing::warn!(%error, "cannot lock the updater's state; another core may write it at the same time");
                None
            }
        }
    }

    /// Reads `state.json` again and applies `change` to it, then writes it;
    /// what this updater keeps follows. Another core's changes stay.
    fn edit_saved(&self, change: impl FnOnce(&mut Saved)) {
        let _state = self.state_lock();
        let mut saved = state::load(&self.paths.dir);
        change(&mut saved);
        if let Err(error) = state::save(&self.paths.dir, &saved) {
            tracing::warn!(%error, "cannot save the updater's state");
        }
        self.lock().saved = saved;
    }

    /// Reads `state.json` and the previous version again: another core
    /// may have changed them.
    fn reload(&self) {
        let saved = {
            let _state = self.state_lock();
            state::load(&self.paths.dir)
        };
        let previous = read_release(&self.paths.install.join(PREVIOUS_DIR))
            .ok()
            .map(|facts| facts.version);
        let mut live = self.lock();
        live.saved = saved;
        live.previous = previous;
    }

    /// The version the install folder holds, as its `release.json` says;
    /// the running one when that cannot be read.
    fn installed_version(&self) -> String {
        read_release(&self.paths.install)
            .map_or_else(|_| self.current.clone(), |facts| facts.version)
    }

    fn refresh_apps_entry(&self, version: &str, bytes: u64) {
        match apps::refresh(&self.paths.apps_key, &self.paths.install, version, bytes) {
            Ok(true) => {}
            Ok(false) => {
                tracing::debug!(key = %self.paths.apps_key, "no Apps entry names this install");
            }
            Err(problem) => tracing::warn!(problem, "the Apps entry keeps the old version"),
        }
    }

    fn refuse_if_blocked(&self) -> Result<(), UpdateError> {
        match &self.blocked {
            Some(reason) => Err(UpdateError::refused(reason.clone())),
            None => Ok(()),
        }
    }

    fn lock(&self) -> MutexGuard<'_, Live> {
        self.live.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Moves to `phase` and tells the clients.
    fn enter(&self, phase: UpdatePhase) {
        {
            let mut live = self.lock();
            live.phase = phase;
            live.message = None;
        }
        self.publish();
    }

    /// Moves to `failed` with `message`, tells the clients, and returns the
    /// error for the reply.
    fn fail(&self, kind: ErrorKind, message: String) -> UpdateError {
        tracing::warn!(error = %message, "update step failed");
        {
            let mut live = self.lock();
            live.phase = UpdatePhase::Failed;
            live.message = Some(message.clone());
        }
        self.publish();
        UpdateError { kind, message }
    }

    fn publish(&self) {
        (self.notify)(Notice::State(Box::new(self.status())));
    }
}

/// Opens (creating it and its folder) a lock file in `dir`. The file stays,
/// empty: removing it could race a core that is about to lock it.
fn lock_file(dir: &Path, name: &str) -> io::Result<File> {
    fs::create_dir_all(dir)?;
    fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(dir.join(name))
}

/// Whether `version` is newer than `current`; an unreadable one never is.
fn is_newer(version: &str, current: &str) -> bool {
    match (Version::parse(version), Version::parse(current)) {
        (Some(version), Some(current)) => version > current,
        _ => false,
    }
}

/// The phase when no step runs: downloaded, available, up to date, or not
/// checked yet.
fn resting_phase(live: &Live, current: &str, dir: &Path) -> UpdatePhase {
    let latest = live
        .saved
        .latest
        .as_ref()
        .filter(|latest| latest.channel == live.channel);
    match latest {
        Some(latest) if is_newer(&latest.version, current) => {
            let staged = live.saved.staged.as_deref() == Some(latest.version.as_str())
                && staged_release(dir, &latest.version).is_some();
            if staged {
                UpdatePhase::Downloaded
            } else {
                UpdatePhase::Available
            }
        }
        Some(_) => UpdatePhase::UpToDate,
        None if live.saved.last_check_ms.is_some() => UpdatePhase::UpToDate,
        None => UpdatePhase::Unchecked,
    }
}

/// Confirms, at a start, the swap this start runs: "swapped to X,
/// confirmed at start Y". Returns whether the state changed.
fn confirm_swap(saved: &mut Saved, current: &str) -> bool {
    let Some(swap) = saved
        .swap
        .as_mut()
        .filter(|swap| swap.confirmed_at_ms.is_none())
    else {
        return false;
    };
    if swap.to == current {
        let at = now_ms();
        swap.confirmed_at_ms = Some(at);
        tracing::info!(from = %swap.from, to = %swap.to, kind = ?swap.kind, confirmed_at_ms = at, "the swap is confirmed: this start runs its version");
        true
    } else {
        tracing::warn!(to = %swap.to, running = current, "the last swap is not what runs");
        false
    }
}

/// Forgets a staged download that is gone or no longer newer, and removes
/// the staging folders and notes nothing needs. Returns whether the state
/// changed.
fn tidy(dir: &Path, saved: &mut Saved, current: &str) -> bool {
    let mut changed = false;
    if let Some(staged) = saved.staged.clone()
        && (!is_newer(&staged, current) || staged_release(dir, &staged).is_none())
    {
        tracing::info!(staged, current, "a staged update is no longer needed");
        saved.staged = None;
        changed = true;
    }
    if let Ok(entries) = fs::read_dir(dir.join("staging")) {
        for entry in entries.flatten() {
            let keep = saved
                .staged
                .as_deref()
                .is_some_and(|staged| entry.file_name().to_string_lossy() == staged);
            if !keep {
                remove_folder(&entry.path());
            }
        }
    }
    let wanted = saved
        .latest
        .as_ref()
        .map(|latest| notes_name(&latest.version));
    if let Ok(entries) = fs::read_dir(dir) {
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().into_owned();
            let notes = name.starts_with("notes-")
                && Path::new(&name)
                    .extension()
                    .is_some_and(|extension| extension.eq_ignore_ascii_case("md"));
            if notes && Some(&name) != wanted.as_ref() {
                let _ = fs::remove_file(entry.path());
            }
        }
    }
    changed
}

fn staging_dir(dir: &Path, version: &str) -> PathBuf {
    dir.join("staging").join(version)
}

/// The unpacked release of `version`, when it is whole: its `release.json`
/// names that version.
fn staged_release(dir: &Path, version: &str) -> Option<PathBuf> {
    let files = staging_dir(dir, version).join("files");
    read_release(&files)
        .ok()
        .filter(|facts| facts.version == version)
        .map(|_| files)
}

fn notes_name(version: &str) -> String {
    format!("notes-{version}.md")
}

fn notes_file(dir: &Path, version: &str) -> PathBuf {
    dir.join(notes_name(version))
}

fn remove_folder(folder: &Path) {
    if let Err(error) = fs::remove_dir_all(folder)
        && error.kind() != io::ErrorKind::NotFound
    {
        tracing::debug!(folder = %folder.display(), %error, "cannot remove a staging folder");
    }
}

/// Milliseconds since 1970-01-01 UTC.
fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |since| {
            u64::try_from(since.as_millis()).unwrap_or(u64::MAX)
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_folder_comes_from_the_flag_then_the_variable_then_local_app_data() {
        let local = Some(OsString::from(r"C:\Users\me\AppData\Local"));
        assert_eq!(
            resolve_dir(Some(PathBuf::from(r"C:\flag")), None, local.clone()),
            PathBuf::from(r"C:\flag")
        );
        assert_eq!(
            resolve_dir(None, Some(OsString::from(r"C:\env")), local.clone()),
            PathBuf::from(r"C:\env")
        );
        assert_eq!(
            resolve_dir(None, None, local),
            PathBuf::from(r"C:\Users\me\AppData\Local\CabinetOS\update")
        );
    }

    #[test]
    fn newer_means_a_higher_semantic_version() {
        assert!(is_newer("0.2.0", "0.1.0"));
        assert!(!is_newer("0.1.0", "0.1.0"));
        assert!(!is_newer("0.0.9", "0.1.0"));
        assert!(is_newer("0.2.0-preview.1", "0.1.0"));
        assert!(!is_newer("0.2.0-preview.1", "0.2.0"));
        assert!(is_newer("0.2.0", "0.2.0-preview.3"));
        assert!(!is_newer("garbage", "0.1.0"));
    }
}
