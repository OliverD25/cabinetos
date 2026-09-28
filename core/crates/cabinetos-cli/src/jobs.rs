//! `cabinetos-cli copy`, `move`, `delete`, `jobs` and `job`: jobs run by the
//! core, followed from here.

use std::collections::BTreeMap;
use std::io::{IsTerminal, Write};
use std::time::{Duration, Instant};

use anyhow::{Context, anyhow, bail};
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{
    Conflict, ConflictKind, Envelope, Event, JobAction, JobInfo, JobKind, JobProgress, JobRequest,
    JobState, Rate, Request, Resolution, Response,
};

use crate::{expect_welcome, failure, say, send};

/// How often a progress line is printed when the output is not a terminal
/// (a file or a pipe keeps one line per second, not one per event).
const LINE_EVERY: Duration = Duration::from_secs(1);

/// A job to run from the command line.
pub(crate) struct JobRun {
    pub(crate) request: JobRequest,
    /// Answer every conflict with this.
    pub(crate) resolve: Option<Resolution>,
    /// Print how many progress events arrived per second.
    pub(crate) stats: bool,
}

/// Starts the job and follows it until it ends. Ctrl+C stops following;
/// the job goes on in the core.
pub(crate) async fn run(client: &mut PipeClient, job: JobRun) -> anyhow::Result<()> {
    let mut events = client
        .events()
        .context("the event stream was already taken")?;
    expect_welcome(client).await?;
    let recycling = matches!(job.request.kind, JobKind::Delete { permanent: false });
    let reply = send(client, Request::StartJob(job.request)).await?;
    let Response::JobStarted { job_id } = reply.body else {
        return Err(failure("start_job", &reply.body));
    };
    let mut screen = Screen::new();
    screen.line(&format!("job {job_id} started"));
    let mut stats = Stats::default();
    loop {
        tokio::select! {
            event = events.recv() => {
                let Some(Envelope { body: event, .. }) = event else {
                    bail!("the connection to the core ended");
                };
                match event {
                    Event::JobProgress(progress) if progress.job_id == job_id => {
                        stats.record(&progress);
                        screen.progress(&progress);
                    }
                    Event::JobConflict(conflict) if conflict.job_id == job_id => {
                        screen.line(&describe_conflict(&conflict, recycling));
                        match &job.resolve {
                            Some(resolution) => {
                                let reply = send(client, Request::ResolveConflict {
                                    job_id,
                                    conflict_id: conflict.conflict_id,
                                    resolution: resolution.clone(),
                                    apply_to_same_kind: false,
                                })
                                .await?;
                                if reply.body != Response::Ok {
                                    screen.line(&format!("{:#}", failure("resolve_conflict", &reply.body)));
                                }
                            }
                            None => screen.line(&format!(
                                "  decide with: cabinetos-cli job resolve {job_id} {} {}",
                                conflict.conflict_id,
                                answers(&conflict.kind, recycling)
                            )),
                        }
                    }
                    Event::JobStateChanged { job_id: changed, state } if changed == job_id => {
                        if state.is_terminal() {
                            screen.done();
                            if let Some(last) = &stats.last {
                                say(format_args!("{}", summary(last)));
                            }
                            if job.stats {
                                say(format_args!("{}", stats.report()));
                            }
                            return outcome(&state);
                        }
                        if state == JobState::Paused {
                            screen.line("paused");
                        }
                    }
                    _ => {}
                }
            }
            _ = tokio::signal::ctrl_c() => {
                screen.done();
                say(format_args!(
                    "stopped following; job {job_id} goes on in the core (cabinetos-cli job cancel {job_id} stops it)"
                ));
                return Ok(());
            }
        }
    }
}

fn outcome(state: &JobState) -> anyhow::Result<()> {
    match state {
        JobState::Completed => Ok(()),
        JobState::CompletedWithErrors => Err(anyhow!("the job ended with errors")),
        JobState::Cancelled => Err(anyhow!("the job was cancelled")),
        JobState::Failed { message } => Err(anyhow!("the job failed: {message}")),
        other => Err(anyhow!("the job ended in state {other:?}")),
    }
}

/// `jobs`: every job the core knows.
pub(crate) async fn list(client: &mut PipeClient) -> anyhow::Result<()> {
    let reply = send(client, Request::ListJobs).await?;
    let Response::Jobs { jobs } = reply.body else {
        return Err(failure("list_jobs", &reply.body));
    };
    if jobs.is_empty() {
        say(format_args!("no jobs"));
    }
    for job in &jobs {
        if !say(format_args!("{}", job_line(job))) {
            break;
        }
    }
    Ok(())
}

