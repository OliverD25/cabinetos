//! The events every connection that said `hello` receives, whoever caused
//! them: configuration changes and job progress.

use std::sync::Arc;

use cabinetos_jobs::JobQueueManager;
use cabinetos_protocol::{Envelope, Event, RequestId};
use tokio::sync::broadcast;

use crate::settings::Settings;

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
}
