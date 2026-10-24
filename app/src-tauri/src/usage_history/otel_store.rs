//! Stores what the OpenTelemetry receiver hands over: one table each for
//! spans, log events and metric points, in the usage database. Each row is
//! attributed to the Assembly session and, for a workflow step, to the run and
//! the step attempt (`node_run_id`) when it is written.

use super::otel::{Record, Signal};
use rusqlite::{params, Connection, TransactionBehavior};
use serde_json::{Map, Value};
use std::collections::HashMap;
use std::path::Path;
use std::time::Duration;

pub const OTEL_SCHEMA: &str = include_str!("../../../migrations/0002_otel.sql");
pub const RETENTION_DAYS: i64 = 90;

/// How rows are tied to Assembly's own records.
pub struct Lookups {
    /// The Assembly session (`owned_id`) a provider session id belongs to.
    pub owned_for_session: Box<dyn Fn(&str) -> Option<String> + Send>,
    /// The workflow run and step attempt an Assembly session belongs to.
    pub step_for_owned: Box<dyn Fn(&str) -> Option<(String, String)> + Send>,
}

#[derive(Clone, Default)]
struct Attribution {
    owned_id: Option<String>,
    run_id: Option<String>,
    node_run_id: Option<String>,
}

pub struct OtelStore {
    connection: Connection,
}

impl OtelStore {
    pub fn open(path: &Path) -> Result<Self, String> {
        let connection = Connection::open(path).map_err(|error| format!("Could not open the telemetry store: {error}"))?;
        // The usage indexer writes to the same file.
        connection.busy_timeout(Duration::from_secs(5)).map_err(|error| error.to_string())?;
        connection.execute_batch(OTEL_SCHEMA).map_err(|error| format!("Could not create the telemetry tables: {error}"))?;
        Ok(Self { connection })
    }

    /// Deletes every row from before `cutoff_ms`.
    pub fn delete_older_than(&self, cutoff_ms: i64) -> Result<usize, String> {
        ["otel_spans", "otel_log_events", "otel_metric_points"].iter().try_fold(0, |deleted, table| {
            self.connection.execute(&format!("DELETE FROM {table} WHERE time_ms < ?1"), [cutoff_ms])
                .map(|count| deleted + count).map_err(|error| format!("Could not delete old telemetry: {error}"))
        })
    }

    /// Writes a batch in one transaction. Each distinct session is looked up
    /// once per batch.
    pub fn insert(&mut self, records: &[Record], lookups: &Lookups) -> Result<(), String> {
        let mut attributions: HashMap<(Option<String>, Option<String>), Attribution> = HashMap::new();
        let transaction = self.connection.transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|error| format!("Could not start a telemetry write: {error}"))?;
        for record in records {
            let key = (record.session_id.clone(), record.owned_id.clone());
            let who = attributions.entry(key).or_insert_with(|| attribute(record, lookups)).clone();
            let attributes = Value::Object(record.attributes.clone()).to_string();
            let written = match record.signal {
                Signal::Metric => transaction.execute(
                    "INSERT INTO otel_metric_points (time_ms, name, unit, value, service, session_id, owned_id, run_id, node_run_id, model, attributes)
                     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)",
                    params![record.time_ms, record.name, record.unit, record.value, record.service, record.session_id,
                        who.owned_id, who.run_id, who.node_run_id, text(&record.attributes, &["gen_ai.response.model", "gen_ai.request.model", "model"]), attributes]),
                signal => {
                    let fields = GenAi::read(record);
                    let (table, extra_columns, extra_values) = if signal == Signal::Span {
                        ("otel_spans", ", end_ms, parent_span_id", ", ?22, ?23")
                    } else {
                        ("otel_log_events", "", "")
                    };
                    let sql = format!(
                        "INSERT INTO {table} (time_ms, trace_id, span_id, name, service, session_id, owned_id, run_id, node_run_id, model, operation,
                            input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_micros, duration_ms, ttft_ms, request_id,
                            tool_name, status, attributes{extra_columns})
                         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20, ?21, ?24{extra_values})");
                    let mut statement = transaction.prepare_cached(&sql).map_err(|error| format!("Could not prepare a telemetry write: {error}"))?;
                    statement.execute(params![record.time_ms, record.trace_id, record.span_id, record.name, record.service, record.session_id,
                        who.owned_id, who.run_id, who.node_run_id, fields.model, fields.operation, fields.input_tokens, fields.output_tokens,
                        fields.cache_read_tokens, fields.cache_write_tokens, fields.cost_micros, fields.duration_ms, fields.ttft_ms,
                        fields.request_id, fields.tool_name, fields.status, record.end_ms, record.parent_span_id, attributes])
                }
            };
            written.map_err(|error| format!("Could not write telemetry: {error}"))?;
        }
        transaction.commit().map_err(|error| format!("Could not finish a telemetry write: {error}"))
    }
}

