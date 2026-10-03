//! The thumbnails and image drawings of Quick View (ADR 0023, decision 4).
//!
//! The shell's image factory may hang: a thumbnail handler that opted out
//! of Windows' surrogate process runs inside the core. So the core never
//! calls it from a task or from the blocking pool, only from threads of
//! its own, each in a COM apartment, and nothing that answers the pipe
//! waits for them:
//!
//! - **Two threads** take requests from one queue. A request the shell has
//!   not answered within its limit (2 s for a thumbnail, 5 s for a drawing)
//!   is answered `timeout`; its thread is left to finish, marked stuck, and
//!   a new thread takes its place. While four threads are stuck, requests
//!   are answered `busy`. A stuck thread that comes back puts its picture
//!   in the cache and ends, unless fewer than two others are working.
//! - **Newest first.** Each connection has at most one waiting request
//!   without `ahead`; a newer one takes its place and the older is answered
//!   `superseded`. Drawings come next, then the reads ahead (at most four
//!   waiting per connection, the oldest dropped).
//! - **The cache** keeps the last 128 pictures by the lower-case path, the
//!   file's last-write time and size, and the size asked for. A request it
//!   answers waits for no thread.
//! - **Warm start.** After the first listing the threads start, and each
//!   has the shell load its image factory once on the listed folder.
//!
//! Drawings (`render_image`) are written as `image.png` into a folder of
//! their own under the render cache, which keeps the last 16 and is emptied
//! when the core starts.

use std::collections::{HashMap, VecDeque};
use std::os::windows::fs::MetadataExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant, UNIX_EPOCH};

use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use cabinetos_fs::{FsError, ImageRequest, ShellImage, ShellImageError, ShellImages};
use cabinetos_protocol::{ErrorCode, RequestId, Response, ThumbnailReason};
use tokio::sync::oneshot;

use crate::listing::{Failure, fs_failure};

/// The file attributes that say a file's data is not on this disk:
/// `OFFLINE`, `RECALL_ON_OPEN`, `RECALL_ON_DATA_ACCESS`.
const NOT_ON_DISK: u32 = 0x1000 | 0x4_0000 | 0x40_0000;

/// The numbers of decision 4: how many threads, how long the shell may
/// take, how many stuck threads before `busy`, what the cache keeps.
#[derive(Clone, Copy, Debug)]
pub(crate) struct Limits {
    /// Threads that take requests while none is stuck.
    pub(crate) workers: usize,
    /// How long the shell may take for a thumbnail.
    pub(crate) thumbnail_timeout: Duration,
    /// How long the shell may take for a drawing.
    pub(crate) render_timeout: Duration,
    /// Stuck threads at which requests are answered `busy`.
    pub(crate) max_stuck: usize,
    /// Pictures the cache keeps.
    pub(crate) cached: usize,
    /// Reads ahead one connection may have waiting.
    pub(crate) ahead_per_connection: usize,
    /// Drawings kept in the render cache.
    pub(crate) renders_kept: usize,
}

impl Default for Limits {
    fn default() -> Self {
        Self {
            workers: 2,
            thumbnail_timeout: Duration::from_secs(2),
            render_timeout: Duration::from_secs(5),
            max_stuck: 4,
            cached: 128,
            ahead_per_connection: 4,
            renders_kept: 16,
        }
    }
}

/// Where pictures come from: the shell, or a stand-in in the tests.
pub(crate) trait Painter: Send + Sync + 'static {
    /// What one thread draws with; made on that thread.
    fn open(&self) -> Box<dyn Canvas>;
}

/// One thread's way to the shell.
pub(crate) trait Canvas {
    /// The picture of `path`, `size` pixels on its longer side at most.
    fn draw(
        &self,
        path: &str,
        size: u32,
        request: ImageRequest,
    ) -> Result<ShellImage, ShellImageError>;
    /// Loads what the first request would load, for `folder`.
    fn warm(&self, folder: &str);
}

/// The shell itself.
pub(crate) struct Shell;

impl Painter for Shell {
    fn open(&self) -> Box<dyn Canvas> {
        Box::new(ShellImages::enter())
    }
}

impl Canvas for ShellImages {
    fn draw(
        &self,
        path: &str,
        size: u32,
        request: ImageRequest,
    ) -> Result<ShellImage, ShellImageError> {
        self.image(path, size, request)
    }

    fn warm(&self, folder: &str) {
        ShellImages::warm(self, folder);
    }
}

/// A picture as the reply carries it.
#[derive(Debug)]
struct Picture {
    width: u32,
    height: u32,
    png_base64: String,
}

/// The cache key: the path in lower case, the last-write time, the file
/// size and the size asked for.
type Key = (String, u64, u64, u32);

/// How a request ended, for the task that waits for it.
enum Outcome {
    Picture(Arc<Picture>),
    Rendered {
        folder: String,
        width: u32,
        height: u32,
    },
    NoPicture(ThumbnailReason),
    Failed(Failure),
}

