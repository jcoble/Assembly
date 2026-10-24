//! A local OpenTelemetry (OTLP over HTTP, JSON encoding) receiver. Agents
//! export their usage, cost, latency and tool events here, and `otel_store`
//! keeps them in the usage database for reports.
//!
//! Memory stays bounded: each request is flattened into records that go on a
//! queue of `QUEUE_CAPACITY`; when the queue is full a record is dropped and
//! counted, never buffered. One writer thread drains the queue in batches.

use axum::body::Bytes;
use axum::extract::State;
use axum::http::{header, HeaderMap, StatusCode};
use axum::routing::post;
use axum::Router;
use super::otel_store::{Lookups, OtelStore, RETENTION_DAYS};
use crate::agent_conversation::manager::AgentRuntimeManager;
use crate::agent_conversation::providers::process::SidecarEnvironment;
use serde_json::{Map, Value};
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{sync_channel, Receiver, SyncSender};
use std::sync::{Arc, OnceLock};

/// Records waiting for the writer. A record is a few hundred bytes to a few
/// kilobytes, so a full queue holds a few megabytes at most.
pub const QUEUE_CAPACITY: usize = 4096;
/// Records written per transaction.
const BATCH: usize = 512;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Signal {
    Span,
    Log,
    Metric,
}

/// One span, log record or metric data point, flattened from its OTLP request.
#[derive(Debug, Clone, PartialEq)]
pub struct Record {
    pub signal: Signal,
    /// Span name, log event name, or metric name.
    pub name: String,
    pub time_ms: i64,
    /// Spans only.
    pub end_ms: Option<i64>,
    pub trace_id: Option<String>,
    pub span_id: Option<String>,
    pub parent_span_id: Option<String>,
    /// Metrics only.
    pub unit: Option<String>,
    pub value: Option<f64>,
    /// The resource's `service.name`, such as `claude-code`.
    pub service: Option<String>,
    /// The agent's own session id (`session.id` or `conversation.id`). For an
    /// ACP adapter this is the ACP `sessionId`.
    pub session_id: Option<String>,
    /// `assembly.owned_id` when the exporter was tagged with one.
    pub owned_id: Option<String>,
    pub attributes: Map<String, Value>,
}

/// The receiving end of the queue, shared by the three routes.
pub struct Inbox {
    pub queue: SyncSender<Record>,
    pub dropped: Arc<AtomicU64>,
}

impl Inbox {
    fn push(&self, records: Vec<Record>) {
        for record in records {
            if self.queue.try_send(record).is_err() {
                self.dropped.fetch_add(1, Ordering::Relaxed);
            }
        }
    }
}

/// The receiver's address while it runs; agent launches export to it.
static ENDPOINT: OnceLock<String> = OnceLock::new();

/// Starts the receiver in the usage database at `path`. Rows are tied to
/// sessions, and to the workflow step they ran, through the agent manager.
pub fn start(path: &Path, runtime: AgentRuntimeManager) {
    let steps = runtime.clone();
    let lookups = Lookups {
        owned_for_session: Box::new(move |native_session_id| runtime.owned_id_for_native_session(native_session_id)),
        step_for_owned: Box::new(move |owned_id| steps.run_and_step_for_session(owned_id)),
    };
    match serve(path, lookups) {
        Ok(endpoint) => {
            let _ = ENDPOINT.set(endpoint);
        }
        Err(error) => crate::debug_log::stderr_log!("Telemetry receiver is off: {error}"),
    }
}

/// A Claude Code launch, pointed at the receiver when it runs. Claude Code
/// exports through `claude-agent-acp` from these variables alone
/// (spike-otel: same records directly and through the adapter).
pub fn claude_telemetry(environment: SidecarEnvironment) -> SidecarEnvironment {
    match ENDPOINT.get() {
        Some(endpoint) => with_telemetry(environment, endpoint),
        None => environment,
    }
}

