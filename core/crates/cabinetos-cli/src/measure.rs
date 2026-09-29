//! `cabinetos-cli measure`: the files, folders and bytes under paths,
//! counted by the core, with its progress as it comes. Ctrl+C asks the
//! core to stop, and the paths counted so far are still printed.

use std::fmt::Write as _;

use anyhow::{Context, bail};
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{Envelope, Event, MeasureResult, Request, Response};

use crate::{binary_size, failure, say, send};

/// Counts `paths` and prints each total.
pub(crate) async fn measure(client: &mut PipeClient, paths: Vec<String>) -> anyhow::Result<()> {
    let mut events = client
        .events()
        .context("the event stream was already taken")?;
    let reply = send(client, Request::MeasurePaths { paths }).await?;
    let Response::MeasureStarted { measure_id } = reply.body else {
        return Err(failure("measure_paths", &reply.body));
    };
    say(format_args!("measure {measure_id} started"));
    let mut cancelling = false;
    loop {
        tokio::select! {
            event = events.recv() => {
                let Some(Envelope { body: event, .. }) = event else {
                    bail!("the connection to the core ended");
                };
                match event {
                    Event::MeasureProgress { measure_id: id, path, files, folders, bytes }
                        if id == measure_id =>
                    {
                        say(format_args!(
                            "  counting {path}: {files} files, {folders} folders, {bytes} bytes"
                        ));
                    }
                    Event::MeasureFinished { measure_id: id, results, cancelled }
                        if id == measure_id =>
                    {
                        for result in &results {
                            say(format_args!("{}", result_line(result)));
                        }
                        if cancelled {
                            bail!("measure {measure_id} was cancelled");
                        }
                        say(format_args!("measure {measure_id} finished"));
                        return Ok(());
                    }
                    _ => {}
                }
            }
            _ = tokio::signal::ctrl_c(), if !cancelling => {
                cancelling = true;
                let reply = send(client, Request::CancelMeasure { measure_id }).await?;
                if reply.body != Response::Ok {
                    return Err(failure("cancel_measure", &reply.body));
                }
            }
        }
    }
}

/// `C:\photos: 5310 files, 120 folders, 18400000000 bytes (17.14 GiB)`,
/// and how many folders could not be read, when any.
fn result_line(result: &MeasureResult) -> String {
    let mut line = format!(
        "{}: {} files, {} folders, {} bytes ({})",
        result.path,
        result.files,
        result.folders,
        result.bytes,
        binary_size(result.bytes)
    );
    if result.unreadable > 0 {
        let _ = write!(line, ", {} unreadable", result.unreadable);
    }
    line
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_result_is_one_line_with_the_unreadable_folders_when_any() {
        let mut result = MeasureResult {
            path: r"C:\photos".to_owned(),
            files: 5310,
            folders: 120,
            bytes: 18_400_000_000,
            unreadable: 0,
        };
        assert_eq!(
            result_line(&result),
            r"C:\photos: 5310 files, 120 folders, 18400000000 bytes (17.14 GiB)"
        );
        result.unreadable = 2;
        assert!(result_line(&result).ends_with(", 2 unreadable"));
    }
}
