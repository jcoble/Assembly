use rusqlite::{
    params, params_from_iter,
    types::{Value as SqlValue, ValueRef},
    Connection, TransactionBehavior,
};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;

pub const USAGE_SCHEMA: &str = include_str!("../../migrations/0001_usage_history.sql");
#[path = "usage_acp.rs"] pub mod acp;
const REBUILD_ROLLUPS_SQL: &str = "DELETE FROM usage_daily_rollups;\nINSERT INTO usage_daily_rollups (day, provider, model, project_id, event_count, session_count, turn_count, workflow_count, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_micros) SELECT date(occurred_at_micros / 1000000, 'unixepoch'), provider, model, project_id, COUNT(*), COUNT(DISTINCT owned_id), COUNT(DISTINCT turn_id), COUNT(DISTINCT workflow_id), SUM(input_tokens), SUM(output_tokens), SUM(cache_read_tokens), SUM(cache_write_tokens), SUM(reasoning_tokens), SUM(estimated_cost_micros) FROM usage_events GROUP BY date(occurred_at_micros / 1000000, 'unixepoch'), provider, model, project_id";

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsageEvent {
    pub provider: String,
    pub provider_instance_id: String,
    pub owned_id: Option<String>,
    pub workflow_id: Option<String>,
    pub turn_id: Option<String>,
    pub project_id: Option<String>,
    pub workspace_id: Option<String>,
    pub occurred_at_micros: i64,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub cache_read_tokens: u64,
    pub cache_write_tokens: u64,
    pub reasoning_tokens: u64,
    pub model: String,
    pub estimated_cost_micros: Option<i64>,
    pub estimate_rate_version: Option<String>,
    pub source_kind: String,
    pub source_event_id: String,
    pub source_key: String,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsageSourceCursor {
    pub provider: String,
    pub provider_instance_id: String,
    pub source_kind: String,
    pub source_key: String,
    pub file_identity: String,
    pub offset: u64,
    pub size: u64,
    pub modified_at_micros: i64,
    pub last_event_id: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsageFilter {
    pub provider: Option<String>,
    pub model: Option<String>,
    pub project_id: Option<String>,
    pub workflow_id: Option<String>,
    pub start_micros: Option<i64>,
    pub end_micros: Option<i64>,
    pub limit: Option<usize>,
    pub offset: Option<usize>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsageSummary {
    pub event_count: u64,
    pub provider_count: u64,
    pub active_days: u64,
    pub session_count: u64,
    pub turn_count: u64,
    pub workflow_count: u64,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub cache_read_tokens: u64,
    pub cache_write_tokens: u64,
    pub reasoning_tokens: u64,
    pub total_tokens: u64,
    pub cache_share_percent: u64,
    pub estimated_cost_micros: Option<i64>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsageBreakdownRow {
    pub provider: String,
    pub model: String,
    pub project_id: Option<String>,
    pub event_count: u64,
    pub session_count: u64,
    pub turn_count: u64,
    pub workflow_count: u64,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub total_count: u64,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsageProviderSummaryRow {
    pub provider: String,
    pub model_count: u64,
    pub event_count: u64,
    pub session_count: u64,
    pub turn_count: u64,
    pub workflow_count: u64,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub cache_read_tokens: u64,
    pub cache_write_tokens: u64,
    pub reasoning_tokens: u64,
    pub estimated_cost_micros: Option<i64>,
    pub unpriced_percent: u64,
    pub total_tokens: u64,
    pub range_total_tokens: u64,
    pub range_estimated_cost_micros: Option<i64>,
    pub range_unpriced_percent: u64,
    pub range_share_percent: u64,
    pub last_model: String,
    pub last_session_id: Option<String>,
    pub last_project_id: Option<String>,
    pub last_seen_at_micros: Option<i64>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsageTokenBreakdown {
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub cache_read_tokens: u64,
    pub cache_write_tokens: u64,
    pub reasoning_tokens: u64,
    pub total_tokens: u64,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsageCostInputRow {
    pub provider: String,
    pub model: String,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub cache_read_tokens: u64,
    pub cache_write_tokens: u64,
    pub reasoning_tokens: u64,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsageDailyRow {
    pub day: String,
    pub provider: String,
    pub model: String,
    pub project_id: Option<String>,
    pub event_count: u64,
    pub session_count: u64,
    pub turn_count: u64,
    pub workflow_count: u64,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub estimated_cost_micros: Option<i64>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsageDailyTotalsRow {
    pub day: String,
    pub event_count: u64,
    pub session_count: u64,
    pub turn_count: u64,
    pub workflow_count: u64,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub cache_read_tokens: u64,
    pub cache_write_tokens: u64,
    pub reasoning_tokens: u64,
    pub estimated_cost_micros: Option<i64>,
    pub total_tokens: u64,
    pub range_max_tokens: u64,
    pub best_day: String,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsageProviderDailyTotalsRow {
    pub day: String,
    pub provider: String,
    pub event_count: u64,
    pub session_count: u64,
    pub turn_count: u64,
    pub workflow_count: u64,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub cache_read_tokens: u64,
    pub cache_write_tokens: u64,
    pub reasoning_tokens: u64,
    pub estimated_cost_micros: Option<i64>,
    pub total_tokens: u64,
}

#[derive(Debug, Clone)]
pub struct UsageDb {
    path: PathBuf,
}

impl UsageDb {
    pub fn open(path: impl Into<PathBuf>) -> Result<Self, String> {
        let path = path.into();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)
                .map_err(|error| format!("Could not create usage database folder: {error}"))?;
        }
        let db = Self { path };
        let mut connection = db.connection()?;
        let transaction = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|error| db.database_error("could not start schema migration", error))?;
        transaction
            .execute_batch(USAGE_SCHEMA)
            .and_then(|()| acp::upgrade(&transaction))
            .map_err(|error| db.database_error("could not apply schema migration", error))?;
        transaction
            .commit()
            .map_err(|error| db.database_error("could not finish schema migration", error))?;
        Ok(db)
    }

    /// Read every durable source cursor in one DB-side query so an indexing
    /// pass can decide which bytes are new without opening one transaction per
    /// source file.
    pub fn read_source_cursors(&self) -> Result<HashMap<String, UsageSourceCursor>, String> {
        let rows = self.query("SELECT provider, provider_instance_id, source_kind, source_key, file_identity, offset_bytes, size_bytes, modified_at_micros, last_event_id FROM usage_source_cursors", &[])?;
        let mut cursors = HashMap::with_capacity(rows.len());
        for row in rows {
            let cursor = UsageSourceCursor {
                provider: value_string(&row, "provider"),
                provider_instance_id: value_string(&row, "provider_instance_id"),
                source_kind: value_string(&row, "source_kind"),
                source_key: value_string(&row, "source_key"),
                file_identity: value_string(&row, "file_identity"),
                offset: value_u64(&row, "offset_bytes"),
                size: value_u64(&row, "size_bytes"),
                modified_at_micros: value_i64(&row, "modified_at_micros").unwrap_or_default(),
                last_event_id: value_optional_string(&row, "last_event_id"),
            };
            cursors.insert(
                cursor_lookup_key(
                    &cursor.provider,
                    &cursor.provider_instance_id,
                    &cursor.source_kind,
                    &cursor.source_key,
                ),
                cursor,
            );
        }
        Ok(cursors)
    }

    #[cfg(test)]
    pub fn insert_events_with_cursor(
        &self,
        events: &[UsageEvent],
        cursor: Option<&UsageSourceCursor>,
    ) -> Result<(), String> {
        self.insert_batches(&[(events, cursor)], true)
    }

    pub fn insert_events_with_cursor_deferred_rollup(
        &self,
        events: &[UsageEvent],
        cursor: Option<&UsageSourceCursor>,
    ) -> Result<(), String> {
        self.insert_batches(&[(events, cursor)], false)
    }

    pub fn rebuild_daily_rollups(&self) -> Result<(), String> {
        let mut connection = self.connection()?;
        let transaction = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|error| self.database_error("could not start rollup rebuild", error))?;
        transaction
            .execute_batch(REBUILD_ROLLUPS_SQL)
            .map_err(|error| self.database_error("could not rebuild usage rollups", error))?;
        transaction
            .commit()
            .map_err(|error| self.database_error("could not commit usage rollups", error))
    }

    fn insert_batches(
        &self,
        batches: &[(&[UsageEvent], Option<&UsageSourceCursor>)],
        rebuild_rollups: bool,
    ) -> Result<(), String> {
        const INSERT_EVENT_SQL: &str = "INSERT INTO usage_events (provider, provider_instance_id, owned_id, workflow_id, turn_id, project_id, workspace_id, occurred_at_micros, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, model, estimated_cost_micros, estimate_rate_version, source_kind, source_event_id, source_key, inserted_at_micros) SELECT ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ? WHERE ?17 = 'acp' OR NOT EXISTS (SELECT 1 FROM usage_events WHERE source_key = ?3 AND source_kind = 'acp' AND ?8 BETWEEN occurred_at_micros AND inserted_at_micros) ON CONFLICT(provider, provider_instance_id, source_kind, source_event_id) DO UPDATE SET workflow_id=excluded.workflow_id, turn_id=excluded.turn_id, project_id=excluded.project_id, workspace_id=excluded.workspace_id, occurred_at_micros=excluded.occurred_at_micros, input_tokens=excluded.input_tokens, output_tokens=excluded.output_tokens, cache_read_tokens=excluded.cache_read_tokens, cache_write_tokens=excluded.cache_write_tokens, reasoning_tokens=excluded.reasoning_tokens, model=excluded.model, estimated_cost_micros=excluded.estimated_cost_micros, estimate_rate_version=excluded.estimate_rate_version";
        const UPSERT_CURSOR_SQL: &str = "INSERT INTO usage_source_cursors (provider, provider_instance_id, source_kind, source_key, file_identity, offset_bytes, size_bytes, modified_at_micros, last_event_id, updated_at_micros) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?) ON CONFLICT(provider, provider_instance_id, source_kind, source_key) DO UPDATE SET file_identity=excluded.file_identity, offset_bytes=excluded.offset_bytes, size_bytes=excluded.size_bytes, modified_at_micros=excluded.modified_at_micros, last_event_id=excluded.last_event_id, updated_at_micros=excluded.updated_at_micros";
        let mut connection = self.connection()?;
        let transaction = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|error| self.database_error("could not start usage batch", error))?;
        let mut has_events = false;
        {
            let mut event_statement = transaction.prepare(INSERT_EVENT_SQL).map_err(|error| {
                self.database_error("could not prepare usage event insert", error)
            })?;
            let mut cursor_statement = transaction.prepare(UPSERT_CURSOR_SQL).map_err(|error| {
                self.database_error("could not prepare usage cursor update", error)
            })?;
            for (events, cursor) in batches {
                for event in *events {
                    has_events = true;
                    validate_event(event)?;
                    event_statement
                        .execute(params![
                            &event.provider,
                            &event.provider_instance_id,
                            event.owned_id.as_deref(),
                            event.workflow_id.as_deref(),
                            event.turn_id.as_deref(),
                            event.project_id.as_deref(),
                            event.workspace_id.as_deref(),
                            event.occurred_at_micros,
                            sqlite_integer(event.input_tokens, "input token count")?,
                            sqlite_integer(event.output_tokens, "output token count")?,
                            sqlite_integer(event.cache_read_tokens, "cache read token count")?,
                            sqlite_integer(event.cache_write_tokens, "cache write token count")?,
                            sqlite_integer(event.reasoning_tokens, "reasoning token count")?,
                            &event.model,
                            event.estimated_cost_micros,
                            event.estimate_rate_version.as_deref(),
                            &event.source_kind,
                            &event.source_event_id,
                            &event.source_key,
                            now_micros(),
                        ])
                        .map_err(|error| {
                            self.database_error("could not insert usage event", error)
                        })?;
                }
                if let Some(cursor) = cursor {
                    cursor_statement
                        .execute(params![
                            &cursor.provider,
                            &cursor.provider_instance_id,
                            &cursor.source_kind,
                            &cursor.source_key,
                            &cursor.file_identity,
                            sqlite_integer(cursor.offset, "usage cursor offset")?,
                            sqlite_integer(cursor.size, "usage source size")?,
                            cursor.modified_at_micros,
                            cursor.last_event_id.as_deref(),
                            now_micros(),
                        ])
                        .map_err(|error| {
                            self.database_error("could not update usage cursor", error)
                        })?;
                }
            }
        }
        if has_events && rebuild_rollups {
            // The rollup is rebuilt with one DB-side aggregate, never by loading
            // usage rows into Rust.
            transaction
                .execute_batch(REBUILD_ROLLUPS_SQL)
                .map_err(|error| self.database_error("could not rebuild usage rollups", error))?;
        }
        transaction
            .commit()
            .map_err(|error| self.database_error("could not commit usage batch", error))
    }

    pub fn read_usage_summary(&self, filter: &UsageFilter) -> Result<UsageSummary, String> {
        let query = summary_query(filter);
        let rows = self.query(&query.sql, &query.parameters)?;
        let row = rows.first().cloned().unwrap_or(Value::Null);
        Ok(UsageSummary {
            event_count: value_u64(&row, "event_count"),
            provider_count: value_u64(&row, "provider_count"),
            active_days: value_u64(&row, "active_days"),
            session_count: value_u64(&row, "session_count"),
            turn_count: value_u64(&row, "turn_count"),
            workflow_count: value_u64(&row, "workflow_count"),
            input_tokens: value_u64(&row, "input_tokens"),
            output_tokens: value_u64(&row, "output_tokens"),
            cache_read_tokens: value_u64(&row, "cache_read_tokens"),
            cache_write_tokens: value_u64(&row, "cache_write_tokens"),
            reasoning_tokens: value_u64(&row, "reasoning_tokens"),
            total_tokens: value_u64(&row, "total_tokens"),
            cache_share_percent: value_u64(&row, "cache_share_percent"),
            estimated_cost_micros: value_i64(&row, "estimated_cost_micros"),
        })
    }

    pub fn read_usage_token_breakdown(
        &self,
        filter: &UsageFilter,
    ) -> Result<UsageTokenBreakdown, String> {
        let query = token_breakdown_query(filter);
        let rows = self.query(&query.sql, &query.parameters)?;
        let row = rows.first().cloned().unwrap_or(Value::Null);
        Ok(UsageTokenBreakdown {
            input_tokens: value_u64(&row, "input_tokens"),
            output_tokens: value_u64(&row, "output_tokens"),
            cache_read_tokens: value_u64(&row, "cache_read_tokens"),
            cache_write_tokens: value_u64(&row, "cache_write_tokens"),
            reasoning_tokens: value_u64(&row, "reasoning_tokens"),
            total_tokens: value_u64(&row, "total_tokens"),
        })
    }

    pub fn read_usage_breakdown(
        &self,
        filter: &UsageFilter,
    ) -> Result<Vec<UsageBreakdownRow>, String> {
        let query = breakdown_query(filter);
        self.query(&query.sql, &query.parameters)?
            .into_iter()
            .map(|row| {
                Ok(UsageBreakdownRow {
                    provider: value_string(&row, "provider"),
                    model: value_string(&row, "model"),
                    project_id: value_optional_string(&row, "project_id"),
                    event_count: value_u64(&row, "event_count"),
                    session_count: value_u64(&row, "session_count"),
                    turn_count: value_u64(&row, "turn_count"),
                    workflow_count: value_u64(&row, "workflow_count"),
                    input_tokens: value_u64(&row, "input_tokens"),
                    output_tokens: value_u64(&row, "output_tokens"),
                    total_count: value_u64(&row, "total_count"),
                })
            })
            .collect()
    }

    pub fn read_usage_provider_summary(
        &self,
        filter: &UsageFilter,
    ) -> Result<Vec<UsageProviderSummaryRow>, String> {
        let query = provider_summary_query(filter);
        self.query(&query.sql, &query.parameters)?
            .into_iter()
            .map(|row| {
                Ok(UsageProviderSummaryRow {
                    provider: value_string(&row, "provider"),
                    model_count: value_u64(&row, "model_count"),
                    event_count: value_u64(&row, "event_count"),
                    session_count: value_u64(&row, "session_count"),
                    turn_count: value_u64(&row, "turn_count"),
                    workflow_count: value_u64(&row, "workflow_count"),
                    input_tokens: value_u64(&row, "input_tokens"),
                    output_tokens: value_u64(&row, "output_tokens"),
                    cache_read_tokens: value_u64(&row, "cache_read_tokens"),
                    cache_write_tokens: value_u64(&row, "cache_write_tokens"),
                    reasoning_tokens: value_u64(&row, "reasoning_tokens"),
                    estimated_cost_micros: value_i64(&row, "estimated_cost_micros"),
                    unpriced_percent: value_u64(&row, "unpriced_percent"),
                    total_tokens: value_u64(&row, "total_tokens"),
                    range_total_tokens: value_u64(&row, "range_total_tokens"),
                    range_estimated_cost_micros: value_i64(&row, "range_estimated_cost_micros"),
                    range_unpriced_percent: value_u64(&row, "range_unpriced_percent"),
                    range_share_percent: value_u64(&row, "range_share_percent"),
                    last_model: value_string(&row, "last_model"),
                    last_session_id: value_optional_string(&row, "last_session_id"),
                    last_project_id: value_optional_string(&row, "last_project_id"),
                    last_seen_at_micros: value_i64(&row, "last_seen_at_micros"),
                })
            })
            .collect()
    }

    pub fn read_daily(&self, filter: &UsageFilter) -> Result<Vec<UsageDailyRow>, String> {
        let query = daily_query(filter);
        self.query(&query.sql, &query.parameters)?
            .into_iter()
            .map(|row| {
                Ok(UsageDailyRow {
                    day: value_string(&row, "day"),
                    provider: value_string(&row, "provider"),
                    model: value_string(&row, "model"),
                    project_id: value_optional_string(&row, "project_id"),
                    event_count: value_u64(&row, "event_count"),
                    session_count: value_u64(&row, "session_count"),
                    turn_count: value_u64(&row, "turn_count"),
                    workflow_count: value_u64(&row, "workflow_count"),
                    input_tokens: value_u64(&row, "input_tokens"),
                    output_tokens: value_u64(&row, "output_tokens"),
                    estimated_cost_micros: value_i64(&row, "estimated_cost_micros"),
                })
            })
            .collect()
    }

    pub fn read_usage_daily_totals(
        &self,
        filter: &UsageFilter,
    ) -> Result<Vec<UsageDailyTotalsRow>, String> {
        let query = daily_totals_query(filter);
        self.query(&query.sql, &query.parameters)?
            .into_iter()
            .map(|row| {
                Ok(UsageDailyTotalsRow {
                    day: value_string(&row, "day"),
                    event_count: value_u64(&row, "event_count"),
                    session_count: value_u64(&row, "session_count"),
                    turn_count: value_u64(&row, "turn_count"),
                    workflow_count: value_u64(&row, "workflow_count"),
                    input_tokens: value_u64(&row, "input_tokens"),
                    output_tokens: value_u64(&row, "output_tokens"),
                    cache_read_tokens: value_u64(&row, "cache_read_tokens"),
                    cache_write_tokens: value_u64(&row, "cache_write_tokens"),
                    reasoning_tokens: value_u64(&row, "reasoning_tokens"),
                    estimated_cost_micros: value_i64(&row, "estimated_cost_micros"),
                    total_tokens: value_u64(&row, "total_tokens"),
                    range_max_tokens: value_u64(&row, "range_max_tokens"),
                    best_day: value_string(&row, "best_day"),
                })
            })
            .collect()
    }

    pub fn read_usage_provider_daily_totals(
        &self,
        filter: &UsageFilter,
    ) -> Result<Vec<UsageProviderDailyTotalsRow>, String> {
        let query = provider_daily_totals_query(filter);
        self.query(&query.sql, &query.parameters)?
            .into_iter()
            .map(|row| {
                Ok(UsageProviderDailyTotalsRow {
                    day: value_string(&row, "day"),
                    provider: value_string(&row, "provider"),
                    event_count: value_u64(&row, "event_count"),
                    session_count: value_u64(&row, "session_count"),
                    turn_count: value_u64(&row, "turn_count"),
                    workflow_count: value_u64(&row, "workflow_count"),
                    input_tokens: value_u64(&row, "input_tokens"),
                    output_tokens: value_u64(&row, "output_tokens"),
                    cache_read_tokens: value_u64(&row, "cache_read_tokens"),
                    cache_write_tokens: value_u64(&row, "cache_write_tokens"),
                    reasoning_tokens: value_u64(&row, "reasoning_tokens"),
                    estimated_cost_micros: value_i64(&row, "estimated_cost_micros"),
                    total_tokens: value_u64(&row, "total_tokens"),
                })
            })
            .collect()
    }

    pub fn read_usage_cost_inputs(
        &self,
        filter: &UsageFilter,
    ) -> Result<Vec<UsageCostInputRow>, String> {
        let query = cost_inputs_query(filter);
        self.query(&query.sql, &query.parameters)?
            .into_iter()
            .map(|row| {
                Ok(UsageCostInputRow {
                    provider: value_string(&row, "provider"),
                    model: value_string(&row, "model"),
                    input_tokens: value_u64(&row, "input_tokens"),
                    output_tokens: value_u64(&row, "output_tokens"),
                    cache_read_tokens: value_u64(&row, "cache_read_tokens"),
                    cache_write_tokens: value_u64(&row, "cache_write_tokens"),
                    reasoning_tokens: value_u64(&row, "reasoning_tokens"),
                })
            })
            .collect()
    }

    #[cfg(test)]
    fn explain_breakdown(&self, filter: &UsageFilter) -> Result<Vec<Value>, String> {
        let query = breakdown_query(filter);
        self.query(
            &format!("EXPLAIN QUERY PLAN {}", query.sql),
            &query.parameters,
        )
    }

    pub(crate) fn connection(&self) -> Result<Connection, String> {
        Connection::open(&self.path)
            .map_err(|error| self.database_error("could not open usage history", error))
    }

    pub(crate) fn database_error(&self, action: &str, error: rusqlite::Error) -> String {
        format!(
            "Usage history database {action} for {}: {error}",
            self.path.display()
        )
    }

    fn query(&self, sql: &str, parameters: &[SqlValue]) -> Result<Vec<Value>, String> {
        trace_sql(sql);
        let connection = self.connection()?;
        let mut statement = connection
            .prepare(sql)
            .map_err(|error| self.database_error("could not prepare usage query", error))?;
        let column_names = statement
            .column_names()
            .into_iter()
            .map(ToString::to_string)
            .collect::<Vec<_>>();
        let mut rows = statement
            .query(params_from_iter(parameters.iter()))
            .map_err(|error| self.database_error("could not run usage query", error))?;
        let mut values = Vec::new();
        while let Some(row) = rows
            .next()
            .map_err(|error| self.database_error("could not read usage query result", error))?
        {
            let mut object = serde_json::Map::with_capacity(column_names.len());
            for (index, column_name) in column_names.iter().enumerate() {
                let value = row.get_ref(index).map_err(|error| {
                    self.database_error("could not decode usage query result", error)
                })?;
                object.insert(column_name.clone(), sqlite_value_to_json(value));
            }
            values.push(Value::Object(object));
        }
        Ok(values)
    }
}

fn validate_event(event: &UsageEvent) -> Result<(), String> {
    if event.provider.trim().is_empty()
        || event.provider_instance_id.trim().is_empty()
        || event.model.trim().is_empty()
        || event.source_kind.trim().is_empty()
        || event.source_event_id.trim().is_empty()
    {
        return Err(
            "Usage events need provider, instance, model, source kind, and source event id"
                .to_string(),
        );
    }
    Ok(())
}

struct UsageQuery {
    sql: String,
    parameters: Vec<SqlValue>,
}

// Codex cache is included in input_tokens; Claude buckets are separate (usage_sources.rs:235–238,309–312).
// Proven by tests::token_breakdown_filters_the_range_and_separates_cached_codex_input.
// Keep the source rows lossless, then normalize "new input" inside every
// analytics aggregate so cache is neither double-counted nor double-billed.
const MAX_USAGE_QUERY_ROWS: usize = 200;
const NORMALIZED_RATE_MODEL_SQL: &str = "CASE WHEN lower(model) GLOB '*-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]' THEN substr(lower(model), 1, length(model) - 9) WHEN lower(model) GLOB '*-[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' THEN substr(lower(model), 1, length(model) - 11) ELSE lower(model) END";

fn summary_query(filter: &UsageFilter) -> UsageQuery {
    let (where_clause, parameters) = where_clause(filter);
    UsageQuery {
        sql: format!("WITH totals AS (SELECT COUNT(*) AS event_count, COUNT(DISTINCT provider) AS provider_count, COUNT(DISTINCT date(occurred_at_micros / 1000000, 'unixepoch')) AS active_days, COUNT(DISTINCT owned_id) AS session_count, COUNT(DISTINCT turn_id) AS turn_count, COUNT(DISTINCT workflow_id) AS workflow_count, COALESCE(SUM(input_tokens), 0) AS input_tokens, COALESCE(SUM(output_tokens), 0) AS output_tokens, COALESCE(SUM(cache_read_tokens), 0) AS cache_read_tokens, COALESCE(SUM(cache_write_tokens), 0) AS cache_write_tokens, COALESCE(SUM(reasoning_tokens), 0) AS reasoning_tokens, SUM(estimated_cost_micros) AS estimated_cost_micros FROM usage_events WHERE {where_clause}), measured AS (SELECT *, input_tokens + output_tokens + cache_read_tokens + cache_write_tokens AS total_tokens FROM totals) SELECT *, CASE WHEN total_tokens = 0 THEN 0 ELSE CAST(ROUND(100.0 * (cache_read_tokens + cache_write_tokens) / total_tokens) AS INTEGER) END AS cache_share_percent FROM measured"),
        parameters,
    }
}

fn token_breakdown_query(filter: &UsageFilter) -> UsageQuery {
    let (where_clause, parameters) = where_clause(filter);
    UsageQuery {
        sql: format!("WITH totals AS (SELECT COALESCE(SUM(input_tokens), 0) AS input_tokens, COALESCE(SUM(output_tokens), 0) AS output_tokens, COALESCE(SUM(cache_read_tokens), 0) AS cache_read_tokens, COALESCE(SUM(cache_write_tokens), 0) AS cache_write_tokens, COALESCE(SUM(reasoning_tokens), 0) AS reasoning_tokens FROM usage_events WHERE {where_clause}) SELECT input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, input_tokens + output_tokens + cache_read_tokens + cache_write_tokens AS total_tokens FROM totals"),
        parameters,
    }
}

fn breakdown_query(filter: &UsageFilter) -> UsageQuery {
    let limit = filter.limit.unwrap_or(20).clamp(1, MAX_USAGE_QUERY_ROWS);
    let offset = filter.offset.unwrap_or(0).min(100_000);
    let (where_clause, mut parameters) = where_clause(filter);
    parameters.push(SqlValue::Integer(limit as i64));
    parameters.push(SqlValue::Integer(offset as i64));
    UsageQuery {
        sql: format!("WITH filtered AS (SELECT provider, model, project_id, owned_id, turn_id, workflow_id, input_tokens, output_tokens FROM usage_events WHERE {where_clause}), grouped AS (SELECT provider, model, project_id, COUNT(*) AS event_count, COUNT(DISTINCT owned_id) AS session_count, COUNT(DISTINCT turn_id) AS turn_count, COUNT(DISTINCT workflow_id) AS workflow_count, SUM(input_tokens) AS input_tokens, SUM(output_tokens) AS output_tokens FROM filtered GROUP BY provider, model, project_id) SELECT provider, model, project_id, event_count, session_count, turn_count, workflow_count, input_tokens, output_tokens, COUNT(*) OVER () AS total_count FROM grouped ORDER BY provider ASC, model ASC, project_id ASC LIMIT ? OFFSET ?"),
        parameters,
    }
}

fn provider_summary_query(filter: &UsageFilter) -> UsageQuery {
    let limit = filter.limit.unwrap_or(20).clamp(1, MAX_USAGE_QUERY_ROWS);
    let offset = filter.offset.unwrap_or(0).min(100_000);
    let (where_clause, mut parameters) = where_clause(filter);
    parameters.push(SqlValue::Integer(limit as i64));
    parameters.push(SqlValue::Integer(offset as i64));
    UsageQuery {
        sql: format!("WITH filtered AS (SELECT id, provider, model, owned_id AS session_id, project_id, turn_id, workflow_id, occurred_at_micros, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens FROM usage_events WHERE {where_clause}), provider_totals AS (SELECT provider, COUNT(DISTINCT model) AS model_count, COUNT(*) AS event_count, COUNT(DISTINCT session_id) AS session_count, COUNT(DISTINCT turn_id) AS turn_count, COUNT(DISTINCT workflow_id) AS workflow_count, COALESCE(SUM(input_tokens), 0) AS input_tokens, COALESCE(SUM(output_tokens), 0) AS output_tokens, COALESCE(SUM(cache_read_tokens), 0) AS cache_read_tokens, COALESCE(SUM(cache_write_tokens), 0) AS cache_write_tokens, COALESCE(SUM(reasoning_tokens), 0) AS reasoning_tokens FROM filtered GROUP BY provider), model_totals AS (SELECT provider, {NORMALIZED_RATE_MODEL_SQL} AS rate_model, COALESCE(SUM(input_tokens), 0) AS input_tokens, COALESCE(SUM(output_tokens), 0) AS output_tokens, COALESCE(SUM(cache_read_tokens), 0) AS cache_read_tokens, COALESCE(SUM(cache_write_tokens), 0) AS cache_write_tokens FROM filtered GROUP BY provider, model), provider_costs AS (SELECT model_totals.provider, CAST(ROUND(SUM(CASE WHEN rates.model IS NULL THEN 0 ELSE model_totals.input_tokens * rates.input_rate + model_totals.output_tokens * rates.output_rate + model_totals.cache_read_tokens * rates.cache_read_rate + model_totals.cache_write_tokens * rates.cache_write_rate END)) AS INTEGER) AS priced_cost_micros, COALESCE(SUM(CASE WHEN rates.model IS NULL THEN model_totals.input_tokens + model_totals.output_tokens + model_totals.cache_read_tokens + model_totals.cache_write_tokens ELSE 0 END), 0) AS unpriced_tokens, COALESCE(SUM(model_totals.input_tokens + model_totals.output_tokens + model_totals.cache_read_tokens + model_totals.cache_write_tokens), 0) AS cost_total_tokens, COALESCE(SUM(CASE WHEN rates.model IS NULL THEN 0 ELSE model_totals.input_tokens + model_totals.output_tokens + model_totals.cache_read_tokens + model_totals.cache_write_tokens END), 0) AS priced_tokens FROM model_totals LEFT JOIN usage_rates rates ON rates.model = model_totals.rate_model GROUP BY model_totals.provider), measured AS (SELECT provider_totals.*, provider_totals.input_tokens + provider_totals.output_tokens + provider_totals.cache_read_tokens + provider_totals.cache_write_tokens AS total_tokens, provider_costs.priced_cost_micros, provider_costs.unpriced_tokens, provider_costs.cost_total_tokens, provider_costs.priced_tokens FROM provider_totals JOIN provider_costs ON provider_costs.provider = provider_totals.provider), decorated AS (SELECT *, SUM(total_tokens) OVER () AS range_total_tokens, SUM(priced_cost_micros) OVER () AS range_priced_cost_micros, SUM(unpriced_tokens) OVER () AS range_unpriced_tokens, SUM(cost_total_tokens) OVER () AS range_cost_total_tokens, SUM(priced_tokens) OVER () AS range_priced_tokens FROM measured), latest AS (SELECT provider, model AS last_model, session_id AS last_session_id, project_id AS last_project_id, occurred_at_micros AS last_seen_at_micros, ROW_NUMBER() OVER (PARTITION BY provider ORDER BY occurred_at_micros DESC, id DESC) AS recency FROM filtered) SELECT decorated.provider, model_count, event_count, session_count, turn_count, workflow_count, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, CASE WHEN cost_total_tokens > 0 AND priced_tokens = 0 THEN NULL ELSE priced_cost_micros END AS estimated_cost_micros, CASE WHEN unpriced_tokens = 0 OR cost_total_tokens = 0 THEN 0 ELSE MAX(1, CAST(ROUND(100.0 * unpriced_tokens / cost_total_tokens) AS INTEGER)) END AS unpriced_percent, total_tokens, range_total_tokens, CASE WHEN range_cost_total_tokens > 0 AND range_priced_tokens = 0 THEN NULL ELSE range_priced_cost_micros END AS range_estimated_cost_micros, CASE WHEN range_unpriced_tokens = 0 OR range_cost_total_tokens = 0 THEN 0 ELSE MAX(1, CAST(ROUND(100.0 * range_unpriced_tokens / range_cost_total_tokens) AS INTEGER)) END AS range_unpriced_percent, CASE WHEN range_total_tokens = 0 THEN 0 ELSE CAST(ROUND(100.0 * total_tokens / range_total_tokens) AS INTEGER) END AS range_share_percent, latest.last_model, latest.last_session_id, latest.last_project_id, latest.last_seen_at_micros FROM decorated JOIN latest ON latest.provider = decorated.provider AND latest.recency = 1 ORDER BY decorated.provider ASC LIMIT ? OFFSET ?"),
        parameters,
    }
}

fn daily_query(filter: &UsageFilter) -> UsageQuery {
    let limit = filter.limit.unwrap_or(31).clamp(1, MAX_USAGE_QUERY_ROWS);
    let offset = filter.offset.unwrap_or(0).min(100_000);
    let (where_clause, mut parameters) = daily_where_clause(filter);
    parameters.push(SqlValue::Integer(limit as i64));
    parameters.push(SqlValue::Integer(offset as i64));
    UsageQuery {
        sql: format!("SELECT day, provider, model, project_id, event_count, session_count, turn_count, workflow_count, input_tokens, output_tokens, estimated_cost_micros FROM usage_daily_provider_model WHERE {where_clause} ORDER BY day DESC, provider ASC, model ASC, project_id ASC LIMIT ? OFFSET ?"),
        parameters,
    }
}

fn daily_totals_query(filter: &UsageFilter) -> UsageQuery {
    let limit = filter.limit.unwrap_or(42).clamp(1, MAX_USAGE_QUERY_ROWS);
    let offset = filter.offset.unwrap_or(0).min(100_000);
    let (where_clause, mut parameters) = where_clause(filter);
    parameters.push(SqlValue::Integer(limit as i64));
    parameters.push(SqlValue::Integer(offset as i64));
    UsageQuery {
        sql: format!("WITH filtered AS (SELECT date(occurred_at_micros / 1000000, 'unixepoch') AS day, owned_id, turn_id, workflow_id, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_micros FROM usage_events WHERE {where_clause}), grouped AS (SELECT day, COUNT(*) AS event_count, COUNT(DISTINCT owned_id) AS session_count, COUNT(DISTINCT turn_id) AS turn_count, COUNT(DISTINCT workflow_id) AS workflow_count, COALESCE(SUM(input_tokens), 0) AS input_tokens, COALESCE(SUM(output_tokens), 0) AS output_tokens, COALESCE(SUM(cache_read_tokens), 0) AS cache_read_tokens, COALESCE(SUM(cache_write_tokens), 0) AS cache_write_tokens, COALESCE(SUM(reasoning_tokens), 0) AS reasoning_tokens, SUM(estimated_cost_micros) AS estimated_cost_micros FROM filtered GROUP BY day), measured AS (SELECT *, input_tokens + output_tokens + cache_read_tokens + cache_write_tokens AS total_tokens FROM grouped), decorated AS (SELECT *, MAX(total_tokens) OVER () AS range_max_tokens, FIRST_VALUE(day) OVER (ORDER BY total_tokens DESC, day DESC) AS best_day FROM measured) SELECT day, event_count, session_count, turn_count, workflow_count, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_micros, total_tokens, range_max_tokens, best_day FROM decorated ORDER BY day DESC LIMIT ? OFFSET ?"),
        parameters,
    }
}

fn provider_daily_totals_query(filter: &UsageFilter) -> UsageQuery {
    let limit = filter.limit.unwrap_or(84).clamp(1, MAX_USAGE_QUERY_ROWS);
    let offset = filter.offset.unwrap_or(0).min(100_000);
    let (where_clause, mut parameters) = where_clause(filter);
    parameters.push(SqlValue::Integer(limit as i64));
    parameters.push(SqlValue::Integer(offset as i64));
    UsageQuery {
        sql: format!("WITH filtered AS (SELECT date(occurred_at_micros / 1000000, 'unixepoch') AS day, provider, owned_id, turn_id, workflow_id, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_micros FROM usage_events WHERE {where_clause}), grouped AS (SELECT day, provider, COUNT(*) AS event_count, COUNT(DISTINCT owned_id) AS session_count, COUNT(DISTINCT turn_id) AS turn_count, COUNT(DISTINCT workflow_id) AS workflow_count, COALESCE(SUM(input_tokens), 0) AS input_tokens, COALESCE(SUM(output_tokens), 0) AS output_tokens, COALESCE(SUM(cache_read_tokens), 0) AS cache_read_tokens, COALESCE(SUM(cache_write_tokens), 0) AS cache_write_tokens, COALESCE(SUM(reasoning_tokens), 0) AS reasoning_tokens, SUM(estimated_cost_micros) AS estimated_cost_micros FROM filtered GROUP BY day, provider) SELECT day, provider, event_count, session_count, turn_count, workflow_count, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_micros, input_tokens + output_tokens + cache_read_tokens + cache_write_tokens AS total_tokens FROM grouped ORDER BY day DESC, provider ASC LIMIT ? OFFSET ?"),
        parameters,
    }
}

fn cost_inputs_query(filter: &UsageFilter) -> UsageQuery {
    let limit = filter
        .limit
        .unwrap_or(MAX_USAGE_QUERY_ROWS)
        .clamp(1, MAX_USAGE_QUERY_ROWS);
    let offset = filter.offset.unwrap_or(0).min(100_000);
    let (where_clause, mut parameters) = where_clause(filter);
    parameters.push(SqlValue::Integer(limit as i64));
    parameters.push(SqlValue::Integer(offset as i64));
    UsageQuery {
        sql: format!("SELECT provider, model, COALESCE(SUM(input_tokens), 0) AS input_tokens, COALESCE(SUM(output_tokens), 0) AS output_tokens, COALESCE(SUM(cache_read_tokens), 0) AS cache_read_tokens, COALESCE(SUM(cache_write_tokens), 0) AS cache_write_tokens, COALESCE(SUM(reasoning_tokens), 0) AS reasoning_tokens FROM usage_events WHERE {where_clause} GROUP BY provider, model ORDER BY provider ASC, model ASC LIMIT ? OFFSET ?"),
        parameters,
    }
}

fn where_clause(filter: &UsageFilter) -> (String, Vec<SqlValue>) {
    let mut parts = vec!["1 = 1".to_string()];
    let mut parameters = Vec::new();
    if let Some(provider) = &filter.provider {
        parts.push("provider = ?".to_string());
        parameters.push(SqlValue::Text(provider.clone()));
    }
    if let Some(model) = &filter.model {
        parts.push("model = ?".to_string());
        parameters.push(SqlValue::Text(model.clone()));
    }
    if let Some(project_id) = &filter.project_id {
        parts.push("project_id = ?".to_string());
        parameters.push(SqlValue::Text(project_id.clone()));
    }
    if let Some(workflow_id) = &filter.workflow_id {
        parts.push("workflow_id = ?".to_string());
        parameters.push(SqlValue::Text(workflow_id.clone()));
    }
    if let Some(start) = filter.start_micros {
        parts.push("occurred_at_micros >= ?".to_string());
        parameters.push(SqlValue::Integer(start));
    }
    if let Some(end) = filter.end_micros {
        parts.push("occurred_at_micros < ?".to_string());
        parameters.push(SqlValue::Integer(end));
    }
    (parts.join(" AND "), parameters)
}

fn daily_where_clause(filter: &UsageFilter) -> (String, Vec<SqlValue>) {
    let mut parts = vec!["1 = 1".to_string()];
    let mut parameters = Vec::new();
    if let Some(provider) = &filter.provider {
        parts.push("provider = ?".to_string());
        parameters.push(SqlValue::Text(provider.clone()));
    }
    if let Some(model) = &filter.model {
        parts.push("model = ?".to_string());
        parameters.push(SqlValue::Text(model.clone()));
    }
    if let Some(project_id) = &filter.project_id {
        parts.push("project_id = ?".to_string());
        parameters.push(SqlValue::Text(project_id.clone()));
    }
    if let Some(start) = filter.start_micros {
        parts.push("day >= date(? / 1000000, 'unixepoch')".to_string());
        parameters.push(SqlValue::Integer(start));
    }
    if let Some(end) = filter.end_micros {
        parts.push("day < date(? / 1000000, 'unixepoch')".to_string());
        parameters.push(SqlValue::Integer(end));
    }
    (parts.join(" AND "), parameters)
}

fn sqlite_integer(value: u64, description: &str) -> Result<i64, String> {
    i64::try_from(value).map_err(|_| format!("{description} is too large for SQLite"))
}

fn sqlite_value_to_json(value: ValueRef<'_>) -> Value {
    match value {
        ValueRef::Null => Value::Null,
        ValueRef::Integer(value) => Value::Number(value.into()),
        ValueRef::Real(value) => serde_json::Number::from_f64(value)
            .map(Value::Number)
            .unwrap_or(Value::Null),
        ValueRef::Text(value) => Value::String(String::from_utf8_lossy(value).into_owned()),
        ValueRef::Blob(value) => Value::Array(
            value
                .iter()
                .map(|byte| Value::Number(u64::from(*byte).into()))
                .collect(),
        ),
    }
}
pub fn cursor_lookup_key(
    provider: &str,
    provider_instance_id: &str,
    source_kind: &str,
    source_key: &str,
) -> String {
    format!("{provider}\u{1f}{provider_instance_id}\u{1f}{source_kind}\u{1f}{source_key}")
}
fn trace_sql(sql: &str) {
    if std::env::var("MCB_SQL_TRACE").as_deref() == Ok("1") {
        crate::debug_log::stderr_log!(
            "MCB_SQL_TRACE {}",
            redact_sql_literals(sql).replace('\n', " ")
        );
    }
}

fn redact_sql_literals(sql: &str) -> String {
    let mut redacted = String::with_capacity(sql.len());
    let mut in_literal = false;
    let mut chars = sql.chars().peekable();
    while let Some(character) = chars.next() {
        if character != '\'' {
            if !in_literal {
                redacted.push(character);
            }
            continue;
        }
        if !in_literal {
            in_literal = true;
            redacted.push_str("'?'");
        } else if chars.peek() == Some(&'\'') {
            let _ = chars.next();
        } else {
            in_literal = false;
        }
    }
    redacted
}
fn now_micros() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|value| value.as_micros().min(i64::MAX as u128) as i64)
        .unwrap_or_default()
}
fn value_u64(row: &Value, key: &str) -> u64 {
    row.get(key)
        .and_then(Value::as_u64)
        .or_else(|| {
            row.get(key)
                .and_then(Value::as_i64)
                .map(|value| value.max(0) as u64)
        })
        .unwrap_or_default()
}
fn value_i64(row: &Value, key: &str) -> Option<i64> {
    row.get(key).and_then(Value::as_i64)
}
fn value_string(row: &Value, key: &str) -> String {
    row.get(key)
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_string()
}
fn value_optional_string(row: &Value, key: &str) -> Option<String> {
    row.get(key)
        .and_then(Value::as_str)
        .map(ToString::to_string)
}
#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn test_db_path() -> PathBuf {
        std::env::temp_dir().join(format!(
            "mcb-usage-test-{}.sqlite3",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ))
    }

    fn event(id: &str) -> UsageEvent {
        UsageEvent {
            provider: "provider-a".into(),
            provider_instance_id: "instance-a".into(),
            owned_id: None,
            workflow_id: None,
            turn_id: None,
            project_id: Some("project-a".into()),
            workspace_id: None,
            occurred_at_micros: 1_700_000_000_000_000,
            input_tokens: 10,
            output_tokens: 4,
            cache_read_tokens: 0,
            cache_write_tokens: 0,
            reasoning_tokens: 0,
            model: "model-a".into(),
            estimated_cost_micros: None,
            estimate_rate_version: None,
            source_kind: "acp".into(),
            source_event_id: id.into(),
            source_key: "session-a".into(),
        }
    }

    #[test]
    fn rusqlite_failures_name_the_database_path() {
        let path = test_db_path();
        fs::write(&path, b"not a sqlite database").expect("invalid test database should write");
        let error = UsageDb::open(&path).unwrap_err();
        assert!(
            error.contains(path.to_string_lossy().as_ref()),
            "error must name the db path: {error}"
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn large_usage_batch_uses_prepared_transaction_without_argument_limits() {
        let path = test_db_path();
        let db = UsageDb::open(&path).expect("sqlite database should open");
        let events = (0..5_000)
            .map(|index| event(&format!("large-batch-event-{index}")))
            .collect::<Vec<_>>();
        let cursor = UsageSourceCursor {
            provider: "provider-a".into(),
            provider_instance_id: "instance-a".into(),
            source_kind: "acp".into(),
            source_key: "large-batch-source".into(),
            file_identity: "large-batch-file".into(),
            offset: 8_000_000,
            size: 8_000_000,
            modified_at_micros: 1_700_000_000_000_000,
            last_event_id: Some("large-batch-event-4999".into()),
        };

        db.insert_events_with_cursor(&events, Some(&cursor))
            .expect("large event batch should insert");

        let summary = db
            .read_usage_summary(&UsageFilter::default())
            .expect("large batch summary should query");
        assert_eq!(summary.event_count, 5_000);
        assert_eq!(summary.input_tokens, 50_000);
        let cursors = db
            .read_source_cursors()
            .expect("large batch cursor should query");
        assert_eq!(cursors.len(), 1);
        assert_eq!(cursors.values().next().unwrap(), &cursor);
        let _ = fs::remove_file(path);
    }

    #[test]
    fn rusqlite_query_paths_return_the_previous_json_equivalent_shapes() {
        let path = test_db_path();
        let db = UsageDb::open(&path).expect("sqlite database should open");
        let mut quoted = event("quoted-provider-event");
        quoted.provider = "provider-'quoted'".into();
        db.insert_events_with_cursor(&[quoted], None)
            .expect("quoted event should insert through bound parameters");
        let filter = UsageFilter {
            provider: Some("provider-'quoted'".into()),
            ..UsageFilter::default()
        };

        assert_eq!(db.read_usage_summary(&filter).unwrap().event_count, 1);
        assert_eq!(db.read_usage_breakdown(&filter).unwrap().len(), 1);
        assert_eq!(db.read_usage_provider_summary(&filter).unwrap().len(), 1);
        assert_eq!(db.read_daily(&filter).unwrap().len(), 1);
        assert_eq!(db.read_usage_daily_totals(&filter).unwrap().len(), 1);
        let _ = fs::remove_file(path);
    }

    #[test]
    fn reopening_an_existing_usage_database_preserves_history() {
        let path = test_db_path();
        {
            let db = UsageDb::open(&path).expect("new sqlite database should open");
            db.insert_events_with_cursor(&[event("existing-event")], None)
                .expect("existing event should insert");
        }

        let reopened = UsageDb::open(&path).expect("existing sqlite database should reopen");
        assert_eq!(
            reopened
                .read_usage_summary(&UsageFilter::default())
                .unwrap()
                .event_count,
            1
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn usage_history_deduplicates_events_and_rebuilds_rollups() {
        let path = test_db_path();
        let db = UsageDb::open(&path).expect("sqlite database should open");
        db.insert_events_with_cursor(&[event("event-1"), event("event-1")], None)
            .expect("events should insert");
        let summary = db
            .read_usage_summary(&UsageFilter::default())
            .expect("summary should query");
        assert_eq!(summary.event_count, 1);
        assert_eq!(db.read_daily(&UsageFilter::default()).unwrap().len(), 1);
        let _ = fs::remove_file(path);
    }

    #[test]
    fn provider_summary_counts_a_multi_model_session_once() {
        let path = test_db_path();
        let db = UsageDb::open(&path).expect("sqlite database should open");
        let mut first = event("event-model-a");
        first.owned_id = Some("session-a".into());
        first.model = "model-a".into();
        first.input_tokens = 10;
        first.output_tokens = 4;
        first.cache_read_tokens = 3;
        let mut second = event("event-model-b");
        second.owned_id = Some("session-a".into());
        second.model = "model-b".into();
        second.input_tokens = 20;
        second.output_tokens = 8;
        second.cache_write_tokens = 5;
        second.reasoning_tokens = 7;
        db.insert_events_with_cursor(&[first, second], None)
            .expect("events should insert");

        let breakdown = db
            .read_usage_breakdown(&UsageFilter::default())
            .expect("model breakdown should query");
        assert_eq!(
            breakdown.iter().map(|row| row.session_count).sum::<u64>(),
            2
        );
        let rows = db
            .read_usage_provider_summary(&UsageFilter::default())
            .expect("provider summary should query");
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].provider, "provider-a");
        assert_eq!(rows[0].event_count, 2);
        assert_eq!(rows[0].session_count, 1);
        assert_eq!(rows[0].input_tokens, 30);
        assert_eq!(rows[0].output_tokens, 12);
        assert_eq!(rows[0].cache_read_tokens, 3);
        assert_eq!(rows[0].cache_write_tokens, 5);
        assert_eq!(rows[0].reasoning_tokens, 7);
        assert_eq!(rows[0].total_tokens, 50);
        assert_eq!(rows[0].range_total_tokens, 50);
        assert_eq!(rows[0].model_count, 2);
        assert_eq!(rows[0].estimated_cost_micros, None);
        assert_eq!(rows[0].unpriced_percent, 100);
        assert_eq!(rows[0].range_estimated_cost_micros, None);
        assert_eq!(rows[0].range_unpriced_percent, 100);
        assert_eq!(rows[0].range_share_percent, 100);
        assert_eq!(rows[0].last_model, "model-b");
        assert_eq!(rows[0].last_session_id.as_deref(), Some("session-a"));
        assert_eq!(rows[0].last_project_id.as_deref(), Some("project-a"));
        assert_eq!(rows[0].last_seen_at_micros, Some(1_700_000_000_000_000));
        let _ = fs::remove_file(path);
    }

    #[test]
    fn daily_totals_groups_two_providers_on_one_day() {
        let path = test_db_path();
        let db = UsageDb::open(&path).expect("sqlite database should open");
        let mut first = event("daily-event-a");
        first.input_tokens = 10;
        first.output_tokens = 4;
        first.cache_read_tokens = 3;
        let mut second = event("daily-event-b");
        second.provider = "provider-b".into();
        second.provider_instance_id = "instance-b".into();
        second.model = "model-b".into();
        second.input_tokens = 20;
        second.output_tokens = 8;
        second.cache_write_tokens = 5;
        second.reasoning_tokens = 7;
        db.insert_events_with_cursor(&[first, second], None)
            .expect("events should insert");

        let rows = db
            .read_usage_daily_totals(&UsageFilter::default())
            .expect("daily totals should query");
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].day, "2023-11-14");
        assert_eq!(rows[0].event_count, 2);
        assert_eq!(rows[0].input_tokens, 30);
        assert_eq!(rows[0].output_tokens, 12);
        assert_eq!(rows[0].cache_read_tokens, 3);
        assert_eq!(rows[0].cache_write_tokens, 5);
        assert_eq!(rows[0].reasoning_tokens, 7);
        assert_eq!(rows[0].total_tokens, 50);
        assert_eq!(rows[0].range_max_tokens, 50);
        assert_eq!(rows[0].best_day, "2023-11-14");
        let _ = fs::remove_file(path);
    }

    #[test]
    fn token_breakdown_filters_the_range_and_separates_cached_codex_input() {
        let path = test_db_path();
        let db = UsageDb::open(&path).expect("sqlite database should open");
        let mut codex = event("token-mix-codex");
        codex.provider = "codex".into();
        codex.model = "gpt-5.6-sol".into();
        codex.input_tokens = 20; // uncached; the transcript reader takes the cached 80 out
        codex.output_tokens = 10;
        codex.cache_read_tokens = 80;
        codex.reasoning_tokens = 4;
        let mut claude = event("token-mix-claude");
        claude.provider = "claude".into();
        claude.model = "claude-opus-4-8".into();
        claude.input_tokens = 30;
        claude.output_tokens = 5;
        claude.cache_read_tokens = 10;
        let mut excluded = event("token-mix-excluded");
        excluded.occurred_at_micros = 1_600_000_000_000_000;
        excluded.input_tokens = 999;
        db.insert_events_with_cursor(&[codex, claude, excluded], None)
            .expect("events should insert");

        let breakdown = db
            .read_usage_token_breakdown(&UsageFilter {
                start_micros: Some(1_699_999_000_000_000),
                end_micros: Some(1_700_001_000_000_000),
                ..UsageFilter::default()
            })
            .expect("token breakdown should query");
        assert_eq!(breakdown.input_tokens, 50);
        assert_eq!(breakdown.output_tokens, 15);
        assert_eq!(breakdown.cache_read_tokens, 90);
        assert_eq!(breakdown.reasoning_tokens, 4);
        assert_eq!(breakdown.total_tokens, 155);
        let _ = fs::remove_file(path);
    }

    #[test]
    fn provider_daily_totals_group_each_provider_and_day_in_sql() {
        let path = test_db_path();
        let db = UsageDb::open(&path).expect("sqlite database should open");
        let mut codex = event("provider-day-codex");
        codex.provider = "codex".into();
        codex.model = "gpt-5.6-sol".into();
        codex.owned_id = Some("codex-session".into());
        codex.input_tokens = 20; // uncached; the transcript reader takes the cached 80 out
        codex.output_tokens = 10;
        codex.cache_read_tokens = 80;
        let mut other = event("provider-day-other");
        other.provider = "provider-b".into();
        other.owned_id = Some("other-session".into());
        other.input_tokens = 20;
        other.output_tokens = 5;
        db.insert_events_with_cursor(&[codex, other], None)
            .expect("events should insert");

        let rows = db
            .read_usage_provider_daily_totals(&UsageFilter::default())
            .expect("provider daily totals should query");
        assert_eq!(rows.len(), 2);
        assert_eq!(rows[0].day, "2023-11-14");
        let codex_row = rows.iter().find(|row| row.provider == "codex").unwrap();
        assert_eq!(codex_row.input_tokens, 20);
        assert_eq!(codex_row.cache_read_tokens, 80);
        assert_eq!(codex_row.total_tokens, 110);
        let _ = fs::remove_file(path);
    }

    #[test]
    fn cost_inputs_are_grouped_by_provider_and_model_in_sql() {
        let path = test_db_path();
        let db = UsageDb::open(&path).expect("sqlite database should open");
        let mut first = event("cost-input-a");
        first.provider = "codex".into();
        first.model = "gpt-5.6-sol".into();
        first.input_tokens = 20; // uncached, as stored
        first.cache_read_tokens = 80;
        let mut second = event("cost-input-b");
        second.provider = "codex".into();
        second.model = "gpt-5.6-sol".into();
        second.input_tokens = 30;
        second.cache_read_tokens = 20;
        db.insert_events_with_cursor(&[first, second], None)
            .expect("events should insert");

        let rows = db
            .read_usage_cost_inputs(&UsageFilter::default())
            .expect("cost inputs should query");
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].provider, "codex");
        assert_eq!(rows[0].model, "gpt-5.6-sol");
        assert_eq!(rows[0].input_tokens, 50);
        assert_eq!(rows[0].cache_read_tokens, 100);
        let _ = fs::remove_file(path);
    }

    #[test]
    fn usage_queries_are_db_side_and_bounded() {
        let query = breakdown_query(&UsageFilter {
            limit: Some(20),
            offset: Some(40),
            ..UsageFilter::default()
        });
        assert!(query.sql.contains("GROUP BY"));
        assert!(query.sql.contains("COUNT(*) OVER ()"));
        assert!(query.sql.contains("LIMIT ? OFFSET ?"));
        assert_eq!(
            query.parameters,
            [SqlValue::Integer(20), SqlValue::Integer(40)]
        );
    }

    #[test]
    fn provider_summary_query_groups_in_sql_and_is_bounded() {
        let query = provider_summary_query(&UsageFilter {
            limit: Some(20),
            offset: Some(40),
            ..UsageFilter::default()
        });
        assert!(query.sql.contains("GROUP BY provider"));
        assert!(query.sql.contains("COUNT(DISTINCT session_id)"));
        assert!(query.sql.contains("COUNT(DISTINCT model)"));
        assert!(query.sql.contains("provider_costs AS"));
        assert!(query.sql.contains("LEFT JOIN usage_rates rates"));
        assert!(query.sql.contains("range_estimated_cost_micros"));
        assert!(query.sql.contains("LIMIT ? OFFSET ?"));
        assert_eq!(
            query.parameters,
            [SqlValue::Integer(20), SqlValue::Integer(40)]
        );
    }

    #[test]
    fn daily_totals_query_groups_in_sql_and_is_bounded() {
        let query = daily_totals_query(&UsageFilter {
            limit: Some(20),
            offset: Some(40),
            ..UsageFilter::default()
        });
        assert!(query.sql.contains("GROUP BY day"));
        assert!(query.sql.contains("SUM(input_tokens)"));
        assert!(query.sql.contains("SUM(cache_read_tokens)"));
        assert!(query.sql.contains("MAX(total_tokens) OVER ()"));
        assert!(query.sql.contains("FIRST_VALUE(day)"));
        assert!(query.sql.contains("ORDER BY day DESC"));
        assert!(query.sql.contains("LIMIT ? OFFSET ?"));
        assert_eq!(
            query.parameters,
            [SqlValue::Integer(20), SqlValue::Integer(40)]
        );
    }

    #[test]
    fn every_usage_list_query_caps_requested_rows_at_two_hundred() {
        let filter = UsageFilter {
            limit: Some(usize::MAX),
            ..UsageFilter::default()
        };
        for query in [
            breakdown_query(&filter),
            provider_summary_query(&filter),
            daily_query(&filter),
            daily_totals_query(&filter),
            provider_daily_totals_query(&filter),
            cost_inputs_query(&filter),
        ] {
            assert!(query.sql.contains("LIMIT ? OFFSET ?"));
            assert_eq!(
                query.parameters.iter().rev().nth(1),
                Some(&SqlValue::Integer(MAX_USAGE_QUERY_ROWS as i64))
            );
        }
    }

    #[test]
    fn usage_breakdown_plan_uses_covering_provider_index() {
        let path = test_db_path();
        let db = UsageDb::open(&path).expect("sqlite database should open");
        let plan = db
            .explain_breakdown(&UsageFilter {
                provider: Some("provider-a".into()),
                ..UsageFilter::default()
            })
            .expect("sqlite should explain the bounded breakdown query");
        let plan_text = serde_json::to_string(&plan).expect("query plan should serialize");
        assert!(
            plan_text.contains("usage_events_provider_model_time_idx"),
            "query plan did not use the provider index: {plan_text}"
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn sql_trace_redacts_string_literals() {
        assert_eq!(
            redact_sql_literals("SELECT * FROM usage_events WHERE provider = 'private-provider'"),
            "SELECT * FROM usage_events WHERE provider = '?'"
        );
    }
}
