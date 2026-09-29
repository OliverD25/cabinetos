//! The JSON Lines format shared by every CabinetOS process.
//!
//! One event becomes one JSON object on one line, with a fixed set of keys in
//! a fixed order (documented in `docs/diagnostics.md`):
//!
//! `ts`, `level`, `boundary`, `target`, `message`, `trace_id`, `request_id`,
//! `plugin_id`, `span`, `fields`, `thread`.

use std::fmt;

use serde::Serialize;
use serde_json::{Map, Value};
use tracing::field::{Field, Visit};
use tracing::span::{Attributes, Id, Record};
use tracing::{Event, Subscriber};
use tracing_subscriber::fmt::format::Writer;
use tracing_subscriber::fmt::{FmtContext, FormatEvent, FormatFields};
use tracing_subscriber::layer::Context;
use tracing_subscriber::registry::{LookupSpan, Scope};

use crate::{Boundary, clock};

/// Span field copied into every event inside the span.
const TRACE_ID: &str = "trace_id";
/// Span field copied into every event inside the span.
const REQUEST_ID: &str = "request_id";
/// Span field copied into every event inside the span.
const PLUGIN_ID: &str = "plugin_id";

/// Formats events for the log file as one JSON object per line.
pub(crate) struct JsonFormat {
    boundary: Boundary,
}

impl JsonFormat {
    pub(crate) fn new(boundary: Boundary) -> Self {
        Self { boundary }
    }
}

impl<S, N> FormatEvent<S, N> for JsonFormat
where
    S: Subscriber + for<'a> LookupSpan<'a>,
    N: for<'a> FormatFields<'a> + 'static,
{
    fn format_event(
        &self,
        ctx: &FmtContext<'_, S, N>,
        mut writer: Writer<'_>,
        event: &Event<'_>,
    ) -> fmt::Result {
        let line = render_line(event, ctx.event_scope(), self.boundary);
        writer.write_str(&line)?;
        writer.write_char('\n')
    }
}

/// One log line. Field order here is the key order in the file.
#[derive(Serialize)]
struct Line<'a> {
    ts: String,
    level: &'static str,
    boundary: Boundary,
    target: &'a str,
    message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    trace_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    request_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    plugin_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    span: Option<&'static str>,
    #[serde(skip_serializing_if = "Map::is_empty")]
    fields: Map<String, Value>,
    thread: String,
}

/// Renders one event as a JSON line, without the trailing newline.
///
/// `scope` is the event's span scope, innermost span first. The innermost span
/// that carries `trace_id` (or `request_id`, or `plugin_id`) provides that
/// key. Outside a
/// plugin's span, an event's own `plugin_id` field provides it: the core
/// names the plugin a line is about. An event with a `plugin_id` is about a
/// plugin: its boundary is `plugin`, whatever the process's own boundary
/// is.
pub(crate) fn render_line<S>(
    event: &Event<'_>,
    scope: Option<Scope<'_, S>>,
    boundary: Boundary,
) -> String
where
    S: for<'a> LookupSpan<'a>,
{
    render_event(event, Around::of(scope), boundary)
}

/// What an event takes from the spans around it: the innermost span's name
/// and the IDs.
#[derive(Clone, Debug, Default)]
pub(crate) struct Around {
    span: Option<&'static str>,
    trace_id: Option<String>,
    request_id: Option<String>,
    plugin_id: Option<String>,
}

