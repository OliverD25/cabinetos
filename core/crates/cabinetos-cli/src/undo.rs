//! `undo <job>` and `undo --last`: the core reverses a job from its undo
//! journal as a new job, followed from here to its end (`docs/jobs.md`,
//! "Undo").

use anyhow::{Context, bail};
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{Envelope, Event, Request, Response, UndoLeftReason};

use crate::{expect_welcome, failure, say, send};

/// Asks for the undo, names what it cannot bring back, and waits for the
/// undo job to end.
pub(crate) async fn undo(client: &mut PipeClient, job: Option<u64>) -> anyhow::Result<()> {
    let mut events = client
        .events()
        .context("the event stream was already taken")?;
    expect_welcome(client).await?;
    let reply = send(client, Request::UndoJob { job }).await?;
    let Response::UndoStarted {
        job_id,
        undoes,
        left,
    } = reply.body
    else {
        return Err(failure("undo_job", &reply.body));
    };
    say(format_args!("job {job_id} undoes job {undoes}"));
    for item in &left {
        say(format_args!(
            "  stays: {} ({})",
            item.path,
            reason(item.reason)
        ));
    }
    loop {
        let Some(Envelope { body: event, .. }) = events.recv().await else {
            bail!("the connection to the core ended");
        };
        if let Event::JobStateChanged {
            job_id: changed,
            state,
        } = event
            && changed == job_id
            && state.is_terminal()
        {
            say(format_args!(
                "job {job_id}: {}",
                crate::jobs::state_word(&state)
            ));
            return crate::jobs::outcome(&state);
        }
    }
}

fn reason(reason: UndoLeftReason) -> &'static str {
    match reason {
        UndoLeftReason::InRecycleBin => "in the Recycle Bin; restore it from there",
        UndoLeftReason::DeletedForGood => "deleted for good",
        UndoLeftReason::NotSaved => "replaced without a saved copy",
        UndoLeftReason::SavedCopyRemoved => {
            "its saved copy was removed; the undo folder keeps at most 256 MiB"
        }
        UndoLeftReason::PutBack => "a saved copy an undo put back; that stays",
    }
}