fn with_telemetry(environment: SidecarEnvironment, endpoint: &str) -> SidecarEnvironment {
    environment.set("CLAUDE_CODE_ENABLE_TELEMETRY", "1")
        .set("OTEL_LOGS_EXPORTER", "otlp")
        .set("OTEL_METRICS_EXPORTER", "otlp")
        .set("OTEL_EXPORTER_OTLP_PROTOCOL", "http/json")
        .set("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint)
        .set("OTEL_LOGS_EXPORT_INTERVAL", "2000")
        .set("OTEL_METRIC_EXPORT_INTERVAL", "10000")
}

/// Deletes rows past retention, then starts the writer thread and the
/// receiver on a free loopback port. Returns the receiver's address.
pub fn serve(path: &Path, lookups: Lookups) -> Result<String, String> {
    let store = OtelStore::open(path)?;
    let now_ms = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |elapsed| elapsed.as_millis() as i64);
    store.delete_older_than(now_ms - RETENTION_DAYS * 86_400_000)?;
    let listener = std::net::TcpListener::bind(("127.0.0.1", 0)).map_err(|error| error.to_string())?;
    listener.set_nonblocking(true).map_err(|error| error.to_string())?;
    let endpoint = format!("http://{}", listener.local_addr().map_err(|error| error.to_string())?);
    let (queue, records) = sync_channel(QUEUE_CAPACITY);
    let dropped = Arc::new(AtomicU64::new(0));
    let inbox = Arc::new(Inbox { queue, dropped: Arc::clone(&dropped) });
    std::thread::Builder::new().name("otel-writer".into())
        .spawn(move || write(records, store, lookups, dropped)).map_err(|error| error.to_string())?;
    tauri::async_runtime::spawn(async move {
        if let Ok(listener) = tokio::net::TcpListener::from_std(listener) {
            let _ = axum::serve(listener, router(inbox)).await;
        }
    });
    Ok(endpoint)
}

/// Drains the queue until every sender is gone, one transaction per batch.
pub(crate) fn write(records: Receiver<Record>, mut store: OtelStore, lookups: Lookups, dropped: Arc<AtomicU64>) {
    let mut reported = 0;
    while let Ok(first) = records.recv() {
        let mut batch = vec![first];
        batch.extend(records.try_iter().take(BATCH - 1));
        if let Err(error) = store.insert(&batch, &lookups) {
            crate::debug_log::stderr_log!("{error}");
        }
        let total = dropped.load(Ordering::Relaxed);
        if total != reported {
            crate::debug_log::stderr_log!("Telemetry queue was full: {} records dropped", total - reported);
            reported = total;
        }
    }
}

pub fn router(inbox: Arc<Inbox>) -> Router {
    Router::new()
        .route("/v1/traces", post(|state, headers, body| receive(Signal::Span, state, headers, body)))
        .route("/v1/logs", post(|state, headers, body| receive(Signal::Log, state, headers, body)))
        .route("/v1/metrics", post(|state, headers, body| receive(Signal::Metric, state, headers, body)))
        .with_state(inbox)
}