/// `job pause|resume|cancel`.
pub(crate) async fn control(
    client: &mut PipeClient,
    job_id: u64,
    action: JobAction,
) -> anyhow::Result<()> {
    let reply = send(client, Request::JobControl { job_id, action }).await?;
    if reply.body != Response::Ok {
        return Err(failure(&format!("job {job_id}"), &reply.body));
    }
    say(format_args!("ok"));
    Ok(())
}

/// `job resolve`.
pub(crate) async fn resolve(
    client: &mut PipeClient,
    job_id: u64,
    conflict_id: u64,
    resolution: Resolution,
) -> anyhow::Result<()> {
    let reply = send(
        client,
        Request::ResolveConflict {
            job_id,
            conflict_id,
            resolution,
            apply_to_same_kind: false,
        },
    )
    .await?;
    if reply.body != Response::Ok {
        return Err(failure(&format!("conflict {conflict_id}"), &reply.body));
    }
    say(format_args!("ok"));
    Ok(())
}

fn job_line(job: &JobInfo) -> String {
    let progress = &job.progress;
    let kind = match job.kind {
        JobKind::Copy => "copy",
        JobKind::Move => "move",
        JobKind::Delete { permanent: false } => "delete",
        JobKind::Delete { permanent: true } => "delete --permanent",
    };
    let target = job
        .destination
        .as_deref()
        .map(|destination| format!(" -> {destination}"))
        .unwrap_or_default();
    format!(
        "{:>4}  {:<22} {:<18} {:>3}%  files {}/{}  conflicts {}  {}{target}",
        progress.job_id,
        state_name(&progress.state),
        kind,
        percent(progress),
        progress.files_done,
        progress.files_total,
        progress.conflicts_open,
        job.sources.join(" "),
    )
}

fn state_name(state: &JobState) -> String {
    match state {
        JobState::Queued => "queued".to_owned(),
        JobState::Scanning => "scanning".to_owned(),
        JobState::Running => "running".to_owned(),
        JobState::Paused => "paused".to_owned(),
        JobState::Completed => "completed".to_owned(),
        JobState::CompletedWithErrors => "completed with errors".to_owned(),
        JobState::Cancelled => "cancelled".to_owned(),
        JobState::Failed { message } => format!("failed: {message}"),
    }
}

/// A conflict in one line; `recycling` when the job deletes to the Recycle
/// Bin.
fn describe_conflict(conflict: &Conflict, recycling: bool) -> String {
    let what = match &conflict.kind {
        ConflictKind::FileExists {
            source_size,
            dest_size,
            ..
        } => format!(
            "exists ({} here, {} there)",
            decimal(*source_size),
            decimal(*dest_size)
        ),
        ConflictKind::AccessDenied => "access denied".to_owned(),
        ConflictKind::SharingViolation => "in use by another program".to_owned(),
        ConflictKind::PathTooLong if recycling => {
            "a path in it is too long for the Recycle Bin; nothing was deleted".to_owned()
        }
        ConflictKind::PathTooLong => "path too long".to_owned(),
        ConflictKind::DiskFull => "disk full; the job is paused".to_owned(),
        ConflictKind::SourceVanished => "the source is gone".to_owned(),
        ConflictKind::RecycleBinTooSmall { size } => format!(
            "the Recycle Bin cannot take it ({}); nothing was deleted",
            decimal(*size)
        ),
        ConflictKind::Io { code, message } => format!("error {code}: {message}"),
    };
    let target = conflict
        .destination
        .as_deref()
        .map(|destination| format!(" -> {destination}"))
        .unwrap_or_default();
    format!(
        "conflict {}: {what}: {}{target}",
        conflict.conflict_id, conflict.source
    )
}

/// The answers that fit a conflict.
fn answers(kind: &ConflictKind, recycling: bool) -> &'static str {
    match kind {
        ConflictKind::RecycleBinTooSmall { .. } => "delete-permanently|skip",
        ConflictKind::PathTooLong if recycling => "delete-permanently|skip|retry",
        _ => "overwrite|skip|rename|retry",
    }
}

/// `45%`, by bytes when there are bytes to count, else by files.
fn percent(progress: &JobProgress) -> u64 {
    let (done, total) = if progress.bytes_total > 0 {
        (progress.bytes_done, progress.bytes_total)
    } else {
        (progress.files_done, progress.files_total)
    };
    if total == 0 {
        return if progress.state.is_terminal() { 100 } else { 0 };
    }
    (u128::from(done) * 100 / u128::from(total))
        .try_into()
        .unwrap_or(100)
}

