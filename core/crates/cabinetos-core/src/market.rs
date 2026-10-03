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
//!   install or uninstall sends `tools_changed`, and then
//!   `quick_view_table_changed` when the Quick View table changed with it.
//! - The Quick View table (ADR 0023, decision 1) is built from the tools'
//!   `quickView` blocks at start, after a tool install or uninstall, and
//!   after `quickView.viewers` changes.

use std::collections::{BTreeSet, HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use cabinetos_market::{Index, Installed, Market, MarketError, QuickViewTable, Source};
use cabinetos_plugins::PluginHost;
use cabinetos_plugins::manifest::{self, Capability};
use cabinetos_protocol::{
    CapabilityLevel, Catalogue, ErrorCode, Event, ExtensionKind, MarketItem, OfferReason, Response,
    Theme, ToolInfo,
};

use crate::CORE_VERSION;
use crate::events::EventHub;
use crate::settings::{Settings, Snapshot};
use crate::themes::Themes;

/// Progress events of one download: at most 30 a second.
const PROGRESS_INTERVAL: Duration = Duration::from_millis(33);

/// How many previewed themes the core keeps (about 2 KB each): the whole
/// collection and some to spare. A full memory starts over.
const PREVIEWS_KEPT: usize = 64;

/// How old the cached extensions' index may be for the Quick View offer
/// before the core reads it from the web again (ADR 0023, decision 5.1).
const OFFER_CACHE_AGE: Duration = Duration::from_hours(7 * 24);

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
    /// Themes read for a preview, by ID, version and hash.
    previews: Mutex<HashMap<String, Theme>>,
    settings: Arc<Settings>,
    events: Arc<EventHub>,
    /// `None` when the plugin host could not start.
    plugins: Option<Arc<PluginHost>>,
    themes: Arc<Themes>,
    /// The window's tools folder in development (`--dev-tools-dir`), listed
    /// first for Quick View.
    dev_tools: Option<PathBuf>,
    /// The Quick View table built last. The lock is held while one is
    /// built, so two builds never cross.
    quick_view: Mutex<Option<Arc<QuickViewTable>>>,
    /// The `marketplace.index` addresses the Quick View offer read from
    /// the web in this session: at most once each.
    offer_reads: Mutex<HashSet<String>>,
}

impl Marketplace {
    pub(crate) fn new(
        market: Market,
        settings: Arc<Settings>,
        events: Arc<EventHub>,
        plugins: Option<Arc<PluginHost>>,
        themes: Arc<Themes>,
        dev_tools: Option<PathBuf>,
    ) -> Self {
        Self {
            market,
            read_extensions: Mutex::new(None),
            read_themes: Mutex::new(None),
            previews: Mutex::new(HashMap::new()),
            settings,
            events,
            plugins,
            themes,
            dev_tools,
            quick_view: Mutex::new(None),
            offer_reads: Mutex::new(HashSet::new()),
        }
    }

    /// The reply to `quick_view_offer`: the first tool of the extensions
    /// catalogue, in its order, that claims `name`, that this core can run
    /// and that is not installed. Blocking: it reads the record of installs
    /// and the tools folders, and may read the catalogue.
    pub(crate) fn quick_view_offer(&self, name: &str) -> Response {
        let Some(index) = self.offer_catalogue() else {
            return Response::QuickViewOffer {
                item: None,
                reason: Some(OfferReason::Offline),
            };
        };
        let mut installed: BTreeSet<String> = self.market.installed().into_keys().collect();
        installed.extend(self.market.tools().into_iter().map(|tool| tool.id));
        if let Some(dev) = &self.dev_tools {
            installed.extend(
                cabinetos_market::list_tools(dev)
                    .into_iter()
                    .map(|tool| tool.id),
            );
        }
        let offered = self.market.offers(&index.items);
        match cabinetos_market::quick_view_offer(name, &offered, &installed) {
            Some(item) => Response::QuickViewOffer {
                item: Some(item),
                reason: None,
            },
            None => Response::QuickViewOffer {
                item: None,
                reason: Some(OfferReason::NoItem),
            },
        }
    }