/// The way back to the task that waits: whoever answers first (the thread,
/// the timer, a newer request) takes it.
type Responder = Arc<Mutex<Option<oneshot::Sender<Outcome>>>>;

fn answer(responder: &Responder, outcome: Outcome) {
    if let Some(sender) = lock(responder).take() {
        let _ = sender.send(outcome);
    }
}

fn nobody_waits(responder: &Responder) -> bool {
    lock(responder)
        .as_ref()
        .is_none_or(oneshot::Sender::is_closed)
}

/// What a thread is asked to do.
enum Work {
    Thumbnail {
        path: String,
        size: u32,
        request: ImageRequest,
        key: Key,
    },
    Render {
        path: String,
        max_size: u32,
    },
}

impl Work {
    fn path(&self) -> &str {
        match self {
            Self::Thumbnail { path, .. } | Self::Render { path, .. } => path,
        }
    }
}

/// A request in the queue.
struct Job {
    seq: u64,
    connection: u64,
    work: Work,
    responder: Responder,
}

/// The waiting requests, by priority.
#[derive(Default)]
struct Queue {
    /// Requests without `ahead`: at most one per connection, oldest first.
    urgent: VecDeque<Job>,
    /// Drawings, oldest first.
    renders: VecDeque<Job>,
    /// Reads ahead, oldest first.
    ahead: VecDeque<Job>,
}

impl Queue {
    fn pop(&mut self) -> Option<Job> {
        self.urgent
            .pop_front()
            .or_else(|| self.renders.pop_front())
            .or_else(|| self.ahead.pop_front())
    }

    fn drain(&mut self) -> Vec<Job> {
        self.urgent
            .drain(..)
            .chain(self.renders.drain(..))
            .chain(self.ahead.drain(..))
            .collect()
    }
}

/// What a thread works on now.
struct Running {
    seq: u64,
    path: String,
    responder: Responder,
    render: bool,
}

/// One thread.
struct Worker {
    id: u64,
    running: Option<Running>,
    stuck: bool,
}

#[derive(Default)]
struct State {
    queue: Queue,
    workers: Vec<Worker>,
    stuck: usize,
    started: bool,
}

struct Inner {
    limits: Limits,
    painter: Box<dyn Painter>,
    state: Mutex<State>,
    work_ready: Condvar,
    cache: Mutex<Cache>,
    renders: Renders,
    runtime: tokio::runtime::Handle,
    next_seq: AtomicU64,
    next_worker: AtomicU64,
}

/// The thumbnail and drawing service, one for every connection.
pub(crate) struct Thumbnails {
    inner: Arc<Inner>,
    warmed: AtomicBool,
}

impl Thumbnails {
    /// The service with the shell, the numbers of decision 4, and the
    /// render cache under `cache_dir`. Starts no thread yet. Must be made
    /// inside the core's runtime.
    pub(crate) fn new(cache_dir: &Path) -> Arc<Self> {
        Self::with(Box::new(Shell), Limits::default(), cache_dir)
    }

    pub(crate) fn with(painter: Box<dyn Painter>, limits: Limits, cache_dir: &Path) -> Arc<Self> {
        Arc::new(Self {
            inner: Arc::new(Inner {
                limits,
                painter,
                state: Mutex::new(State::default()),
                work_ready: Condvar::new(),
                cache: Mutex::new(Cache::new(limits.cached)),
                renders: Renders::new(cache_dir.join("render"), limits.renders_kept),
                runtime: tokio::runtime::Handle::current(),
                next_seq: AtomicU64::new(1),
                next_worker: AtomicU64::new(1),
            }),
            warmed: AtomicBool::new(false),
        })
    }

    /// Empties the render cache of earlier sessions. Blocking: call it on
    /// the blocking pool.
    pub(crate) fn clear_renders(&self) {
        drop(self.inner.renders.clear_once());
    }

    /// The warm start: after the first listing, starts the threads, each of
    /// which has the shell load its image factory on `folder`. Later calls
    /// do nothing.
    pub(crate) fn warm_start(&self, folder: &str) {
        if self.warmed.swap(true, Ordering::Relaxed) {
            return;
        }
        let mut state = lock(&self.inner.state);
        start(&self.inner, &mut state, Some(folder));
    }

    /// The reply to `get_thumbnail` of `connection`. `path` is absolute and
    /// `size` one of the sizes the protocol allows.
    pub(crate) async fn thumbnail(
        &self,
        connection: u64,
        path: String,
        size: u32,
        ahead: bool,
    ) -> Response {
        let started = Instant::now();
        let (reply, cached) = self.thumbnail_reply(connection, &path, size, ahead).await;
        let took_ms = started.elapsed().as_secs_f64() * 1000.0;
        match &reply {
            Response::Thumbnail {
                reason: Some(reason),
                ..
            } => tracing::debug!(path, size, ahead, took_ms, cached, ?reason, "thumbnail"),
            Response::Thumbnail { .. } => {
                tracing::debug!(path, size, ahead, took_ms, cached, "thumbnail");
            }
            _ => {}
        }
        reply
    }