async fn receive(signal: Signal, State(inbox): State<Arc<Inbox>>, headers: HeaderMap, body: Bytes) -> (StatusCode, [(header::HeaderName, &'static str); 1], &'static str) {
    let json = [(header::CONTENT_TYPE, "application/json")];
    let is_json = headers.get(header::CONTENT_TYPE).and_then(|value| value.to_str().ok()).is_some_and(|value| value.contains("json"));
    if !is_json {
        // Only OTLP's JSON encoding is read; exporters are launched with http/json.
        return (StatusCode::UNSUPPORTED_MEDIA_TYPE, json, "{}");
    }
    match serde_json::from_slice::<Value>(&body) {
        Ok(request) => {
            inbox.push(parse(signal, &request));
            (StatusCode::OK, json, "{}")
        }
        Err(_) => (StatusCode::BAD_REQUEST, json, "{}"),
    }
}

/// Flattens one OTLP export request into records.
pub fn parse(signal: Signal, request: &Value) -> Vec<Record> {
    let (resources_key, scopes_key, items_key) = match signal {
        Signal::Span => ("resourceSpans", "scopeSpans", "spans"),
        Signal::Log => ("resourceLogs", "scopeLogs", "logRecords"),
        Signal::Metric => ("resourceMetrics", "scopeMetrics", "metrics"),
    };
    let mut records = Vec::new();
    for resource in list(request, resources_key) {
        let resource_attributes = attributes(resource.pointer("/resource/attributes"));
        let service = text(&resource_attributes, "service.name");
        let resource_owner = text(&resource_attributes, "assembly.owned_id");
        for scope in list(resource, scopes_key) {
            for item in list(scope, items_key) {
                let base = |name: String, time_ms: i64, mut attributes: Map<String, Value>| {
                    let owned_id = attributes.remove("assembly.owned_id").and_then(|value| value.as_str().map(str::to_string))
                        .or_else(|| resource_owner.clone());
                    let session_id = ["session.id", "conversation.id", "gen_ai.conversation.id"].iter().find_map(|key| text(&attributes, key));
                    Record {
                        signal, name, time_ms, end_ms: None, trace_id: None, span_id: None, parent_span_id: None,
                        unit: None, value: None, service: service.clone(), session_id, owned_id, attributes,
                    }
                };
                match signal {
                    Signal::Span => {
                        let mut attrs = attributes(item.get("attributes"));
                        if let Some(code) = item.pointer("/status/code") {
                            attrs.insert("otel.status_code".into(), status_name(code).into());
                        }
                        let mut record = base(string(item, "name").unwrap_or_default(), nanos_ms(item.get("startTimeUnixNano")), attrs);
                        record.end_ms = Some(nanos_ms(item.get("endTimeUnixNano")));
                        record.trace_id = string(item, "traceId");
                        record.span_id = string(item, "spanId");
                        record.parent_span_id = string(item, "parentSpanId").filter(|id| !id.is_empty());
                        records.push(record);
                    }
                    Signal::Log => {
                        let attrs = attributes(item.get("attributes"));
                        let time = item.get("timeUnixNano").filter(|time| nanos_ms(Some(time)) > 0).or_else(|| item.get("observedTimeUnixNano"));
                        let mut record = base(log_name(item, &attrs), nanos_ms(time), attrs);
                        record.trace_id = string(item, "traceId").filter(|id| !id.is_empty());
                        record.span_id = string(item, "spanId").filter(|id| !id.is_empty());
                        records.push(record);
                    }
                    Signal::Metric => {
                        let name = string(item, "name").unwrap_or_default();
                        let unit = string(item, "unit").filter(|unit| !unit.is_empty());
                        let points = ["sum", "gauge", "histogram"].iter().find_map(|kind| item.pointer(&format!("/{kind}/dataPoints")));
                        for point in points.and_then(Value::as_array).into_iter().flatten() {
                            let mut attrs = attributes(point.get("attributes"));
                            let value = number(point.get("asDouble")).or_else(|| number(point.get("asInt"))).or_else(|| number(point.get("sum")));
                            if let Some(count) = number(point.get("count")) {
                                attrs.insert("count".into(), count.into());
                            }
                            let mut record = base(name.clone(), nanos_ms(point.get("timeUnixNano")), attrs);
                            record.unit = unit.clone();
                            record.value = value;
                            records.push(record);
                        }
                    }
                }
            }
        }
    }
    records
}

fn list<'a>(value: &'a Value, key: &str) -> impl Iterator<Item = &'a Value> {
    value.get(key).and_then(Value::as_array).into_iter().flatten()
}

fn string(value: &Value, key: &str) -> Option<String> {
    value.get(key).and_then(Value::as_str).map(str::to_string)
}

fn text(attributes: &Map<String, Value>, key: &str) -> Option<String> {
    attributes.get(key).and_then(Value::as_str).map(str::to_string)
}

/// OTLP JSON writes 64-bit integers as strings.
fn number(value: Option<&Value>) -> Option<f64> {
    let value = value?;
    value.as_f64().or_else(|| value.as_str()?.parse().ok())
}

fn nanos_ms(value: Option<&Value>) -> i64 {
    value.and_then(|value| value.as_u64().or_else(|| value.as_str()?.parse().ok())).map_or(0, |nanos| (nanos / 1_000_000) as i64)
}

fn status_name(code: &Value) -> &'static str {
    match code.as_u64().or_else(|| code.as_str().map(|name| if name.ends_with("ERROR") { 2 } else if name.ends_with("OK") { 1 } else { 0 })) {
        Some(2) => "error",
        Some(1) => "ok",
        _ => "unset",
    }
}

/// A log's name: its body when that is an event name (Claude Code sends
/// `claude_code.api_request`), else its `event.name`. A body that reads like
/// text is never kept.
fn log_name(item: &Value, attributes: &Map<String, Value>) -> String {
    item.pointer("/body/stringValue").and_then(Value::as_str)
        .filter(|body| body.len() <= 128 && !body.contains(char::is_whitespace))
        .map(str::to_string)
        .or_else(|| text(attributes, "event.name"))
        .unwrap_or_else(|| "log".into())
}

/// OTLP `KeyValue` list to a JSON object, without the redacted keys.
fn attributes(list: Option<&Value>) -> Map<String, Value> {
    list.and_then(Value::as_array).into_iter().flatten()
        .filter_map(|entry| Some((entry.get("key")?.as_str()?.to_string(), any_value(entry.get("value")?))))
        .filter(|(key, _)| !redacted(key))
        .collect()
}

