//! `cabinetos-cli ls`: a listing read from shared memory, like the UI will.

use std::io::{self, BufWriter, Write};
use std::time::{Duration, Instant};

use anyhow::{Context, bail};
use cabinetos_fs::time::local_time;
use cabinetos_fs::{EntryView, ListingReader};
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::shm::EntryKind;
use cabinetos_protocol::{Envelope, Event, RefreshReason, Request, Response, SortSpec};

use crate::{expect_welcome, failure, say, send};

/// What `ls` was asked to do.
pub(crate) struct LsArgs {
    pub(crate) path: String,
    pub(crate) long: bool,
    /// `None` leaves it to the core's configuration.
    pub(crate) include_hidden: Option<bool>,
    /// `None` leaves it to the core's configuration.
    pub(crate) sort: Option<SortSpec>,
    pub(crate) watch: bool,
}

/// Lists a directory through the core, prints it, and with `watch` keeps
/// printing refreshes until Ctrl+C.
pub(crate) async fn ls(client: &mut PipeClient, args: LsArgs) -> anyhow::Result<()> {
    let mut events = client
        .events()
        .context("the event stream was already taken")?;
    expect_welcome(client).await?;
    let reply = send(
        client,
        Request::ListDirectory {
            path: args.path.clone(),
            include_hidden: args.include_hidden,
            sort: args.sort,
            watch: args.watch,
        },
    )
    .await?;
    let Response::ListingOpened {
        listing_id,
        section_handle,
        entry_count,
        elapsed_us,
        ..
    } = reply.body
    else {
        return Err(failure(&args.path, &reply.body));
    };

    let started = Instant::now();
    let entries = read_section(client, section_handle)?;
    let client_time = started.elapsed();

    let open = print_entries(&entries, args.long)
        && say(format_args!(
            "core: enumerated {entry_count} entries in {:.1} ms",
            Duration::from_micros(elapsed_us).as_secs_f64() * 1000.0
        ))
        && say(format_args!(
            "client: mapped and read in {:.1} ms",
            client_time.as_secs_f64() * 1000.0
        ));

    if args.watch && open {
        loop {
            tokio::select! {
                event = events.recv() => match event {
                    Some(Envelope { body: Event::ListingRefreshed {
                        listing_id: refreshed, section_handle, entry_count, generation, reason, ..
                    }, .. }) if refreshed == listing_id => {
                        // Taking the handle and dropping it closes this
                        // process's copy, so old listings do not pile up.
                        drop(client.take_section(section_handle));
                        if !say(format_args!(
                            "refreshed: {entry_count} entries, generation {generation}, reason {}",
                            reason_name(reason)
                        )) {
                            break;
                        }
                    }
                    Some(Envelope { body: Event::ListingLost { listing_id: lost, message }, .. })
                        if lost == listing_id =>
                    {
                        say(format_args!("lost: {message}"));
                        break;
                    }
                    Some(_) => {}
                    None => bail!("the connection to the core ended"),
                },
                _ = tokio::signal::ctrl_c() => break,
            }
        }
    }

    let closed = send(client, Request::CloseListing { listing_id }).await?;
    if closed.body != Response::Ok {
        return Err(failure(&args.path, &closed.body));
    }
    Ok(())
}

/// Maps the section read-only and decodes every entry.
fn read_section(client: &PipeClient, section_handle: u64) -> anyhow::Result<Vec<EntryView>> {
    let section = client.take_section(section_handle)?;
    let view = section.map_readonly()?;
    let reader = ListingReader::new(view.as_slice())?;
    Ok(reader.entries().collect::<Result<_, _>>()?)
}

/// Prints the entries. Returns `false` once stdout is closed (piped into
/// `head`, say).
fn print_entries(entries: &[EntryView], long: bool) -> bool {
    let mut out = BufWriter::new(io::stdout().lock());
    for entry in entries {
        let written = if long {
            writeln!(
                out,
                "{} {} {} {:>15} {}",
                glyph(entry.kind),
                attribute_letters(entry.meta.attributes),
                modified(entry.meta.modified),
                size(entry),
                entry.name
            )
        } else {
            writeln!(out, "{} {}", glyph(entry.kind), entry.name)
        };
        if written.is_err() {
            return false;
        }
    }
    out.flush().is_ok()
}

/// `d` directory, `l` link, `-` file, `?` unknown, as in `ls -l`.
fn glyph(kind: EntryKind) -> char {
    match kind {
        EntryKind::Directory => 'd',
        EntryKind::ReparsePoint => 'l',
        EntryKind::File => '-',
        EntryKind::Unknown => '?',
    }
}

/// Read-only, hidden, system, archive, link (reparse point).
fn attribute_letters(attributes: u32) -> String {
    [
        (0x1, 'R'),
        (0x2, 'H'),
        (0x4, 'S'),
        (0x20, 'A'),
        (0x400, 'L'),
    ]
    .iter()
    .map(|&(bit, letter)| if attributes & bit != 0 { letter } else { '-' })
    .collect()
}

fn modified(ticks: i64) -> String {
    local_time(ticks).map_or_else(
        || "----------------".to_owned(),
        |t| {
            format!(
                "{:04}-{:02}-{:02} {:02}:{:02}",
                t.year, t.month, t.day, t.hour, t.minute
            )
        },
    )
}

fn size(entry: &EntryView) -> String {
    if entry.kind == EntryKind::Directory || entry.meta.attributes & 0x10 != 0 {
        "<DIR>".to_owned()
    } else {
        group_thousands(entry.meta.size)
    }
}

/// `1234567` → `1 234 567`.
fn group_thousands(value: u64) -> String {
    let digits = value.to_string();
    let mut grouped = String::with_capacity(digits.len() + digits.len() / 3);
    for (index, digit) in digits.chars().enumerate() {
        if index > 0 && (digits.len() - index).is_multiple_of(3) {
            grouped.push(' ');
        }
        grouped.push(digit);
    }
    grouped
}

fn reason_name(reason: RefreshReason) -> &'static str {
    match reason {
        RefreshReason::Changed => "changed",
        RefreshReason::Overflow => "overflow",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_sizes_and_attributes() {
        assert_eq!(group_thousands(0), "0");
        assert_eq!(group_thousands(999), "999");
        assert_eq!(group_thousands(1234), "1 234");
        assert_eq!(group_thousands(1_234_567), "1 234 567");
        assert_eq!(attribute_letters(0x20), "---A-");
        assert_eq!(attribute_letters(0x1 | 0x2 | 0x4 | 0x20 | 0x400), "RHSAL");
        assert_eq!(glyph(EntryKind::ReparsePoint), 'l');
        assert_eq!(modified(0), "----------------");
    }
}