    async fn thumbnail_reply(
        &self,
        connection: u64,
        path: &str,
        size: u32,
        ahead: bool,
    ) -> (Response, bool) {
        let stat_path = path.to_owned();
        let file = match tokio::task::spawn_blocking(move || stat(&stat_path)).await {
            Ok(Ok(file)) => file,
            Ok(Err(failure)) => return (failed(failure), false),
            Err(error) => std::panic::resume_unwind(error.into_panic()),
        };
        let key = (path.to_lowercase(), file.modified, file.len, size);
        if let Some(picture) = lock(&self.inner.cache).get(&key) {
            return (picture_reply(path, size, &picture), true);
        }
        let request = if file.cloud {
            ImageRequest::CachedThumbnail
        } else {
            ImageRequest::Thumbnail
        };
        let work = Work::Thumbnail {
            path: path.to_owned(),
            size,
            request,
            key,
        };
        let reply = match self.queue(connection, work, ahead).await {
            Outcome::Picture(picture) => picture_reply(path, size, &picture),
            Outcome::NoPicture(ThumbnailReason::None) if file.cloud => {
                no_picture(path, size, ThumbnailReason::Cloud)
            }
            Outcome::NoPicture(reason) => no_picture(path, size, reason),
            Outcome::Failed(failure) => failed(failure),
            Outcome::Rendered { .. } => no_picture(path, size, ThumbnailReason::None),
        };
        (reply, false)
    }

    /// The reply to `render_image`. `path` is absolute and `max_size` from
    /// 1 to 2560.
    pub(crate) async fn render(&self, connection: u64, path: String, max_size: u32) -> Response {
        let stat_path = path.clone();
        match tokio::task::spawn_blocking(move || stat(&stat_path)).await {
            Ok(Ok(_)) => {}
            Ok(Err(failure)) => return failed(failure),
            Err(error) => std::panic::resume_unwind(error.into_panic()),
        }
        let work = Work::Render {
            path: path.clone(),
            max_size,
        };
        match self.queue(connection, work, false).await {
            Outcome::Rendered {
                folder,
                width,
                height,
            } => Response::RenderedImage {
                folder,
                width,
                height,
            },
            Outcome::Failed(failure) => failed(failure),
            Outcome::NoPicture(ThumbnailReason::Timeout) => failed((
                ErrorCode::Io,
                format!(
                    "{path}: the shell did not draw it within {} s",
                    self.inner.limits.render_timeout.as_secs()
                ),
            )),
            Outcome::NoPicture(ThumbnailReason::Busy) => failed((
                ErrorCode::Io,
                format!(
                    "{path}: {} thumbnail threads are stuck in the shell; try again later",
                    self.inner.limits.max_stuck
                ),
            )),
            Outcome::NoPicture(_) | Outcome::Picture(_) => failed((
                ErrorCode::Io,
                format!("{path}: the shell cannot draw this file"),
            )),
        }
    }

    /// Puts `work` in the queue and waits for its outcome.
    async fn queue(&self, connection: u64, work: Work, ahead: bool) -> Outcome {
        let (sender, receiver) = oneshot::channel();
        let responder: Responder = Arc::new(Mutex::new(Some(sender)));
        let render = matches!(work, Work::Render { .. });
        let job = Job {
            seq: self.inner.next_seq.fetch_add(1, Ordering::Relaxed),
            connection,
            work,
            responder,
        };
        let superseded = {
            let mut state = lock(&self.inner.state);
            if state.stuck >= self.inner.limits.max_stuck {
                return Outcome::NoPicture(ThumbnailReason::Busy);
            }
            start(&self.inner, &mut state, None);
            let limit = self.inner.limits.ahead_per_connection;
            let queue = &mut state.queue;
            let superseded = if render {
                queue.renders.push_back(job);
                None
            } else if ahead {
                queue.ahead.push_back(job);
                let mine = queue
                    .ahead
                    .iter()
                    .filter(|queued| queued.connection == connection)
                    .count();
                (mine > limit)
                    .then(|| {
                        queue
                            .ahead
                            .iter()
                            .position(|queued| queued.connection == connection)
                            .and_then(|oldest| queue.ahead.remove(oldest))
                    })
                    .flatten()
            } else if let Some(slot) = queue
                .urgent
                .iter_mut()
                .find(|queued| queued.connection == connection)
            {
                Some(std::mem::replace(slot, job))
            } else {
                queue.urgent.push_back(job);
                None
            };
            self.inner.work_ready.notify_one();
            superseded
        };
        if let Some(old) = superseded {
            answer(
                &old.responder,
                Outcome::NoPicture(ThumbnailReason::Superseded),
            );
        }
        receiver
            .await
            .unwrap_or(Outcome::NoPicture(ThumbnailReason::Busy))
    }

    #[cfg(test)]
    fn threads(&self) -> (usize, usize) {
        let state = lock(&self.inner.state);
        (state.workers.len(), state.stuck)
    }
}

