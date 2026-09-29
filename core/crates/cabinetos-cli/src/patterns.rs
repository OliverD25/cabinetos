//! `cabinetos-cli match`: the entries of a folder whose names match
//! patterns, as the window's pattern box (Num +, Num -) and its quick
//! search ask the core.

use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{Request, Response};

use crate::describe::names;
use crate::{expect_welcome, failure, say, send};

/// What to look for.
pub(crate) struct Search {
    pub(crate) patterns: String,
    pub(crate) files_only: bool,
    pub(crate) first_from: Option<u32>,
}

/// Lists `path`, asks which entries match, and prints them: how many, then
/// each range with its first and last name; or, with `first_from`, the
/// first match.
pub(crate) async fn run(
    client: &mut PipeClient,
    path: &str,
    search: &Search,
) -> anyhow::Result<()> {
    expect_welcome(client).await?;
    let reply = send(
        client,
        Request::ListDirectory {
            path: path.to_owned(),
            include_hidden: None,
            sort: None,
            watch: false,
        },
    )
    .await?;
    let Response::ListingOpened {
        listing_id,
        section_handle,
        ..
    } = reply.body
    else {
        return Err(failure(path, &reply.body));
    };
    let names = names(client, section_handle)?;
    let reply = send(
        client,
        Request::MatchEntries {
            listing_id,
            patterns: search.patterns.clone(),
            files_only: search.files_only,
            first_from: search.first_from,
        },
    )
    .await?;
    let Response::EntryMatches {
        generation, ranges, ..
    } = reply.body
    else {
        return Err(failure(&search.patterns, &reply.body));
    };
    let name = |index: u32| {
        usize::try_from(index)
            .ok()
            .and_then(|index| names.get(index))
            .map_or("?", String::as_str)
    };
    if search.first_from.is_some() {
        match ranges.first() {
            Some(&[index, _]) => say(format_args!("{index}: {}", name(index))),
            None => say(format_args!("no match")),
        };
    } else {
        let matched: u64 = ranges.iter().map(|&[_, count]| u64::from(count)).sum();
        say(format_args!(
            "{matched} of {} entries match, in {} ranges (generation {generation})",
            names.len(),
            ranges.len()
        ));
        for &[start, count] in &ranges {
            let last = start + count - 1;
            let shown = if count == 1 {
                format!("[{start}, 1] {}", name(start))
            } else {
                format!("[{start}, {count}] {} .. {}", name(start), name(last))
            };
            if !say(format_args!("{shown}")) {
                break;
            }
        }
    }
    let closed = send(client, Request::CloseListing { listing_id }).await?;
    if closed.body != Response::Ok {
        return Err(failure(path, &closed.body));
    }
    Ok(())
}