/// `1.2 GB`: decimal units, as disk makers and the design use them.
#[expect(clippy::cast_precision_loss, reason = "a size rounded for display")]
fn decimal(bytes: u64) -> String {
    let units = ["B", "kB", "MB", "GB", "TB", "PB"];
    let mut value = bytes as f64;
    let mut unit = 0;
    while value >= 1000.0 && unit < units.len() - 1 {
        value /= 1000.0;
        unit += 1;
    }
    if unit == 0 {
        format!("{bytes} B")
    } else {
        format!("{value:.1} {}", units[unit])
    }
}

/// `45%  1.2 GB / 2.7 GB  610 MB/s  eta 3 s  files 8412/10001  conflicts 1`;
/// a job that moves no bytes (a delete, a move on one volume) shows its
/// pace instead: `84%  412 items/s  files 8412/10001`.
fn progress_line(progress: &JobProgress) -> String {
    use std::fmt::Write as _;
    let mut line = format!("{:>3}%", percent(progress));
    if progress.bytes_total > 0 {
        let _ = write!(
            line,
            "  {} / {}  {}/s",
            decimal(progress.bytes_done),
            decimal(progress.bytes_total),
            decimal(progress.speed_bps)
        );
    } else if let Some(rate) = progress.items_per_second.map(Rate::get)
        // The final record and a paused job carry 0: no pace to show.
        && rate > 0.0
    {
        let _ = if rate >= 10.0 {
            write!(line, "  {rate:.0} items/s")
        } else {
            write!(line, "  {rate:.1} items/s")
        };
    }
    if let Some(eta) = progress.eta_seconds {
        let _ = write!(line, "  eta {eta} s");
    }
    let _ = write!(
        line,
        "  files {}/{}",
        progress.files_done, progress.files_total
    );
    if progress.conflicts_open > 0 {
        let _ = write!(line, "  conflicts {}", progress.conflicts_open);
    }
    match progress.state {
        JobState::Queued => line.push_str("  queued"),
        JobState::Scanning => line.push_str("  scanning"),
        JobState::Paused => line.push_str("  paused"),
        _ => {}
    }
    line
}

/// The last line: what was done, in how long, how fast.
#[expect(
    clippy::cast_precision_loss,
    clippy::cast_possible_truncation,
    clippy::cast_sign_loss,
    reason = "a rate for display"
)]
fn summary(last: &JobProgress) -> String {
    let seconds = last.elapsed_ms as f64 / 1000.0;
    // A job that moved no bytes (a delete, a move on one volume) is
    // measured in files and folders.
    let moved_bytes = last.bytes_done > 0 || last.bytes_total > 0;
    let rate = match (seconds > 0.0, moved_bytes) {
        (false, _) => String::new(),
        (true, true) => format!(
            ", {}/s on average",
            decimal((last.bytes_done as f64 / seconds) as u64)
        ),
        (true, false) => format!(
            ", {:.0} items/s on average",
            last.files_done as f64 / seconds
        ),
    };
    let amount = if moved_bytes {
        format!("{} ", decimal(last.bytes_done))
    } else {
        String::new()
    };
    format!(
        "{}: {} of {} files and folders, {} skipped, {} failed; {amount}in {seconds:.1} s{rate}",
        state_name(&last.state),
        last.files_done,
        last.files_total,
        last.files_skipped,
        last.files_failed,
    )
}

/// Draws the progress: one line rewritten in place on a terminal; one line
/// per second otherwise.
struct Screen {
    terminal: bool,
    /// A progress line is on screen and must be cleared first.
    drawn: bool,
    last_printed: Option<Instant>,
    pending: Option<String>,
}

impl Screen {
    fn new() -> Self {
        Self {
            terminal: std::io::stdout().is_terminal(),
            drawn: false,
            last_printed: None,
            pending: None,
        }
    }

    fn progress(&mut self, progress: &JobProgress) {
        let line = progress_line(progress);
        if self.terminal {
            let mut out = std::io::stdout().lock();
            let _ = write!(out, "\r{line}\x1b[K").and_then(|()| out.flush());
            self.drawn = true;
        } else if self
            .last_printed
            .is_none_or(|printed| printed.elapsed() >= LINE_EVERY)
        {
            say(format_args!("{line}"));
            self.last_printed = Some(Instant::now());
            self.pending = None;
        } else {
            self.pending = Some(line);
        }
    }