impl Around {
    /// Reads `scope`, innermost span first.
    pub(crate) fn of<S>(scope: Option<Scope<'_, S>>) -> Self
    where
        S: for<'a> LookupSpan<'a>,
    {
        let mut around = Self::default();
        for span_ref in scope.into_iter().flatten() {
            around.span.get_or_insert(span_ref.name());
            if let Some(ids) = span_ref.extensions().get::<SpanIds>() {
                if around.trace_id.is_none() {
                    around.trace_id.clone_from(&ids.trace_id);
                }
                if around.request_id.is_none() {
                    around.request_id.clone_from(&ids.request_id);
                }
                if around.plugin_id.is_none() {
                    around.plugin_id.clone_from(&ids.plugin_id);
                }
            }
            if around.trace_id.is_some()
                && around.request_id.is_some()
                && around.plugin_id.is_some()
            {
                break;
            }
        }
        around
    }
}

/// Renders `event` with what it takes from the spans around it.
pub(crate) fn render_event(event: &Event<'_>, around: Around, boundary: Boundary) -> String {
    let metadata = event.metadata();
    let mut visitor = EventVisitor::default();
    event.record(&mut visitor);
    let mut around = around;
    if let Some(Value::String(named)) = visitor.fields.get(PLUGIN_ID)
        && around
            .plugin_id
            .as_ref()
            .is_none_or(|from_span| from_span == named)
    {
        around.plugin_id = Some(named.clone());
        visitor.fields.remove(PLUGIN_ID);
    }
    render(
        around,
        boundary,
        (metadata.level().as_str(), metadata.target()),
        visitor.message,
        visitor.fields,
    )
}

/// Renders a line that no `tracing` event made: a note the log writer adds
/// itself, such as how long a thread waited for it.
pub(crate) fn render_note(
    around: Around,
    boundary: Boundary,
    (level, target): (&'static str, &str),
    message: &str,
    fields: Map<String, Value>,
) -> String {
    render(
        around,
        boundary,
        (level, target),
        message.to_owned(),
        fields,
    )
}

fn render(
    around: Around,
    boundary: Boundary,
    (level, target): (&'static str, &str),
    message: String,
    fields: Map<String, Value>,
) -> String {
    let line = Line {
        ts: clock::rfc3339_millis(clock::now()),
        level,
        boundary: if around.plugin_id.is_some() {
            Boundary::Plugin
        } else {
            boundary
        },
        target,
        message,
        trace_id: around.trace_id,
        request_id: around.request_id,
        plugin_id: around.plugin_id,
        span: around.span,
        fields,
        thread: thread_label(),
    };
    serde_json::to_string(&line)
        .unwrap_or_else(|error| format!(r#"{{"message":"unserializable log line: {error}"}}"#))
}

/// The thread's name, or its ID when it has none.
pub(crate) fn thread_label() -> String {
    let thread = std::thread::current();
    match thread.name() {
        Some(name) => name.to_owned(),
        None => format!("{:?}", thread.id()),
    }
}

/// Collects an event's message and its other fields.
#[derive(Default)]
struct EventVisitor {
    message: String,
    fields: Map<String, Value>,
}

impl EventVisitor {
    fn put(&mut self, field: &Field, value: Value) {
        if field.name() == "message" {
            self.message = match value {
                Value::String(text) => text,
                other => other.to_string(),
            };
        } else {
            self.fields.insert(field.name().to_owned(), value);
        }
    }
}

impl Visit for EventVisitor {
    fn record_f64(&mut self, field: &Field, value: f64) {
        let value = serde_json::Number::from_f64(value)
            .map_or_else(|| Value::String(value.to_string()), Value::Number);
        self.put(field, value);
    }

    fn record_i64(&mut self, field: &Field, value: i64) {
        self.put(field, Value::from(value));
    }

    fn record_u64(&mut self, field: &Field, value: u64) {
        self.put(field, Value::from(value));
    }

    fn record_bool(&mut self, field: &Field, value: bool) {
        self.put(field, Value::Bool(value));
    }

    fn record_str(&mut self, field: &Field, value: &str) {
        self.put(field, Value::String(value.to_owned()));
    }

    fn record_error(&mut self, field: &Field, value: &(dyn std::error::Error + 'static)) {
        self.put(field, Value::String(value.to_string()));
    }

    fn record_debug(&mut self, field: &Field, value: &dyn fmt::Debug) {
        self.put(field, Value::String(format!("{value:?}")));
    }
}

/// The IDs a span carries, stored in the span's extensions.
#[derive(Default)]
#[expect(
    clippy::struct_field_names,
    reason = "the fields are named after the log keys they fill"
)]
pub(crate) struct SpanIds {
    pub(crate) trace_id: Option<String>,
    request_id: Option<String>,
    plugin_id: Option<String>,
}

impl SpanIds {
    fn is_empty(&self) -> bool {
        self.trace_id.is_none() && self.request_id.is_none() && self.plugin_id.is_none()
    }
}

/// Whether `metadata` is a span that declares one of the ID fields. Such
/// spans are created whatever the log level, so the trace they carry
/// reaches the pipe even when nothing is logged.
pub(crate) fn is_id_span(metadata: &tracing::Metadata<'_>) -> bool {
    metadata.is_span()
        && [TRACE_ID, REQUEST_ID, PLUGIN_ID]
            .iter()
            .any(|name| metadata.fields().field(name).is_some())
}

/// Reads `trace_id`, `request_id` and `plugin_id` from span fields. Record
/// them with `%` (`request_id = %id`) or as strings, so the value is the
/// plain text.
struct SpanIdsVisitor<'a>(&'a mut SpanIds);

impl SpanIdsVisitor<'_> {
    fn put(&mut self, field: &Field, text: String) {
        match field.name() {
            TRACE_ID => self.0.trace_id = Some(text),
            REQUEST_ID => self.0.request_id = Some(text),
            PLUGIN_ID => self.0.plugin_id = Some(text),
            _ => {}
        }
    }
}

impl Visit for SpanIdsVisitor<'_> {
    fn record_str(&mut self, field: &Field, value: &str) {
        self.put(field, value.to_owned());
    }

    fn record_debug(&mut self, field: &Field, value: &dyn fmt::Debug) {
        if matches!(field.name(), TRACE_ID | REQUEST_ID | PLUGIN_ID) {
            self.put(field, format!("{value:?}"));
        }
    }
}

/// Copies `trace_id`, `request_id` and `plugin_id` from span fields into the
/// span's extensions, where [`render_line`] finds them for every event in
/// the span.
pub(crate) struct SpanIdsLayer;

impl<S> tracing_subscriber::Layer<S> for SpanIdsLayer
where
    S: Subscriber + for<'a> LookupSpan<'a>,
{
    fn on_new_span(&self, attrs: &Attributes<'_>, id: &Id, ctx: Context<'_, S>) {
        let mut ids = SpanIds::default();
        attrs.record(&mut SpanIdsVisitor(&mut ids));
        if !ids.is_empty()
            && let Some(span) = ctx.span(id)
        {
            span.extensions_mut().insert(ids);
        }
    }

    fn on_record(&self, id: &Id, values: &Record<'_>, ctx: Context<'_, S>) {
        // Format the values before taking the span's lock: a Debug impl that
        // panics must not leave the lock held while the panic hook runs.
        let mut update = SpanIds::default();
        values.record(&mut SpanIdsVisitor(&mut update));
        if update.is_empty() {
            return;
        }
        let Some(span) = ctx.span(id) else { return };
        let mut extensions = span.extensions_mut();
        match extensions.get_mut::<SpanIds>() {
            Some(ids) => {
                if update.trace_id.is_some() {
                    ids.trace_id = update.trace_id;
                }
                if update.request_id.is_some() {
                    ids.request_id = update.request_id;
                }
                if update.plugin_id.is_some() {
                    ids.plugin_id = update.plugin_id;
                }
            }
            None => extensions.insert(update),
        }
    }
}

#[cfg(test)]
mod tests {
    use std::sync::{Arc, Mutex};

