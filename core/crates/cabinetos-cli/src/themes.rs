//! `cabinetos-cli themes`: the colour themes in the core's themes folder,
//! and the one in effect (`docs/themes.md`).

use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{Request, Response, Theme, ThemeInfo, ThemeKind};

use crate::{failure, say, send};

/// `themes list`: every valid theme, the one in effect marked with `*`.
pub(crate) async fn list(client: &mut PipeClient) -> anyhow::Result<()> {
    let reply = send(client, Request::ListThemes).await?;
    let Response::Themes { themes } = reply.body else {
        return Err(failure("list_themes", &reply.body));
    };
    let in_effect = get(client, None).await?.id;
    if themes.is_empty() {
        say(format_args!("no themes"));
        return Ok(());
    }
    for theme in &themes {
        if !say(format_args!("{}", line(theme, theme.id == in_effect))) {
            break;
        }
    }
    Ok(())
}

/// One theme in one line.
fn line(theme: &ThemeInfo, in_effect: bool) -> String {
    let kind = match theme.kind {
        ThemeKind::Dark => "dark",
        ThemeKind::Light => "light",
    };
    let accent = theme
        .accent
        .as_ref()
        .map_or("system", |accent| accent.as_str());
    format!(
        "{} {:<18} {:<18} {kind:<5} accent {accent:<7}  {} by {}",
        if in_effect { '*' } else { ' ' },
        theme.id,
        theme.name,
        theme.version,
        theme.author
    )
}

/// `themes show`: the whole theme `id`, or the one in effect, as JSON.
pub(crate) async fn show(client: &mut PipeClient, id: Option<&str>) -> anyhow::Result<()> {
    let theme = get(client, id).await?;
    say(format_args!("{}", serde_json::to_string_pretty(&theme)?));
    Ok(())
}

async fn get(client: &mut PipeClient, id: Option<&str>) -> anyhow::Result<Theme> {
    let reply = send(
        client,
        Request::GetTheme {
            theme_id: id.map(str::to_owned),
        },
    )
    .await?;
    match reply.body {
        Response::Theme { theme } => Ok(*theme),
        other => Err(failure(id.unwrap_or("get_theme"), &other)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_theme_reads_as_one_line() {
        let theme: ThemeInfo = serde_json::from_value(serde_json::json!({
            "id": "nord", "name": "Nord", "author": "CabinetOS", "version": "1.0.0",
            "kind": "dark", "accent": "#88C0D0"
        }))
        .unwrap();
        assert_eq!(
            line(&theme, true),
            "* nord               Nord               dark  accent #88C0D0  1.0.0 by CabinetOS"
        );
        let plain = ThemeInfo {
            accent: None,
            ..theme
        };
        assert!(line(&plain, false).starts_with("  nord"));
        assert!(line(&plain, false).contains("accent system "));
    }
}
