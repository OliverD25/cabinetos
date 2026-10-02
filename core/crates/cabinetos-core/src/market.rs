//! The marketplace as the core serves it (`docs/marketplace.md`): the two
//! catalogues a client browses, and installs and uninstalls with what
//! follows them.
//!
//! - The marketplace has two catalogues (ADR 0022): `index.json` for the
//!   extensions (`marketplace.index`) and `themes.json` for the themes
//!   (`marketplace.themes`). Each is read, cached and answered on its own,
//!   so a themes server that is down does not hide the extensions.
//! - The core reads a catalogue only when a client asks: `marketplace_refresh`,
//!   or a search or install before it was read (or after its address
//!   changed). There is no background refresh.
//! - Every plugin install, an update too, arrives as `needs_review`: the
//!   core clears the plugin's grants before the new files are in place, and
//!   the plugin host reloads it.
//! - A theme install applies at once when `ui.theme` names it; a tool
//!   install or uninstall sends `tools_changed`.

use std::path::Path;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use cabinetos_market::{Index, Installed, Market, MarketError, Source};
use cabinetos_plugins::PluginHost;
use cabinetos_plugins::manifest::{self, Capability};
use cabinetos_protocol::{
    CapabilityLevel, Catalogue, ErrorCode, Event, ExtensionKind, MarketItem, Response, ToolInfo,
};

use crate::CORE_VERSION;
use crate::events::EventHub;
use crate::settings::Settings;
use crate::themes::Themes;

/// Progress events of one download: at most 30 a second.
const PROGRESS_INTERVAL: Duration = Duration::from_millis(33);

/// A catalogue read last, with the settings it came from.
type Read = Option<(String, Arc<Index>)>;

/// The marketplace client and what an install touches.
pub(crate) struct Marketplace {
    market: Market,
    /// The extensions read last (plugins and tools), with the
    /// `marketplace.index` they came from.
    read_extensions: Mutex<Read>,
    /// The themes read last, with the `marketplace.themes` and
    /// `marketplace.index` they came from: while `themes.json` is missing
    /// the themes are the index's own, so both settings decide.
    read_themes: Mutex<Read>,
    settings: Arc<Settings>,
    events: Arc<EventHub>,
    /// `None` when the plugin host could not start.
    plugins: Option<Arc<PluginHost>>,
    themes: Arc<Themes>,
}

impl Marketplace {
    pub(crate) fn new(
        market: Market,
        settings: Arc<Settings>,
        events: Arc<EventHub>,
        plugins: Option<Arc<PluginHost>>,
        themes: Arc<Themes>,
    ) -> Self {
        Self {
            market,
            read_extensions: Mutex::new(None),
            read_themes: Mutex::new(None),
            settings,
            events,
            plugins,
            themes,
        }
    }

    /// The reply to `marketplace_refresh`: reads the catalogue now.
    /// Blocking.
    pub(crate) fn refresh(&self, catalogue: Catalogue) -> Response {
        match self.read(catalogue) {
            Ok(index) => index_reply(&index, self.market.offers(&index.items)),
            Err(error) => failure(error),
        }
    }

    /// The reply to `marketplace_search`. Blocking when it reads the
    /// catalogue.
    pub(crate) fn search(
        &self,
        query: &str,
        kind: Option<ExtensionKind>,
        catalogue: Catalogue,
    ) -> Response {
        match self.current(catalogue) {
            Ok(index) => {
                let offered = self.market.offers(&index.items);
                index_reply(&index, cabinetos_market::search(&offered, query, kind))
            }
            Err(error) => failure(error),
        }
    }

    /// Every installed Tool Extension. Blocking: it reads their folders.
    pub(crate) fn tools(&self) -> Vec<ToolInfo> {
        self.market.tools()
    }