/// Starts the threads, once.
fn start(inner: &Arc<Inner>, state: &mut State, warm: Option<&str>) {
    if state.started {
        return;
    }
    state.started = true;
    for _ in 0..inner.limits.workers {
        spawn_worker(inner, state, warm.map(str::to_owned));
    }
}

fn spawn_worker(inner: &Arc<Inner>, state: &mut State, warm: Option<String>) {
    let id = inner.next_worker.fetch_add(1, Ordering::Relaxed);
    state.workers.push(Worker {
        id,
        running: None,
        stuck: false,
    });
    let shared = Arc::clone(inner);
    let spawned = std::thread::Builder::new()
        .name(format!("thumbnail-{id}"))
        .spawn(move || work_loop(&shared, id, warm.as_deref()));
    if let Err(error) = spawned {
        tracing::error!(%error, "cannot start a thumbnail thread");
        state.workers.retain(|worker| worker.id != id);
    }
}

fn work_loop(inner: &Arc<Inner>, id: u64, warm: Option<&str>) {
    let canvas = inner.painter.open();
    if let Some(folder) = warm {
        let started = Instant::now();
        canvas.warm(folder);
        tracing::debug!(
            folder,
            took_ms = started.elapsed().as_secs_f64() * 1000.0,
            "thumbnail thread warmed"
        );
    }
    loop {
        let job = {
            let mut state = lock(&inner.state);
            loop {
                if let Some(job) = state.queue.pop() {
                    if nobody_waits(&job.responder) {
                        continue;
                    }
                    if let Some(worker) = state.workers.iter_mut().find(|worker| worker.id == id) {
                        worker.running = Some(Running {
                            seq: job.seq,
                            path: job.work.path().to_owned(),
                            responder: Arc::clone(&job.responder),
                            render: matches!(job.work, Work::Render { .. }),
                        });
                    }
                    break job;
                }
                state = inner
                    .work_ready
                    .wait(state)
                    .unwrap_or_else(PoisonError::into_inner);
            }
        };
        let limit = match job.work {
            Work::Thumbnail { .. } => inner.limits.thumbnail_timeout,
            Work::Render { .. } => inner.limits.render_timeout,
        };
        let timer = Arc::clone(inner);
        let seq = job.seq;
        inner.runtime.spawn(async move {
            tokio::time::sleep(limit).await;
            expire(&timer, id, seq);
        });
        let outcome = run(inner, canvas.as_ref(), &job);
        answer(&job.responder, outcome);
        if !finished(inner, id) {
            return;
        }
    }
}

/// Does one job on this thread.
fn run(inner: &Inner, canvas: &dyn Canvas, job: &Job) -> Outcome {
    match &job.work {
        Work::Thumbnail {
            path,
            size,
            request,
            key,
        } => match canvas.draw(path, *size, *request) {
            Ok(image) => {
                let picture = Arc::new(Picture {
                    width: image.width,
                    height: image.height,
                    png_base64: BASE64.encode(&image.png),
                });
                // Even for a request answered meanwhile: walking back to the
                // file is then a memory read.
                lock(&inner.cache).insert(key.clone(), Arc::clone(&picture));
                Outcome::Picture(picture)
            }
            Err(ShellImageError::NoImage(_)) => Outcome::NoPicture(ThumbnailReason::None),
            Err(ShellImageError::Path(error)) => Outcome::Failed(fs_failure(&error)),
        },
        Work::Render { path, max_size } => match canvas.draw(path, *max_size, ImageRequest::Render)
        {
            Ok(_) if nobody_waits(&job.responder) => Outcome::NoPicture(ThumbnailReason::Timeout),
            Ok(image) => match inner.renders.keep(&image.png) {
                Ok(folder) => Outcome::Rendered {
                    folder: folder.display().to_string(),
                    width: image.width,
                    height: image.height,
                },
                Err(error) => Outcome::Failed((
                    ErrorCode::Io,
                    format!("cannot write the drawing of {path}: {error}"),
                )),
            },
            Err(ShellImageError::NoImage(message)) => Outcome::Failed((
                ErrorCode::Io,
                format!("{path}: the shell cannot draw this file: {message}"),
            )),
            Err(ShellImageError::Path(error)) => Outcome::Failed(fs_failure(&error)),
        },
    }
}

/// After a job: whether this thread takes the next one. A stuck thread
/// that came back ends when enough others work.
fn finished(inner: &Inner, id: u64) -> bool {
    let mut state = lock(&inner.state);
    let Some(index) = state.workers.iter().position(|worker| worker.id == id) else {
        return false;
    };
    let worker = &mut state.workers[index];
    worker.running = None;
    if !worker.stuck {
        return true;
    }
    worker.stuck = false;
    state.stuck -= 1;
    let working = state
        .workers
        .iter()
        .filter(|other| other.id != id && !other.stuck)
        .count();
    tracing::info!(
        stuck = state.stuck,
        "a stuck thumbnail thread came back from the shell"
    );
    if working >= inner.limits.workers {
        state.workers.remove(index);
        return false;
    }
    true
}

