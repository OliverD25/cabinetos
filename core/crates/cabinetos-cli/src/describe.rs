//! `cabinetos-cli describe` and `icon`: the shell's type names and icons,
//! as the file panes ask for them.

use std::path::Path;
use std::time::Instant;

use anyhow::{Context, bail};
use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use cabinetos_fs::ListingReader;
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{Request, Response};

use crate::{expect_welcome, failure, say, send};

/// `describe <path>`: lists the folder, asks for the details of entries
/// `from..from + count`, and prints each with its name.
pub(crate) async fn describe(
    client: &mut PipeClient,
    path: &str,
    from: u32,
    count: u32,
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

    let started = Instant::now();
    let reply = send(
        client,
        Request::DescribeEntries {
            listing_id,
            from,
            count,
        },
    )
    .await?;
    let round_trip = started.elapsed();
    let Response::EntryDetails { details, .. } = reply.body else {
        return Err(failure(path, &reply.body));
    };
    for (offset, detail) in details.iter().enumerate() {
        let index = from as usize + offset;
        let name = names.get(index).map_or("?", String::as_str);
        // The name goes last: padding counts characters, and a wide one
        // (日本語) or a combining accent would shift the columns after it.
        if !say(format_args!(
            "{index:>5}  {:<28} {:<21} {name}",
            detail.type_name, detail.icon_key
        )) {
            return Ok(());
        }
    }
    say(format_args!(
        "{} of {} entries described in {:.1} ms (round trip)",
        details.len(),
        names.len(),
        round_trip.as_secs_f64() * 1000.0
    ));
    let closed = send(client, Request::CloseListing { listing_id }).await?;
    if closed.body != Response::Ok {
        return Err(failure(path, &closed.body));
    }
    Ok(())
}

/// Every name in the listing's section, in order.
pub(crate) fn names(client: &PipeClient, section_handle: u64) -> anyhow::Result<Vec<String>> {
    let section = client.take_section(section_handle)?;
    let view = section.map_readonly()?;
    let reader = ListingReader::new(view.as_slice())?;
    reader
        .entries()
        .map(|entry| Ok(entry?.name))
        .collect::<anyhow::Result<_>>()
}

/// `icon <key>`: writes the icon's PNG to `out` and prints its size.
pub(crate) async fn icon(
    client: &mut PipeClient,
    key: &str,
    size: u32,
    out: &Path,
) -> anyhow::Result<()> {
    let reply = send(
        client,
        Request::GetIcon {
            key: key.to_owned(),
            size,
        },
    )
    .await?;
    let Response::Icon { png_base64, .. } = reply.body else {
        return Err(failure(key, &reply.body));
    };
    let png = BASE64
        .decode(png_base64)
        .context("the icon is not valid base64")?;
    let (width, height) = png_size(&png).context("the icon is not a PNG")?;
    std::fs::write(out, &png).with_context(|| format!("cannot write {}", out.display()))?;
    say(format_args!(
        "{}: {width}x{height} PNG, {} bytes",
        out.display(),
        png.len()
    ));
    Ok(())
}

/// The width and height a PNG's header gives.
fn png_size(png: &[u8]) -> anyhow::Result<(u32, u32)> {
    const SIGNATURE: [u8; 8] = [0x89, b'P', b'N', b'G', b'\r', b'\n', 0x1a, b'\n'];
    if png.len() < 24 || png[..8] != SIGNATURE || &png[12..16] != b"IHDR" {
        bail!("no PNG signature and header");
    }
    let number = |at: usize| u32::from_be_bytes([png[at], png[at + 1], png[at + 2], png[at + 3]]);
    Ok((number(16), number(20)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_a_png_s_size_from_its_header() {
        let mut png = vec![0x89, b'P', b'N', b'G', b'\r', b'\n', 0x1a, b'\n'];
        png.extend_from_slice(&13u32.to_be_bytes());
        png.extend_from_slice(b"IHDR");
        png.extend_from_slice(&32u32.to_be_bytes());
        png.extend_from_slice(&24u32.to_be_bytes());
        assert_eq!(png_size(&png).unwrap(), (32, 24));
        assert!(png_size(b"GIF89a").is_err());
    }
}