/// The provider session id decides the Assembly session: one adapter process
/// serves many sessions, so a tag set on the process can't. The tag is used
/// when the session id names no live session.
fn attribute(record: &Record, lookups: &Lookups) -> Attribution {
    let owned_id = record.session_id.as_deref().and_then(|id| (lookups.owned_for_session)(id)).or_else(|| record.owned_id.clone());
    let step = owned_id.as_deref().and_then(|id| (lookups.step_for_owned)(id));
    let (run_id, node_run_id) = step.map_or((None, None), |(run, node)| (Some(run), Some(node)));
    Attribution { owned_id, run_id, node_run_id }
}

/// The GenAI fields promoted to columns. Each is read from the OpenTelemetry
/// GenAI name first (what TanStack AI's `otelMiddleware` writes), then from
/// Claude Code's own name.
struct GenAi {
    model: Option<String>,
    operation: Option<String>,
    input_tokens: Option<i64>,
    output_tokens: Option<i64>,
    cache_read_tokens: Option<i64>,
    cache_write_tokens: Option<i64>,
    cost_micros: Option<i64>,
    duration_ms: Option<i64>,
    ttft_ms: Option<i64>,
    request_id: Option<String>,
    tool_name: Option<String>,
    status: Option<String>,
}

impl GenAi {
    fn read(record: &Record) -> Self {
        let attributes = &record.attributes;
        let cost_micros = int(attributes, &["cost_usd_micros"])
            .or_else(|| float(attributes, &["gen_ai.usage.cost", "cost_usd"]).map(|usd| (usd * 1_000_000.0).round() as i64));
        let duration_ms = record.end_ms.map(|end| end - record.time_ms).or_else(|| int(attributes, &["duration_ms"]));
        Self {
            model: text(attributes, &["gen_ai.response.model", "gen_ai.request.model", "model"]),
            operation: text(attributes, &["gen_ai.operation.name", "event.name"]),
            input_tokens: int(attributes, &["gen_ai.usage.input_tokens", "input_tokens"]),
            output_tokens: int(attributes, &["gen_ai.usage.output_tokens", "output_tokens"]),
            cache_read_tokens: int(attributes, &["gen_ai.usage.cache_read.input_tokens", "cache_read_tokens"]),
            cache_write_tokens: int(attributes, &["gen_ai.usage.cache_creation.input_tokens", "cache_creation_tokens"]),
            cost_micros,
            duration_ms,
            ttft_ms: int(attributes, &["ttft_ms"]),
            request_id: text(attributes, &["gen_ai.response.id", "request_id"]),
            tool_name: text(attributes, &["gen_ai.tool.name", "tool_name"]),
            status: ["otel.status_code", "status", "success"].iter().find_map(|key| match attributes.get(*key)? {
                Value::String(text) => Some(text.clone()),
                Value::Null => None,
                other => Some(other.to_string()),
            }),
        }
    }
}