/// The timer of a job: when the job still runs, its thread is stuck.
fn expire(inner: &Arc<Inner>, id: u64, seq: u64) {
    let mut state = lock(&inner.state);
    let Some(worker) = state.workers.iter_mut().find(|worker| worker.id == id) else {
        return;
    };
    let Some(running) = worker.running.as_ref().filter(|running| running.seq == seq) else {
        return;
    };
    if worker.stuck {
        return;
    }
    worker.stuck = true;
    let (path, responder, render) = (
        running.path.clone(),
        Arc::clone(&running.responder),
        running.render,
    );
    state.stuck += 1;
    answer(&responder, Outcome::NoPicture(ThumbnailReason::Timeout));
    if state.stuck >= inner.limits.max_stuck {
        let files: Vec<String> = state
            .workers
            .iter()
            .filter(|worker| worker.stuck)
            .filter_map(|worker| worker.running.as_ref().map(|running| running.path.clone()))
            .collect();
        tracing::warn!(
            stuck = state.stuck,
            files = %files.join(" | "),
            "thumbnail threads are stuck in the shell; thumbnails are busy until one comes back"
        );
        let waiting = state.queue.drain();
        drop(state);
        for job in waiting {
            answer(&job.responder, Outcome::NoPicture(ThumbnailReason::Busy));
        }
    } else {
        tracing::warn!(
            path,
            render,
            stuck = state.stuck,
            "a thumbnail thread is stuck in the shell; a new one takes its place"
        );
        spawn_worker(inner, &mut state, None);
    }
}

/// What the cache key and the cloud rule need of a file.
struct FileFacts {
    modified: u64,
    len: u64,
    cloud: bool,
}

/// Reads a file's or folder's attributes, size and last-write time; never
/// its data, so a cloud file is not downloaded.
fn stat(path: &str) -> Result<FileFacts, Failure> {
    let metadata = std::fs::metadata(path).map_err(|error| {
        let fs_error = match error
            .raw_os_error()
            .and_then(|code| u32::try_from(code).ok())
        {
            Some(code) => FsError::from_win32(path, code),
            None => FsError::Io {
                path: path.to_owned(),
                source: error,
            },
        };
        fs_failure(&fs_error)
    })?;
    let modified = metadata
        .modified()
        .ok()
        .and_then(|time| time.duration_since(UNIX_EPOCH).ok())
        .map_or(0, |since| {
            u64::try_from(since.as_nanos()).unwrap_or(u64::MAX)
        });
    Ok(FileFacts {
        modified,
        len: metadata.len(),
        cloud: metadata.file_attributes() & NOT_ON_DISK != 0,
    })
}

fn picture_reply(path: &str, size: u32, picture: &Picture) -> Response {
    Response::Thumbnail {
        path: path.to_owned(),
        size,
        width: Some(picture.width),
        height: Some(picture.height),
        png_base64: Some(picture.png_base64.clone()),
        reason: None,
    }
}

fn no_picture(path: &str, size: u32, reason: ThumbnailReason) -> Response {
    Response::Thumbnail {
        path: path.to_owned(),
        size,
        width: None,
        height: None,
        png_base64: None,
        reason: Some(reason),
    }
}

fn failed((code, message): Failure) -> Response {
    Response::Error { code, message }
}

/// The last pictures, the oldest forgotten first.
struct Cache {
    map: HashMap<Key, Arc<Picture>>,
    order: VecDeque<Key>,
    capacity: usize,
}

impl Cache {
    fn new(capacity: usize) -> Self {
        Self {
            map: HashMap::new(),
            order: VecDeque::new(),
            capacity,
        }
    }

    fn get(&self, key: &Key) -> Option<Arc<Picture>> {
        self.map.get(key).cloned()
    }

    fn insert(&mut self, key: Key, picture: Arc<Picture>) {
        if self.map.insert(key.clone(), picture).is_none() {
            self.order.push_back(key);
        }
        while self.order.len() > self.capacity {
            if let Some(oldest) = self.order.pop_front() {
                self.map.remove(&oldest);
            }
        }
    }
}

/// The render cache: one folder per drawing, the last ones kept.
struct Renders {
    root: PathBuf,
    kept: usize,
    /// The folders of this session, oldest first; `None` until the
    /// folders of earlier sessions are removed.
    folders: Mutex<Option<VecDeque<PathBuf>>>,
}

impl Renders {
    fn new(root: PathBuf, kept: usize) -> Self {
        Self {
            root,
            kept,
            folders: Mutex::new(None),
        }
    }