    use serde_json::json;
    use tracing_subscriber::layer::SubscriberExt;

    use super::*;

    /// Renders every event into a shared list, like the ring buffer does.
    struct Capture {
        lines: Arc<Mutex<Vec<String>>>,
    }

    impl<S> tracing_subscriber::Layer<S> for Capture
    where
        S: Subscriber + for<'a> LookupSpan<'a>,
    {
        fn on_event(&self, event: &Event<'_>, ctx: Context<'_, S>) {
            let line = render_line(event, ctx.event_scope(event), Boundary::Engine);
            assert!(!line.contains('\n'), "a log line must be one line: {line}");
            self.lines.lock().unwrap().push(line);
        }
    }

    /// The raw JSON lines of the events emitted by `body`.
    fn capture_raw(body: impl FnOnce()) -> Vec<String> {
        let lines = Arc::new(Mutex::new(Vec::new()));
        let subscriber = tracing_subscriber::registry()
            .with(SpanIdsLayer)
            .with(Capture {
                lines: Arc::clone(&lines),
            });
        tracing::subscriber::with_default(subscriber, body);
        // A copy, not `Arc::try_unwrap`: while another test thread registers
        // a callsite, `tracing` briefly holds a strong reference to every
        // live subscriber, this one included.
        lines.lock().unwrap().clone()
    }

    /// The events emitted by `body`, parsed.
    fn capture(body: impl FnOnce()) -> Vec<Value> {
        capture_raw(body)
            .iter()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect()
    }

    #[test]
    fn writes_the_fixed_schema() {
        let lines = capture(|| {
            let span = tracing::info_span!("request", request_id = "01J9ZQ4X7K3M5N8P2R6S0T1V4W");
            let _entered = span.enter();
            tracing::warn!(count = 3, ok = true, name = "x", "multi\nline message");
        });
        let line = &lines[0];
        let keys: Vec<&str> = line
            .as_object()
            .unwrap()
            .keys()
            .map(String::as_str)
            .collect();
        // serde_json sorts keys when parsing, so compare the set, not the order.
        for key in [
            "ts",
            "level",
            "boundary",
            "target",
            "message",
            "request_id",
            "span",
            "fields",
            "thread",
        ] {
            assert!(keys.contains(&key), "missing {key} in {line}");
        }
        assert_eq!(line["level"], "WARN");
        assert_eq!(line["boundary"], "engine");
        assert_eq!(line["message"], "multi\nline message");
        assert_eq!(line["request_id"], "01J9ZQ4X7K3M5N8P2R6S0T1V4W");
        assert_eq!(line["span"], "request");
        assert_eq!(line["fields"], json!({"count": 3, "ok": true, "name": "x"}));
        assert!(line.get("plugin_id").is_none());
    }