    /// The reply to `install_extension`; `install_progress` and
    /// `install_finished` tell every client how it goes. Blocking: it
    /// downloads.
    pub(crate) fn install(&self, id: &str, version: Option<&str>) -> Response {
        let outcome = self.try_install(id, version);
        let (ok, message) = match &outcome {
            Ok(message) => (true, message.clone()),
            Err(error) => (false, error.message.clone()),
        };
        if !ok {
            tracing::warn!(id, error = %message, "install failed");
        }
        // From the record of installs, which a failed install leaves as it
        // was: every client learns what is installed now, whoever asked.
        let installed_version = self
            .market
            .installed()
            .remove(id)
            .map(|record| record.version);
        self.events.publish(Event::InstallFinished {
            extension_id: id.to_owned(),
            ok,
            message,
            installed_version,
        });
        match outcome {
            Ok(_) => Response::Ok,
            Err(error) => failure(error),
        }
    }

    fn try_install(&self, id: &str, version: Option<&str>) -> Result<String, MarketError> {
        let index = self.index_of(id)?;
        let item = self.market.choose(&index, id, version)?;
        if item.kind == ExtensionKind::Plugin {
            self.clear_grants(id)?;
        }
        let allow_insecure = self.settings.snapshot().config.marketplace.allow_insecure;
        let check_plugin = |dir: &Path| manifest::read(dir, CORE_VERSION).map(|_| ());
        let mut last_sent: Option<Instant> = None;
        let mut progress = |bytes: u64, total: u64, done: bool| {
            if done || last_sent.is_none_or(|sent| sent.elapsed() >= PROGRESS_INTERVAL) {
                last_sent = Some(Instant::now());
                self.events.publish(Event::InstallProgress {
                    extension_id: id.to_owned(),
                    bytes,
                    total,
                });
            }
        };
        let installed =
            self.market
                .install(&index, item, allow_insecure, &check_plugin, &mut progress)?;
        self.after_install(id, &installed);
        Ok(format!(
            "installed {id} {} ({})",
            installed.version,
            kind_name(installed.kind)
        ))
    }

    /// A plugin's earlier grants must not carry over to what is installed
    /// now: every install waits for the user's review. Done before the new
    /// files are in place, so a failure here installs nothing.
    fn clear_grants(&self, id: &str) -> Result<(), MarketError> {
        let granted = self
            .settings
            .snapshot()
            .config
            .plugins
            .get(id)
            .is_some_and(|settings| !settings.granted.is_empty());
        if !granted {
            return Ok(());
        }
        if let Response::Error { message, .. } = self
            .settings
            .update_plugin(id, |settings| settings.granted.clear())
        {
            return Err(MarketError::new(
                ErrorCode::MarketplaceError,
                format!("cannot clear the grants of `{id}` before installing it: {message}"),
            ));
        }
        Ok(())
    }

    fn after_install(&self, id: &str, installed: &Installed) {
        match installed.kind {
            ExtensionKind::Plugin => {
                if let Some(host) = &self.plugins
                    && let Err(error) = host.reload(id)
                {
                    tracing::warn!(id, %error, "the installed plugin cannot be loaded");
                }
            }
            ExtensionKind::Theme => self.themes.refresh(),
            ExtensionKind::Tool => self.events.publish(Event::ToolsChanged {
                tools: self.market.tools(),
            }),
        }
    }

    /// The reply to `uninstall_extension`. Blocking.
    pub(crate) fn uninstall(&self, id: &str) -> Response {
        let result = self.market.uninstall(id, |installed| match installed.kind {
            ExtensionKind::Theme if self.themes.in_use(id) => Err(MarketError::new(
                ErrorCode::MarketplaceError,
                format!("`{id}` is the theme in effect (ui.theme); choose another theme first"),
            )),
            ExtensionKind::Plugin => {
                if let Some(host) = &self.plugins {
                    host.remove(id);
                }
                Ok(())
            }
            _ => Ok(()),
        });
        match result {
            Ok(installed) => {
                if installed.kind == ExtensionKind::Tool {
                    self.events.publish(Event::ToolsChanged {
                        tools: self.market.tools(),
                    });
                }
                Response::Ok
            }
            Err(error) => {
                // Some files could not be removed: a plugin still in its
                // folder comes back, so the list tells the truth.
                if let Some(host) = &self.plugins
                    && !host.contains(id)
                    && self
                        .market
                        .dirs()
                        .plugins
                        .join(id)
                        .join(manifest::MANIFEST_FILE)
                        .is_file()
                {
                    let _ = host.reload(id);
                }
                failure(error)
            }
        }
    }

