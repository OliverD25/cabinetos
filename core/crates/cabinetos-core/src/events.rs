//! The events every connection that said `hello` receives, whoever caused
//! them: configuration and theme changes, job progress, plugin news, shells
//! that exited and drive letters that changed.

use std::sync::Arc;

use cabinetos_fs::Hydrator;
use cabinetos_jobs::JobQueueManager;
use cabinetos_plugins::PluginHost;
use cabinetos_protocol::{Envelope, Event, RequestId};
use cabinetos_terminal::Terminals;
use tokio::sync::broadcast;

use crate::market::Marketplace;
use crate::search::IndexerLink;
use crate::settings::Settings;
use crate::themes::Themes;

/// Events a connection may fall behind by before it misses some: about ten
/// seconds of progress from four jobs at once.
const EVENT_BUFFER: usize = 1024;

/// One stream of events for all connections.
pub(crate) struct EventHub {
    sender: broadcast::Sender<Envelope<Event>>,
}

impl EventHub {
    pub(crate) fn new() -> Arc<Self> {
        let (sender, _) = broadcast::channel(EVENT_BUFFER);
        Arc::new(Self { sender })
    }

    /// Sends `event` to every connection that listens. Never blocks.
    pub(crate) fn publish(&self, event: Event) {
        // An error means no connection is listening, which is fine.
        let _ = self.sender.send(Envelope::new(RequestId::new(), event));
    }

    /// The events from now on, for a connection that said `hello`.
    pub(crate) fn subscribe(&self) -> broadcast::Receiver<Envelope<Event>> {
        self.sender.subscribe()
    }
}

/// What every connection works with.
pub(crate) struct Services {
    pub(crate) settings: Arc<Settings>,
    pub(crate) jobs: JobQueueManager,
    pub(crate) events: Arc<EventHub>,
    /// `None` when the plugin host could not start: no plugins then.
    pub(crate) plugins: Option<Arc<PluginHost>>,
    /// The way to the indexer, for search.
    pub(crate) indexer: IndexerLink,
    /// The terminal sessions. They belong to the core, not to a
    /// connection: a client may leave and a new one attach.
    pub(crate) terminals: Arc<Terminals>,
    /// Type names and icons, with their caches, for every connection.
    pub(crate) hydrator: Arc<Hydrator>,
    /// The themes folder and the theme in effect.
    pub(crate) themes: Arc<Themes>,
    /// The marketplace: the index, installs and uninstalls.
    pub(crate) market: Arc<Marketplace>,
}