    /// A line of its own, above the progress line.
    fn line(&mut self, text: &str) {
        if self.drawn {
            print!("\r\x1b[K");
            self.drawn = false;
        }
        say(format_args!("{text}"));
    }

    /// The last progress stays on screen.
    fn done(&mut self) {
        if self.drawn {
            println!();
            self.drawn = false;
        } else if let Some(line) = self.pending.take() {
            say(format_args!("{line}"));
        }
    }
}

/// Counts the progress events per second, the proof of the 30 Hz rule.
#[derive(Default)]
struct Stats {
    first: Option<Instant>,
    /// By second since the first event arrived.
    by_arrival: BTreeMap<u64, usize>,
    /// By second of the core's clock (`elapsed_ms`).
    by_core: BTreeMap<u64, usize>,
    count: usize,
    last: Option<JobProgress>,
}

impl Stats {
    fn record(&mut self, progress: &JobProgress) {
        let first = *self.first.get_or_insert_with(Instant::now);
        let second = first.elapsed().as_secs();
        *self.by_arrival.entry(second).or_default() += 1;
        *self.by_core.entry(progress.elapsed_ms / 1000).or_default() += 1;
        self.count += 1;
        self.last = Some(progress.clone());
    }

    fn report(&self) -> String {
        let busiest = |seconds: &BTreeMap<u64, usize>| seconds.values().copied().max().unwrap_or(0);
        let span = self
            .first
            .map_or(0.0, |first| first.elapsed().as_secs_f64());
        let per_second: Vec<String> = self.by_arrival.values().map(ToString::to_string).collect();
        format!(
            "progress events: {} in {span:.1} s; at most {} in one second as they arrived, at most {} by the core's clock; per second: {}",
            self.count,
            busiest(&self.by_arrival),
            busiest(&self.by_core),
            per_second.join(" "),
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn progress() -> JobProgress {
        JobProgress {
            job_id: 3,
            state: JobState::Running,
            bytes_done: 1_200_000_000,
            bytes_total: 2_700_000_000,
            files_done: 8412,
            files_total: 10_001,
            files_skipped: 0,
            files_failed: 0,
            conflicts_open: 1,
            current_path: None,
            speed_bps: 610_000_000,
            items_per_second: None,
            eta_seconds: Some(3),
            elapsed_ms: 2000,
        }
    }

    #[test]
    fn the_progress_line_has_the_documented_form() {
        assert_eq!(
            progress_line(&progress()),
            " 44%  1.2 GB / 2.7 GB  610.0 MB/s  eta 3 s  files 8412/10001  conflicts 1"
        );
        let mut delete = progress();
        delete.bytes_total = 0;
        delete.bytes_done = 0;
        delete.eta_seconds = None;
        delete.conflicts_open = 0;
        assert_eq!(progress_line(&delete), " 84%  files 8412/10001");
        // A job without bytes shows its pace in items.
        delete.items_per_second = Rate::new(412.25);
        assert_eq!(
            progress_line(&delete),
            " 84%  412 items/s  files 8412/10001"
        );
        delete.items_per_second = Rate::new(7.3);
        assert_eq!(
            progress_line(&delete),
            " 84%  7.3 items/s  files 8412/10001"
        );
        // A copy shows bytes per second, not items.
        let mut copy = progress();
        copy.items_per_second = Rate::new(412.25);
        assert!(!progress_line(&copy).contains("items/s"));
        // The final record (and a paused job) carries a pace of 0: nothing
        // to show.
        delete.items_per_second = Rate::new(0.0);
        assert_eq!(progress_line(&delete), " 84%  files 8412/10001");
    }

    #[test]
    fn the_summary_of_a_job_without_bytes_counts_items() {
        let mut delete = progress();
        delete.state = JobState::Completed;
        delete.bytes_total = 0;
        delete.bytes_done = 0;
        delete.files_done = 30_031;
        delete.files_total = 30_031;
        delete.elapsed_ms = 4400;
        assert_eq!(
            summary(&delete),
            "completed: 30031 of 30031 files and folders, 0 skipped, 0 failed; in 4.4 s, 6825 items/s on average"
        );
        let mut copy = progress();
        copy.state = JobState::Completed;
        assert_eq!(
            summary(&copy),
            "completed: 8412 of 10001 files and folders, 0 skipped, 0 failed; 1.2 GB in 2.0 s, 600.0 MB/s on average"
        );
    }

    #[test]
    fn decimal_units() {
        assert_eq!(decimal(999), "999 B");
        assert_eq!(decimal(1_200_000_000), "1.2 GB");
        assert_eq!(decimal(20_000_000_000), "20.0 GB");
    }
}