    #[test]
    fn keys_come_in_the_documented_order() {
        let lines = capture_raw(|| {
            let span = tracing::info_span!(
                "plugin",
                plugin_id = "md-preview",
                request_id = "R1",
                trace_id = "T1"
            );
            let _entered = span.enter();
            tracing::info!(extra = 1, "hello");
        });
        let line = &lines[0];
        let order = [
            "\"ts\"",
            "\"level\"",
            "\"boundary\"",
            "\"target\"",
            "\"message\"",
            "\"trace_id\"",
            "\"request_id\"",
            "\"plugin_id\"",
            "\"span\"",
            "\"fields\"",
            "\"thread\"",
        ];
        let positions: Vec<usize> = order.iter().map(|key| line.find(key).unwrap()).collect();
        assert!(positions.is_sorted(), "keys out of order: {line}");
    }

    #[test]
    fn events_inside_a_plugin_span_have_the_plugin_boundary() {
        let lines = capture(|| {
            tracing::info!("before");
            let span = tracing::info_span!("plugin", plugin_id = "hello");
            let _entered = span.enter();
            tracing::info!("inside");
        });
        assert_eq!(lines[0]["boundary"], "engine");
        assert_eq!(lines[1]["boundary"], "plugin");
        assert_eq!(lines[1]["plugin_id"], "hello");
    }

    #[test]
    fn a_plugin_id_field_names_the_plugin_outside_its_span() {
        let lines = capture(|| {
            tracing::info!(plugin_id = "crashy", "starting the plugin again");
            let span = tracing::info_span!("plugin", plugin_id = "hello");
            let _entered = span.enter();
            tracing::info!(plugin_id = "hello", "the same plugin twice");
            tracing::info!(plugin_id = "other", "another plugin inside");
        });
        assert_eq!(lines[0]["plugin_id"], "crashy");
        assert_eq!(lines[0]["boundary"], "plugin");
        assert!(lines[0].get("fields").is_none(), "{}", lines[0]);
        assert_eq!(lines[1]["plugin_id"], "hello");
        assert!(lines[1].get("fields").is_none(), "{}", lines[1]);
        assert_eq!(lines[2]["plugin_id"], "hello", "the span wins");
        assert_eq!(lines[2]["fields"]["plugin_id"], "other");
    }

    #[test]
    fn omits_ids_and_fields_outside_spans() {
        let lines = capture(|| tracing::info!("plain"));
        let line = &lines[0];
        assert_eq!(line["message"], "plain");
        for key in ["request_id", "plugin_id", "span", "fields"] {
            assert!(line.get(key).is_none(), "{key} should be omitted: {line}");
        }
    }

    #[test]
    fn innermost_span_with_the_field_wins() {
        let lines = capture(|| {
            let outer = tracing::info_span!("plugin", plugin_id = "tags", request_id = "OUTER");
            let _outer = outer.enter();
            let inner = tracing::info_span!("request", request_id = "INNER");
            let _inner = inner.enter();
            let plain = tracing::info_span!("step");
            let _plain = plain.enter();
            tracing::info!("nested");
        });
        let line = &lines[0];
        assert_eq!(line["request_id"], "INNER");
        assert_eq!(line["plugin_id"], "tags");
        assert_eq!(line["span"], "step");
    }

    #[test]
    fn picks_up_ids_recorded_after_the_span_starts() {
        let lines = capture(|| {
            let span = tracing::info_span!("request", request_id = tracing::field::Empty);
            span.record("request_id", "LATE");
            let _entered = span.enter();
            tracing::info!("after record");
        });
        assert_eq!(lines[0]["request_id"], "LATE");
    }