    /// Removes the drawings of earlier sessions, once.
    fn clear_once(&self) -> MutexGuard<'_, Option<VecDeque<PathBuf>>> {
        let mut folders = lock(&self.folders);
        if folders.is_none() {
            if let Err(error) = std::fs::remove_dir_all(&self.root)
                && error.kind() != std::io::ErrorKind::NotFound
            {
                tracing::warn!(dir = %self.root.display(), %error, "cannot empty the render cache");
            }
            *folders = Some(VecDeque::new());
        }
        folders
    }

    /// Writes `png` as `image.png` into a new folder and returns the
    /// folder; the oldest folder beyond the number kept is removed.
    fn keep(&self, png: &[u8]) -> std::io::Result<PathBuf> {
        let mut folders = self.clear_once();
        let folder = self.root.join(RequestId::new().as_str());
        std::fs::create_dir_all(&folder)?;
        std::fs::write(folder.join("image.png"), png)?;
        let list = folders.get_or_insert_with(VecDeque::new);
        list.push_back(folder.clone());
        while list.len() > self.kept {
            if let Some(oldest) = list.pop_front()
                && let Err(error) = std::fs::remove_dir_all(&oldest)
            {
                tracing::debug!(dir = %oldest.display(), %error, "cannot remove an old drawing");
            }
        }
        Ok(folder)
    }
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

#[cfg(test)]
mod tests {
    use std::sync::mpsc;

    use super::*;

    fn scratch() -> tempfile::TempDir {
        let root = std::env::temp_dir().join("cabinetos-core-test");
        std::fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix("quickview")
            .tempdir_in(root)
            .unwrap()
    }

    /// A PNG of `width` by `height` pixels.
    fn write_png(path: &Path, width: u32, height: u32) {
        let mut bytes = Vec::new();
        let mut encoder = png::Encoder::new(&mut bytes, width, height);
        encoder.set_color(png::ColorType::Rgb);
        encoder.set_depth(png::BitDepth::Eight);
        let mut writer = encoder.write_header().unwrap();
        let mut pixels = Vec::new();
        for y in 0..height {
            for x in 0..width {
                pixels.extend([
                    u8::try_from(x * 255 / width).unwrap(),
                    u8::try_from(y * 255 / height).unwrap(),
                    90,
                ]);
            }
        }
        writer.write_image_data(&pixels).unwrap();
        writer.finish().unwrap();
        std::fs::write(path, bytes).unwrap();
    }

    fn png_size(base64: &str) -> (u32, u32) {
        let bytes = BASE64.decode(base64).unwrap();
        let reader = png::Decoder::new(std::io::Cursor::new(bytes))
            .read_info()
            .unwrap();
        (reader.info().width, reader.info().height)
    }

    fn text(path: &Path) -> String {
        path.display().to_string()
    }

    /// A painter whose every drawing waits until the test lets it go (or
    /// forever), and counts how often it was asked.
    struct Stuck {
        release: Arc<Mutex<Option<mpsc::Receiver<()>>>>,
        asked: Arc<AtomicU64>,
    }

    struct StuckCanvas {
        release: Arc<Mutex<Option<mpsc::Receiver<()>>>>,
        asked: Arc<AtomicU64>,
    }

    impl Painter for Stuck {
        fn open(&self) -> Box<dyn Canvas> {
            Box::new(StuckCanvas {
                release: Arc::clone(&self.release),
                asked: Arc::clone(&self.asked),
            })
        }
    }

    impl Canvas for StuckCanvas {
        fn draw(
            &self,
            _path: &str,
            _size: u32,
            _request: ImageRequest,
        ) -> Result<ShellImage, ShellImageError> {
            self.asked.fetch_add(1, Ordering::SeqCst);
            loop {
                let released = lock(&self.release)
                    .as_ref()
                    .is_some_and(|release| release.try_recv().is_ok());
                if released {
                    return Err(ShellImageError::NoImage("released".to_owned()));
                }
                std::thread::sleep(Duration::from_millis(5));
            }
        }

        fn warm(&self, _folder: &str) {}
    }