    /// The catalogue read last, or read now when there is none or its
    /// settings changed since.
    fn current(&self, catalogue: Catalogue) -> Result<Arc<Index>, MarketError> {
        let key = self.key(catalogue);
        if let Some((read_from, index)) = &*self.lock(catalogue)
            && *read_from == key
        {
            return Ok(Arc::clone(index));
        }
        self.read(catalogue)
    }

    /// Where the extension `id` is offered, for an install: the extensions'
    /// index, else the themes catalogue, so the command line installs a
    /// theme by its ID too. The themes catalogue is read only when the
    /// extensions do not have the ID. An ID that neither has gives the
    /// extensions' index, whose `choose` says there is no such extension.
    fn index_of(&self, id: &str) -> Result<Arc<Index>, MarketError> {
        let has = |index: &Arc<Index>| index.items.iter().any(|item| item.id == id);
        let extensions = self.current(Catalogue::Extensions)?;
        if has(&extensions) {
            return Ok(extensions);
        }
        let themes = self.current(Catalogue::Themes)?;
        Ok(if has(&themes) { themes } else { extensions })
    }

    fn read(&self, catalogue: Catalogue) -> Result<Arc<Index>, MarketError> {
        let key = self.key(catalogue);
        let config = self.settings.snapshot().config.marketplace.clone();
        let index = match catalogue {
            Catalogue::Extensions => {
                let source = Source::parse(&config.index, config.allow_insecure)?;
                self.market
                    .fetch(&source, config.allow_insecure)?
                    .only(Catalogue::Extensions)
            }
            Catalogue::Themes => {
                let themes = Source::parse_themes(&config.themes, config.allow_insecure)?;
                self.market.fetch_themes(
                    &themes,
                    || Source::parse(&config.index, config.allow_insecure),
                    config.allow_insecure,
                )?
            }
        };
        let index = Arc::new(index);
        tracing::info!(
            catalogue = ?catalogue,
            source = %index.source,
            items = index.items.len(),
            "marketplace catalogue read"
        );
        *self.lock(catalogue) = Some((key, Arc::clone(&index)));
        Ok(index)
    }

    /// The settings a catalogue read depends on, to tell when one is stale.
    fn key(&self, catalogue: Catalogue) -> String {
        let config = &self.settings.snapshot().config.marketplace;
        match catalogue {
            Catalogue::Extensions => config.index.clone(),
            Catalogue::Themes => format!("{}\n{}", config.themes, config.index),
        }
    }

    fn lock(&self, catalogue: Catalogue) -> MutexGuard<'_, Read> {
        match catalogue {
            Catalogue::Extensions => &self.read_extensions,
            Catalogue::Themes => &self.read_themes,
        }
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
    }
}

/// `marketplace_index` with `items`, each capability's level filled in.
fn index_reply(index: &Index, mut items: Vec<MarketItem>) -> Response {
    for item in &mut items {
        for capability in &mut item.capabilities {
            capability.level = Some(
                Capability::parse(&capability.name)
                    .map_or(CapabilityLevel::High, Capability::level),
            );
        }
    }
    Response::MarketplaceIndex {
        items,
        source: index.source.clone(),
        fetched_at_ms: index.fetched_at_ms,
    }
}

fn failure(error: MarketError) -> Response {
    Response::Error {
        code: error.code,
        message: error.message,
    }
}

fn kind_name(kind: ExtensionKind) -> &'static str {
    match kind {
        ExtensionKind::Plugin => "plugin",
        ExtensionKind::Theme => "theme",
        ExtensionKind::Tool => "tool",
    }
}