    /// The extensions catalogue for the offer, `None` when it cannot be
    /// read: the one read in this session for the current address; else a
    /// catalogue on this machine from disk; else the marketplace's cached
    /// copy while it is under seven days old; else the web, once per
    /// session and address.
    fn offer_catalogue(&self) -> Option<Arc<Index>> {
        let key = self.key(Catalogue::Extensions);
        if let Some((read_from, index)) = &*self.lock(Catalogue::Extensions)
            && *read_from == key
        {
            return Some(Arc::clone(index));
        }
        let config = self.settings.snapshot().config.marketplace.clone();
        let source = Source::parse(&config.index, config.allow_insecure)
            .inspect_err(|error| {
                tracing::info!(error = %error.message, "no catalogue for the quick view offer");
            })
            .ok()?;
        if let Source::Local(_) = source {
            return self
                .market
                .fetch(&source, config.allow_insecure)
                .inspect_err(|error| {
                    tracing::info!(error = %error.message, "no catalogue for the quick view offer");
                })
                .ok()
                .map(|index| Arc::new(index.only(Catalogue::Extensions)));
        }
        if let Some(index) = self.market.cached_index(&source, OFFER_CACHE_AGE) {
            return Some(Arc::new(index.only(Catalogue::Extensions)));
        }
        let first = self
            .offer_reads
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .insert(key);
        if !first {
            return None;
        }
        tracing::info!(
            source = %config.index,
            "the quick view offer reads the extensions catalogue (once this session)"
        );
        self.read(Catalogue::Extensions)
            .inspect_err(|error| {
                tracing::info!(error = %error.message, "no catalogue for the quick view offer");
            })
            .ok()
    }

    /// The reply to `quick_view_table`. Blocking when no table is built yet.
    pub(crate) fn quick_view_table(&self) -> Response {
        let (viewers, kinds) = (*self.current_quick_view()).clone();
        Response::QuickViewTable { viewers, kinds }
    }

    /// The table built last, built now when there is none. Blocking then.
    pub(crate) fn current_quick_view(&self) -> Arc<QuickViewTable> {
        let mut table = self
            .quick_view
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        Arc::clone(table.get_or_insert_with(|| Arc::new(self.build_quick_view())))
    }

    /// Builds the table again; when it differs from the one before, every
    /// client gets `quick_view_table_changed`. Blocking: it reads the tools
    /// folders.
    pub(crate) fn rebuild_quick_view(&self) {
        let mut table = self
            .quick_view
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        let built = self.build_quick_view();
        if table.as_deref() == Some(&built) {
            return;
        }
        let first = table.is_none();
        *table = Some(Arc::new(built.clone()));
        drop(table);
        let (viewers, kinds) = built;
        tracing::info!(
            viewers = viewers.len(),
            kinds = kinds.len(),
            "quick view table built"
        );
        if !first {
            self.events
                .publish(Event::QuickViewTableChanged { viewers, kinds });
        }
    }

    fn build_quick_view(&self) -> QuickViewTable {
        let choices = self.settings.snapshot().config.quick_view.viewers.clone();
        self.market
            .quick_view_table(self.dev_tools.as_deref(), &choices)
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

    /// The reply to `preview_theme`: the whole theme of the themes
    /// catalogue, read without installing it. A theme read lately is
    /// answered from memory: the gallery asks again each time the selection
    /// returns to a tile. Blocking when it downloads.
    pub(crate) fn preview_theme(&self, id: &str) -> Response {
        match self.try_preview_theme(id) {
            Ok(theme) => Response::Theme {
                theme: Box::new(theme),
            },
            Err(error) => failure(error),
        }
    }

    fn try_preview_theme(&self, id: &str) -> Result<Theme, MarketError> {
        let themes = self.current(Catalogue::Themes)?;
        let item = self.market.choose(&themes, id, None)?;
        let key = format!("{}@{}@{}", item.id, item.version, item.download.sha256);
        if let Some(theme) = self.lock_previews().get(&key) {
            return Ok(theme.clone());
        }
        let allow_insecure = self.settings.snapshot().config.marketplace.allow_insecure;
        let theme = self.market.preview_theme(&themes, item, allow_insecure)?;
        let mut previews = self.lock_previews();
        if previews.len() >= PREVIEWS_KEPT {
            previews.clear();
        }
        previews.insert(key, theme.clone());
        Ok(theme)
    }

    fn lock_previews(&self) -> MutexGuard<'_, HashMap<String, Theme>> {
        self.previews.lock().unwrap_or_else(PoisonError::into_inner)
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
            ExtensionKind::Tool => {
                self.events.publish(Event::ToolsChanged {
                    tools: self.market.tools(),
                });
                self.rebuild_quick_view();
            }
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
                    self.rebuild_quick_view();
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

/// Builds the Quick View table at start, then again each time
/// `quickView.viewers` changes in the configuration (from the window, the
/// palette or the file).
pub(crate) async fn follow_quick_view(
    market: Arc<Marketplace>,
    mut changes: tokio::sync::watch::Receiver<Arc<Snapshot>>,
) {
    let mut applied = changes
        .borrow_and_update()
        .config
        .quick_view
        .viewers
        .clone();
    let building = Arc::clone(&market);
    let _ = tokio::task::spawn_blocking(move || building.rebuild_quick_view()).await;
    while changes.changed().await.is_ok() {
        let viewers = changes
            .borrow_and_update()
            .config
            .quick_view
            .viewers
            .clone();
        if viewers != applied {
            applied = viewers;
            let market = Arc::clone(&market);
            let _ = tokio::task::spawn_blocking(move || market.rebuild_quick_view()).await;
        }
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