    fn quick() -> Limits {
        Limits {
            thumbnail_timeout: Duration::from_millis(150),
            render_timeout: Duration::from_millis(300),
            ..Limits::default()
        }
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_png_gets_a_picture_with_its_aspect_and_comes_from_the_cache_the_second_time() {
        let dir = scratch();
        let file = dir.path().join("wide.png");
        write_png(&file, 1200, 600);
        let thumbnails = Thumbnails::new(dir.path());
        let started = Instant::now();
        let first = thumbnails.thumbnail(1, text(&file), 256, false).await;
        let first_ms = started.elapsed().as_secs_f64() * 1000.0;
        let Response::Thumbnail {
            width: Some(256),
            height: Some(128),
            png_base64: Some(png),
            reason: None,
            ..
        } = &first
        else {
            panic!("{first:?}");
        };
        assert_eq!(png_size(png), (256, 128));
        let started = Instant::now();
        let (second, cached) = thumbnails
            .thumbnail_reply(1, &text(&file), 256, false)
            .await;
        let second_ms = started.elapsed().as_secs_f64() * 1000.0;
        assert!(cached);
        assert_eq!(second, first);
        // Printed for the report: `cargo test -p cabinetos-core quickview -- --nocapture`.
        println!(
            "get_thumbnail of a 1200x600 PNG: first {first_ms:.1} ms, cached {second_ms:.1} ms"
        );
        // A changed file is a new entry.
        write_png(&file, 600, 600);
        let (third, cached) = thumbnails
            .thumbnail_reply(1, &text(&file), 256, false)
            .await;
        assert!(!cached);
        assert!(
            matches!(
                third,
                Response::Thumbnail {
                    width: Some(256),
                    height: Some(256),
                    ..
                }
            ),
            "{third:?}"
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_file_without_a_thumbnail_gives_none_and_a_missing_one_not_found() {
        let dir = scratch();
        let file = dir.path().join("notes.xyz");
        std::fs::write(&file, b"nobody draws this").unwrap();
        let thumbnails = Thumbnails::new(dir.path());
        let reply = thumbnails.thumbnail(1, text(&file), 256, false).await;
        assert_eq!(
            reply,
            Response::Thumbnail {
                path: text(&file),
                size: 256,
                width: None,
                height: None,
                png_base64: None,
                reason: Some(ThumbnailReason::None),
            }
        );
        let gone = thumbnails
            .thumbnail(1, text(&dir.path().join("gone.png")), 256, false)
            .await;
        assert!(
            matches!(
                gone,
                Response::Error {
                    code: ErrorCode::NotFound,
                    ..
                }
            ),
            "{gone:?}"
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_queued_request_is_superseded_by_a_newer_one_of_its_connection() {
        let dir = scratch();
        let (release, receiver) = mpsc::channel();
        let asked = Arc::new(AtomicU64::new(0));
        let painter = Stuck {
            release: Arc::new(Mutex::new(Some(receiver))),
            asked: Arc::clone(&asked),
        };
        let thumbnails = Thumbnails::with(
            Box::new(painter),
            Limits {
                workers: 1,
                thumbnail_timeout: Duration::from_secs(30),
                ..Limits::default()
            },
            dir.path(),
        );
        let files: Vec<String> = (0..3)
            .map(|index| {
                let file = dir.path().join(format!("{index}.png"));
                std::fs::write(&file, b"x").unwrap();
                text(&file)
            })
            .collect();
        // The first keeps the one thread busy; the second waits; the third
        // takes its place.
        let busy = tokio::spawn({
            let thumbnails = Arc::clone(&thumbnails);
            let file = files[0].clone();
            async move { thumbnails.thumbnail(1, file, 256, false).await }
        });
        while asked.load(Ordering::SeqCst) == 0 {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        let waiting = tokio::spawn({
            let thumbnails = Arc::clone(&thumbnails);
            let file = files[1].clone();
            async move { thumbnails.thumbnail(1, file, 256, false).await }
        });
        while lock(&thumbnails.inner.state).queue.urgent.is_empty() {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        let newer = tokio::spawn({
            let thumbnails = Arc::clone(&thumbnails);
            let file = files[2].clone();
            async move { thumbnails.thumbnail(1, file, 256, false).await }
        });
        let superseded = waiting.await.unwrap();
        assert!(
            matches!(
                &superseded,
                Response::Thumbnail { path, reason: Some(ThumbnailReason::Superseded), .. } if *path == files[1]
            ),
            "{superseded:?}"
        );
        // The one in the shell runs to its end; then the newer one runs.
        release.send(()).unwrap();
        let first = busy.await.unwrap();
        assert!(
            matches!(
                first,
                Response::Thumbnail {
                    reason: Some(ThumbnailReason::None),
                    ..
                }
            ),
            "{first:?}"
        );
        release.send(()).unwrap();
        let last = newer.await.unwrap();
        assert!(
            matches!(&last, Response::Thumbnail { path, reason: Some(ThumbnailReason::None), .. } if *path == files[2]),
            "{last:?}"
        );
        assert_eq!(asked.load(Ordering::SeqCst), 2);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn reads_ahead_keep_four_per_connection_and_wait_for_the_others() {
        let dir = scratch();
        let (release, receiver) = mpsc::channel();
        let asked = Arc::new(AtomicU64::new(0));
        let thumbnails = Thumbnails::with(
            Box::new(Stuck {
                release: Arc::new(Mutex::new(Some(receiver))),
                asked: Arc::clone(&asked),
            }),
            Limits {
                workers: 1,
                thumbnail_timeout: Duration::from_secs(30),
                ..Limits::default()
            },
            dir.path(),
        );
        let file = dir.path().join("a.png");
        std::fs::write(&file, b"x").unwrap();
        let busy = tokio::spawn({
            let thumbnails = Arc::clone(&thumbnails);
            let file = text(&file);
            async move { thumbnails.thumbnail(1, file, 256, false).await }
        });
        while asked.load(Ordering::SeqCst) == 0 {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        let mut aheads = Vec::new();
        for index in 0..5 {
            let ahead = dir.path().join(format!("ahead-{index}.png"));
            std::fs::write(&ahead, b"x").unwrap();
            aheads.push(tokio::spawn({
                let thumbnails = Arc::clone(&thumbnails);
                async move { thumbnails.thumbnail(1, text(&ahead), 256, true).await }
            }));
            while lock(&thumbnails.inner.state).queue.ahead.len() < (index + 1).min(4) {
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
        }
        let dropped = aheads.remove(0).await.unwrap();
        assert!(
            matches!(
                dropped,
                Response::Thumbnail {
                    reason: Some(ThumbnailReason::Superseded),
                    ..
                }
            ),
            "{dropped:?}"
        );
        assert_eq!(lock(&thumbnails.inner.state).queue.ahead.len(), 4);
        for _ in 0..5 {
            release.send(()).unwrap();
        }
        busy.await.unwrap();
        for ahead in aheads {
            assert!(matches!(
                ahead.await.unwrap(),
                Response::Thumbnail {
                    reason: Some(ThumbnailReason::None),
                    ..
                }
            ));
        }
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_shell_call_that_never_returns_gives_timeout_then_busy_after_four() {
        let dir = scratch();
        let (release, receiver) = mpsc::channel();
        let asked = Arc::new(AtomicU64::new(0));
        let thumbnails = Thumbnails::with(
            Box::new(Stuck {
                release: Arc::new(Mutex::new(Some(receiver))),
                asked: Arc::clone(&asked),
            }),
            quick(),
            dir.path(),
        );
        let file = dir.path().join("hangs.png");
        std::fs::write(&file, b"x").unwrap();
        for round in 1..=4 {
            let reply = thumbnails.thumbnail(1, text(&file), 256, false).await;
            assert!(
                matches!(
                    reply,
                    Response::Thumbnail {
                        reason: Some(ThumbnailReason::Timeout),
                        ..
                    }
                ),
                "round {round}: {reply:?}"
            );
            let (threads, stuck) = thumbnails.threads();
            assert_eq!(stuck, round);
            // Each stuck thread was replaced, except the fourth.
            assert_eq!(threads, 2 + round.min(3));
        }
        let busy = thumbnails.thumbnail(1, text(&file), 256, false).await;
        assert!(
            matches!(
                busy,
                Response::Thumbnail {
                    reason: Some(ThumbnailReason::Busy),
                    ..
                }
            ),
            "{busy:?}"
        );
        assert_eq!(asked.load(Ordering::SeqCst), 4);
        // One comes back: requests run again, and it ends once enough
        // others work.
        release.send(()).unwrap();
        while thumbnails.threads().1 == 4 {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        let rendered = thumbnails.render(1, text(&file), 512).await;
        assert!(
            matches!(&rendered, Response::Error { code: ErrorCode::Io, message } if message.contains("did not draw")),
            "{rendered:?}"
        );
        // Let every thread go, so none outlives the test.
        for _ in 0..8 {
            let _ = release.send(());
        }
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_render_writes_a_png_of_the_size_asked_and_the_seventeenth_removes_the_first() {
        let dir = scratch();
        let file = dir.path().join("photo.png");
        write_png(&file, 1024, 768);
        let cache = dir.path().join("cache");
        let earlier = cache.join("render").join("from-an-earlier-session");
        std::fs::create_dir_all(&earlier).unwrap();
        let thumbnails = Thumbnails::new(&cache);
        thumbnails.clear_renders();
        assert!(!earlier.exists(), "the render cache is emptied at start");
        let mut folders = Vec::new();
        for round in 0..17 {
            let reply = thumbnails.render(1, text(&file), 512).await;
            let Response::RenderedImage {
                folder,
                width,
                height,
            } = reply
            else {
                panic!("round {round}: {reply:?}");
            };
            assert_eq!((width, height), (512, 384));
            let png = std::fs::read(Path::new(&folder).join("image.png")).unwrap();
            let reader = png::Decoder::new(std::io::Cursor::new(png))
                .read_info()
                .unwrap();
            assert_eq!((reader.info().width, reader.info().height), (512, 384));
            folders.push(PathBuf::from(folder));
        }
        assert!(!folders[0].exists(), "the 17th render removes the first");
        assert!(folders[1..].iter().all(|folder| folder.exists()));
        let not_an_image = dir.path().join("notes.xyz");
        std::fs::write(&not_an_image, b"x").unwrap();
        let refused = thumbnails.render(1, text(&not_an_image), 512).await;
        assert!(
            matches!(
                refused,
                Response::Error {
                    code: ErrorCode::Io,
                    ..
                }
            ),
            "{refused:?}"
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn the_warm_start_starts_the_threads_once() {
        let dir = scratch();
        let thumbnails = Thumbnails::new(dir.path());
        assert_eq!(thumbnails.threads(), (0, 0));
        thumbnails.warm_start(&text(dir.path()));
        thumbnails.warm_start(&text(dir.path()));
        assert_eq!(thumbnails.threads(), (2, 0));
    }
}