    #[test]
    fn a_request_span_carries_its_trace_and_what_it_starts_inherits_it() {
        let id: cabinetos_protocol::RequestId = "01J9ZQ4X7K3M5N8P2R6S0T1V4W".parse().unwrap();
        let trace: cabinetos_protocol::RequestId = "01J9ZQ4X7K3M5N8P2R6S0T1V4V".parse().unwrap();
        let lines = capture(|| {
            tracing::info!("before");
            let span = crate::span_for_action(&id, &trace);
            let _entered = span.enter();
            tracing::info!("in the request");
            let job = tracing::info_span!("job", job_id = 3);
            let _job = job.enter();
            tracing::info!("in the job");
        });
        assert!(lines[0].get("trace_id").is_none());
        assert_eq!(lines[1]["trace_id"], trace.as_str());
        assert_eq!(lines[1]["request_id"], id.as_str());
        assert_eq!(lines[2]["trace_id"], trace.as_str());
        assert_eq!(lines[2]["span"], "job");

        let own: cabinetos_protocol::RequestId = "01J9ZQ4X7K3M5N8P2R6S0T1V4T".parse().unwrap();
        let lines = capture(|| {
            let span = crate::span_for_request(&own);
            let _entered = span.enter();
            tracing::info!("its own action");
        });
        assert_eq!(lines[0]["trace_id"], own.as_str());
        assert_eq!(lines[0]["request_id"], own.as_str());
    }

    #[test]
    fn current_trace_is_the_innermost_trace_around_the_caller() {
        let id: cabinetos_protocol::RequestId = "01J9ZQ4X7K3M5N8P2R6S0T1V4W".parse().unwrap();
        let trace: cabinetos_protocol::RequestId = "01J9ZQ4X7K3M5N8P2R6S0T1V4V".parse().unwrap();
        let subscriber = tracing_subscriber::registry().with(SpanIdsLayer);
        tracing::subscriber::with_default(subscriber, || {
            assert_eq!(crate::current_trace(), None);
            let request = crate::span_for_action(&id, &trace);
            let job = tracing::info_span!(parent: &request, "job", job_id = 1);
            // Entered on another thread, as a job's thread enters its cause.
            let dispatch = tracing::dispatcher::get_default(Clone::clone);
            let seen = std::thread::scope(|scope| {
                scope
                    .spawn(|| {
                        tracing::dispatcher::with_default(&dispatch, || {
                            let _entered = job.enter();
                            crate::current_trace()
                        })
                    })
                    .join()
                    .unwrap()
            });
            assert_eq!(seen, Some(trace.clone()));
            let _entered = request.enter();
            assert_eq!(crate::current_trace(), Some(trace.clone()));
            let unrelated = tracing::info_span!(parent: None, "listing", listing_id = 4);
            let _unrelated = unrelated.enter();
            assert_eq!(crate::current_trace(), None, "a watcher is nobody's action");
        });
    }

    #[test]
    fn only_spans_that_declare_an_id_are_id_spans() {
        let subscriber = tracing_subscriber::registry().with(Probe);
        tracing::subscriber::with_default(subscriber, || {
            let _ = tracing::info_span!("request", request_id = "R");
            let _ = tracing::trace_span!("plugin", plugin_id = "p");
            let _ = tracing::info_span!("anything", trace_id = tracing::field::Empty);
            let _ = tracing::info_span!("listing", listing_id = 1);
            tracing::info!(request_id = "R", "an event is never an id span");
        });
        assert_eq!(
            PROBED.lock().unwrap().clone(),
            vec![
                ("request", true),
                ("plugin", true),
                ("anything", true),
                ("listing", false),
                ("event", false)
            ]
        );
    }

    static PROBED: Mutex<Vec<(&'static str, bool)>> = Mutex::new(Vec::new());

    /// Records what [`is_id_span`] says about each span and event.
    struct Probe;

    impl<S: Subscriber> tracing_subscriber::Layer<S> for Probe {
        fn on_new_span(&self, attrs: &Attributes<'_>, _: &Id, _: Context<'_, S>) {
            let metadata = attrs.metadata();
            PROBED
                .lock()
                .unwrap()
                .push((metadata.name(), is_id_span(metadata)));
        }

        fn on_event(&self, event: &Event<'_>, _: Context<'_, S>) {
            PROBED
                .lock()
                .unwrap()
                .push(("event", is_id_span(event.metadata())));
        }
    }

    #[test]
    fn display_values_are_written_without_quotes() {
        let lines = capture(|| {
            let id: cabinetos_protocol::RequestId = "01J9ZQ4X7K3M5N8P2R6S0T1V4W".parse().unwrap();
            let span = crate::span_for_request(&id);
            let _entered = span.enter();
            tracing::info!("inside");
        });
        assert_eq!(lines[0]["request_id"], "01J9ZQ4X7K3M5N8P2R6S0T1V4W");
    }
}