fn text(attributes: &Map<String, Value>, keys: &[&str]) -> Option<String> {
    keys.iter().find_map(|key| attributes.get(*key)?.as_str().map(str::to_string))
}

/// Claude Code sends some counts as strings (`"duration_ms": "13"`).
fn float(attributes: &Map<String, Value>, keys: &[&str]) -> Option<f64> {
    keys.iter().find_map(|key| {
        let value = attributes.get(*key)?;
        value.as_f64().or_else(|| value.as_str()?.parse().ok())
    })
}

fn int(attributes: &Map<String, Value>, keys: &[&str]) -> Option<i64> {
    float(attributes, keys).map(|value| value.round() as i64)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::usage_history::otel::{parse, write};
    use serde_json::json;
    use std::sync::atomic::AtomicU64;
    use std::sync::mpsc::sync_channel;
    use std::sync::Arc;

    fn kv(key: &str, value: &str) -> Value {
        json!({"key": key, "value": {"stringValue": value}})
    }

    fn int_kv(key: &str, value: i64) -> Value {
        json!({"key": key, "value": {"intValue": value.to_string()}})
    }

    fn database(name: &str) -> std::path::PathBuf {
        let folder = std::env::temp_dir().join(format!("assembly-otel-{name}-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&folder).unwrap();
        folder.join("usage-history.sqlite3")
    }

    fn lookups() -> Lookups {
        Lookups {
            owned_for_session: Box::new(|id| (id == "native-1").then(|| "owned-live".to_string())),
            step_for_owned: Box::new(|owned| (owned == "owned-live").then(|| ("run-1".to_string(), "node-run-1".to_string()))),
        }
    }

    /// Two Claude Code log records as the CLI sends them: identity on every
    /// record, an API call, and a tool result.
    fn claude_logs(session: &str) -> Value {
        let identity = |mut attributes: Vec<Value>| {
            attributes.extend([kv("session.id", session), kv("user.email", "person@example.com"), kv("user.id", "hashed-user"),
                kv("organization.id", "org-123"), kv("user.account_uuid", "acct-uuid"), kv("user.account_id", "acct-id"),
                kv("assembly.owned_id", "owned-tag")]);
            attributes
        };
        json!({"resourceLogs": [{
            "resource": {"attributes": [kv("service.name", "claude-code"), kv("user.email", "person@example.com")]},
            "scopeLogs": [{"logRecords": [
                {"timeUnixNano": "1792616407067000000", "body": {"stringValue": "claude_code.api_request"}, "attributes": identity(vec![
                    kv("event.name", "api_request"), kv("model", "claude-opus-5-5"), int_kv("input_tokens", 2), int_kv("output_tokens", 4),
                    int_kv("cache_read_tokens", 10836), int_kv("cache_creation_tokens", 25790), int_kv("cost_usd_micros", 208575),
                    int_kv("duration_ms", 3363), int_kv("ttft_ms", 3311), kv("request_id", "req_1"), kv("prompt", "a secret prompt")])},
                {"timeUnixNano": "1792616408000000000", "body": {"stringValue": "claude_code.tool_result"}, "attributes": identity(vec![
                    kv("event.name", "tool_result"), kv("tool_name", "Bash"), kv("success", "true"), kv("duration_ms", "120"),
                    kv("tool_parameters", "{\"command\":\"cat secrets\"}"), kv("prompt_length", "22")])}
            ]}]
        }]})
    }

    fn store(path: &std::path::Path, request: &Value) {
        let (queue, records) = sync_channel(16);
        for record in parse(super::super::otel::Signal::Log, request) {
            queue.send(record).unwrap();
        }
        drop(queue);
        write(records, OtelStore::open(path).unwrap(), lookups(), Arc::new(AtomicU64::new(0)));
    }

    #[test]
    fn an_api_request_is_stored_with_its_genai_columns_and_its_step() {
        let path = database("columns");
        store(&path, &claude_logs("native-1"));
        let connection = Connection::open(&path).unwrap();
        let row: (String, i64, i64, i64, i64, i64, i64, i64, String, String, String) = connection.query_row(
            "SELECT model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_micros, duration_ms, ttft_ms,
                    owned_id, run_id, node_run_id FROM otel_log_events WHERE request_id = 'req_1'", [],
            |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?, row.get(4)?, row.get(5)?, row.get(6)?, row.get(7)?,
                row.get(8)?, row.get(9)?, row.get(10)?))).unwrap();
        assert_eq!(row, ("claude-opus-5-5".into(), 2, 4, 10836, 25790, 208575, 3363, 3311, "owned-live".into(), "run-1".into(), "node-run-1".into()));
        let tool: (String, i64, String, String) = connection.query_row(
            "SELECT tool_name, duration_ms, status, operation FROM otel_log_events WHERE name = 'claude_code.tool_result'", [],
            |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?))).unwrap();
        assert_eq!(tool, ("Bash".into(), 120, "true".into(), "tool_result".into()));
    }

    #[test]
    fn identity_and_content_never_reach_the_database() {
        let path = database("redaction");
        store(&path, &claude_logs("native-1"));
        let connection = Connection::open(&path).unwrap();
        let stored: String = connection.query_row("SELECT group_concat(attributes, ' ') FROM otel_log_events", [], |row| row.get(0)).unwrap();
        for secret in ["person@example.com", "hashed-user", "org-123", "acct-uuid", "acct-id", "a secret prompt", "cat secrets"] {
            assert!(!stored.contains(secret), "{secret} was stored: {stored}");
        }
        assert!(stored.contains("\"prompt_length\":\"22\""), "{stored}");
    }

    #[test]
    fn a_session_id_with_no_live_session_falls_back_to_the_tag() {
        let path = database("tag");
        store(&path, &claude_logs("native-gone"));
        let connection = Connection::open(&path).unwrap();
        let (owned, step): (String, Option<String>) = connection.query_row(
            "SELECT owned_id, node_run_id FROM otel_log_events WHERE request_id = 'req_1'", [], |row| Ok((row.get(0)?, row.get(1)?))).unwrap();
        assert_eq!((owned.as_str(), step), ("owned-tag", None));
    }

    fn seeded(signal: Signal, name: &str, owned: &str, attributes: Value) -> Record {
        Record { signal, name: name.into(), time_ms: 1_000, end_ms: None, trace_id: None, span_id: None, parent_span_id: None,
            unit: None, value: None, service: None, session_id: None, owned_id: Some(owned.into()),
            attributes: attributes.as_object().unwrap().clone() }
    }

    fn span(name: &str, id: &str, parent: Option<&str>, (start, end): (i64, i64), attributes: Value) -> Record {
        Record { trace_id: Some("trace-1".into()), span_id: Some(id.into()), parent_span_id: parent.map(str::to_string),
            time_ms: start, end_ms: Some(end), ..seeded(Signal::Span, name, "owned-b", attributes) }
    }

    fn view(connection: &Connection, sql: &str) -> Vec<Vec<Option<i64>>> {
        let mut statement = connection.prepare(sql).unwrap();
        let columns = statement.column_count();
        statement.query_map([], |row| (0..columns).map(|i| row.get(i)).collect()).unwrap().map(Result::unwrap).collect()
    }

    #[test]
    fn report_views_add_up_cost_tokens_latency_and_tool_time() {
        let path = database("views");
        let mut store = OtelStore::open(&path).unwrap();
        let steps = Lookups {
            owned_for_session: Box::new(|_| None),
            step_for_owned: Box::new(|owned| Some(("run-1".to_string(), if owned == "owned-a" { "step-a" } else { "step-b" }.to_string()))),
        };
        let call = |input: i64, output: i64, cost: i64, duration: i64, ttft: i64| json!({"model": "m1", "input_tokens": input,
            "output_tokens": output, "cache_read_tokens": 100, "cache_creation_tokens": 50, "cost_usd_micros": cost,
            "duration_ms": duration, "ttft_ms": ttft});
        store.insert(&[
            seeded(Signal::Log, "claude_code.api_request", "owned-a", call(10, 5, 1000, 2000, 500)),
            seeded(Signal::Log, "claude_code.api_request", "owned-a", call(20, 10, 3000, 4000, 1500)),
            seeded(Signal::Log, "claude_code.assistant_response", "owned-a", json!({"model": "m1"})),
            seeded(Signal::Log, "claude_code.tool_result", "owned-a", json!({"tool_name": "Bash", "duration_ms": "300"})),
            seeded(Signal::Metric, "claude_code.cost.usage", "owned-a", json!({"model": "m1"})),
            // TanStack AI's chat span repeats its iterations' usage, so only the iteration counts.
            span("chat m2", "s1", None, (0, 1000), json!({"gen_ai.request.model": "m2", "gen_ai.usage.input_tokens": 7,
                "gen_ai.usage.output_tokens": 3, "gen_ai.usage.cost": 0.002})),
            span("chat m2 #1", "s2", Some("s1"), (0, 900), json!({"gen_ai.request.model": "m2", "gen_ai.usage.input_tokens": 7,
                "gen_ai.usage.output_tokens": 3, "gen_ai.usage.cost": 0.002})),
            span("execute_tool search", "s3", Some("s2"), (100, 350), json!({"gen_ai.tool.name": "search", "gen_ai.operation.name": "execute_tool"})),
        ], &steps).unwrap();
        let connection = Connection::open(&path).unwrap();
        let measures = "model_calls, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_micros, model_ms, avg_call_ms, slowest_call_ms, avg_ttft_ms, tool_calls, tool_ms";
        let n = |values: [i64; 12]| values.map(|value| (value >= 0).then_some(value)).to_vec();
        let step_a = n([2, 30, 15, 200, 100, 4000, 6000, 3000, 4000, 1000, 1, 300]);
        let step_b = n([1, 7, 3, -1, -1, 2000, 900, 900, 900, -1, 1, 250]);
        assert_eq!(view(&connection, &format!("SELECT {measures} FROM otel_report_by_step ORDER BY node_run_id")), [step_a.clone(), step_b.clone()]);
        assert_eq!(view(&connection, &format!("SELECT {measures} FROM otel_report_by_session ORDER BY owned_id")), [step_a.clone(), step_b.clone()]);
        assert_eq!(view(&connection, &format!("SELECT {measures} FROM otel_report_by_run")), [n([3, 37, 18, 200, 100, 6000, 6900, 2300, 4000, 1000, 2, 550])]);
        assert_eq!(view(&connection, &format!("SELECT {measures} FROM otel_report_by_model ORDER BY model")),
            [n([2, 30, 15, 200, 100, 4000, 6000, 3000, 4000, 1000, 0, -1]), n([1, 7, 3, -1, -1, 2000, 900, 900, 900, -1, 0, -1])]);
        let keys: Vec<(String, String)> = connection.prepare("SELECT run_id, node_run_id FROM otel_report_by_step ORDER BY node_run_id").unwrap()
            .query_map([], |row| Ok((row.get(0)?, row.get(1)?))).unwrap().map(Result::unwrap).collect();
        assert_eq!(keys, [("run-1".into(), "step-a".into()), ("run-1".into(), "step-b".into())]);
    }

    #[test]
    fn rows_older_than_the_cutoff_are_deleted() {
        let path = database("retention");
        store(&path, &claude_logs("native-1"));
        let store = OtelStore::open(&path).unwrap();
        assert_eq!(store.delete_older_than(1_792_616_407_500).unwrap(), 1);
        let left: i64 = store.connection.query_row("SELECT COUNT(*) FROM otel_log_events", [], |row| row.get(0)).unwrap();
        assert_eq!(left, 1);
    }
}