/// Never kept: who the user is (email, user, account and organization ids)
/// and any field that can carry prompt, response or tool content. Claude Code
/// puts all of the identity fields on every record.
fn redacted(key: &str) -> bool {
    let key = key.to_ascii_lowercase();
    let last = key.rsplit('.').next().unwrap_or_default();
    key == "user.id" || ["email", "account", "organization"].iter().any(|word| key.contains(word))
        || matches!(last, "prompt" | "prompt_text" | "response" | "content" | "message" | "messages" | "choice" | "body" | "text"
            | "arguments" | "input" | "output" | "result" | "error" | "tool_input" | "tool_output" | "tool_parameters")
}

fn any_value(value: &Value) -> Value {
    if let Some(text) = value.get("stringValue") {
        return text.clone();
    }
    if let Some(int) = value.get("intValue") {
        return int.as_i64().or_else(|| int.as_str()?.parse().ok()).map_or(Value::Null, Value::from);
    }
    if let Some(other) = value.get("doubleValue").or_else(|| value.get("boolValue")) {
        return other.clone();
    }
    if let Some(values) = value.pointer("/arrayValue/values").and_then(Value::as_array) {
        return values.iter().map(any_value).collect();
    }
    if let Some(entries) = value.pointer("/kvlistValue/values") {
        return Value::Object(attributes(Some(entries)));
    }
    Value::Null
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::future::IntoFuture;

    fn kv(key: &str, value: Value) -> Value {
        json!({"key": key, "value": value})
    }

    pub(crate) fn claude_logs() -> Value {
        json!({"resourceLogs": [{
            "resource": {"attributes": [kv("service.name", json!({"stringValue": "claude-code"})), kv("assembly.owned_id", json!({"stringValue": "owned-1"}))]},
            "scopeLogs": [{"logRecords": [{
                "timeUnixNano": "1792616407067000000",
                "body": {"stringValue": "claude_code.api_request"},
                "attributes": [
                    kv("session.id", json!({"stringValue": "native-1"})),
                    kv("event.name", json!({"stringValue": "api_request"})),
                    kv("model", json!({"stringValue": "claude-opus-5-5"})),
                    kv("input_tokens", json!({"intValue": "2"})),
                    kv("output_tokens", json!({"intValue": 4})),
                    kv("cost_usd", json!({"doubleValue": 0.2085752})),
                ]
            }]}]
        }]})
    }

    async fn serve(capacity: usize) -> (String, Receiver<Record>, Arc<AtomicU64>) {
        let (queue, records) = sync_channel(capacity);
        let dropped = Arc::new(AtomicU64::new(0));
        let inbox = Arc::new(Inbox { queue, dropped: Arc::clone(&dropped) });
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://{}", listener.local_addr().unwrap());
        tokio::spawn(axum::serve(listener, router(inbox)).into_future());
        (url, records, dropped)
    }

    async fn post(url: &str, path: &str, content_type: &str, body: &Value) -> u16 {
        reqwest::Client::new().post(format!("{url}{path}")).header("content-type", content_type)
            .body(body.to_string()).send().await.unwrap().status().as_u16()
    }

    #[tokio::test]
    async fn a_posted_log_request_is_queued_as_records() {
        let (url, records, dropped) = serve(16).await;
        assert_eq!(post(&url, "/v1/logs", "application/json", &claude_logs()).await, 200);
        let record = records.try_recv().unwrap();
        assert_eq!(record.signal, Signal::Log);
        assert_eq!(record.name, "claude_code.api_request");
        assert_eq!(record.time_ms, 1_792_616_407_067);
        assert_eq!(record.service.as_deref(), Some("claude-code"));
        assert_eq!(record.session_id.as_deref(), Some("native-1"));
        assert_eq!(record.owned_id.as_deref(), Some("owned-1"));
        assert_eq!(record.attributes["input_tokens"], json!(2));
        assert_eq!(record.attributes["cost_usd"], json!(0.2085752));
        assert!(records.try_recv().is_err());
        assert_eq!(dropped.load(Ordering::Relaxed), 0);
    }

    #[tokio::test]
    async fn a_full_queue_drops_and_counts_instead_of_growing() {
        let (url, records, dropped) = serve(1).await;
        for _ in 0..3 {
            assert_eq!(post(&url, "/v1/logs", "application/json", &claude_logs()).await, 200);
        }
        assert!(records.try_recv().is_ok());
        assert!(records.try_recv().is_err());
        assert_eq!(dropped.load(Ordering::Relaxed), 2);
    }

    #[tokio::test]
    async fn protobuf_requests_are_refused() {
        let (url, records, _) = serve(16).await;
        assert_eq!(post(&url, "/v1/logs", "application/x-protobuf", &claude_logs()).await, 415);
        assert!(records.try_recv().is_err());
    }

    #[tokio::test(flavor = "current_thread")]
    async fn a_claude_launch_exports_logs_and_metrics_to_the_receiver() {
        use crate::agent_conversation::protocol::{AgentProviderManifest, ProviderSource, ProviderTransport};
        use crate::agent_conversation::providers::process::{SidecarEnvironment, SidecarProcess};
        let names = ["CLAUDE_CODE_ENABLE_TELEMETRY", "OTEL_LOGS_EXPORTER", "OTEL_METRICS_EXPORTER", "OTEL_EXPORTER_OTLP_PROTOCOL",
            "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_LOGS_EXPORT_INTERVAL", "OTEL_METRIC_EXPORT_INTERVAL"];
        let script = format!("printf '[%s]\\n' \"{}\"", names.map(|name| format!("\\\"${name}\\\"")).join(","));
        let manifest = AgentProviderManifest {
            id: "claude-agent-acp".into(), display_name: "Environment fixture".into(), transport: ProviderTransport::AcpStdio,
            executable: "/bin/sh".into(), args: vec!["-c".into(), script], version: "1".into(),
            content_hash: "0".repeat(64), trusted_source: ProviderSource::Bundled,
        };
        let environment = with_telemetry(SidecarEnvironment::default(), "http://127.0.0.1:4318");
        let process = SidecarProcess::spawn_with_environment(&manifest, &std::env::temp_dir(), "owned-otel", &environment).unwrap();
        let (mut reader, _writer, mut handle) = process.split();
        let seen = reader.next_json().await.unwrap();
        handle.stop().await;
        assert_eq!(seen, json!(["1", "otlp", "otlp", "http/json", "http://127.0.0.1:4318", "2000", "10000"]));
    }

    #[test]
    fn spans_and_metric_points_are_flattened() {
        let spans = json!({"resourceSpans": [{"resource": {"attributes": [kv("service.name", json!({"stringValue": "assembly-models"}))]},
            "scopeSpans": [{"spans": [{
                "traceId": "t1", "spanId": "s2", "parentSpanId": "s1", "name": "execute_tool search",
                "startTimeUnixNano": "1000000000", "endTimeUnixNano": "1250000000", "status": {"code": 2},
                "attributes": [kv("gen_ai.tool.name", json!({"stringValue": "search"})), kv("assembly.owned_id", json!({"stringValue": "owned-2"}))]
            }]}]}]});
        let [span] = parse(Signal::Span, &spans).try_into().unwrap();
        assert_eq!((span.time_ms, span.end_ms), (1000, Some(1250)));
        assert_eq!((span.trace_id.as_deref(), span.parent_span_id.as_deref()), (Some("t1"), Some("s1")));
        assert_eq!(span.owned_id.as_deref(), Some("owned-2"));
        assert_eq!(span.attributes["otel.status_code"], json!("error"));
        assert!(!span.attributes.contains_key("assembly.owned_id"));

        let metrics = json!({"resourceMetrics": [{"scopeMetrics": [{"metrics": [
            {"name": "claude_code.token.usage", "unit": "tokens", "sum": {"dataPoints": [
                {"timeUnixNano": "2000000000", "asDouble": 25790, "attributes": [kv("type", json!({"stringValue": "cacheCreation"}))]},
                {"timeUnixNano": "2000000000", "asInt": "4", "attributes": [kv("type", json!({"stringValue": "output"}))]}]}},
            {"name": "gen_ai.client.operation.duration", "unit": "s", "histogram": {"dataPoints": [
                {"timeUnixNano": "3000000000", "sum": 1.5, "count": "2"}]}}
        ]}]}]});
        let points = parse(Signal::Metric, &metrics);
        let values: Vec<_> = points.iter().map(|point| (point.name.as_str(), point.value, point.time_ms)).collect();
        assert_eq!(values, [("claude_code.token.usage", Some(25790.0), 2000), ("claude_code.token.usage", Some(4.0), 2000),
            ("gen_ai.client.operation.duration", Some(1.5), 3000)]);
        assert_eq!(points[2].attributes["count"], json!(2.0));
        assert_eq!(points[0].unit.as_deref(), Some("tokens"));
    }
}
