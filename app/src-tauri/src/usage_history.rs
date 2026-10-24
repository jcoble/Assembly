use crate::usage_db::{
    UsageBreakdownRow, UsageDailyRow, UsageDailyTotalsRow, UsageDb, UsageEvent, UsageFilter,
    UsageProviderSummaryRow, UsageSummary,
};
use crate::usage_indexer::UsageIndexer;
use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Manager};

pub mod otel;
pub mod otel_store;

#[derive(Debug, Clone, Default)]
pub struct UsageHistoryState;

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UsageHistoryQuery {
    pub provider: Option<String>,
    pub model: Option<String>,
    pub project_id: Option<String>,
    pub workflow_id: Option<String>,
    pub start_micros: Option<i64>,
    pub end_micros: Option<i64>,
    pub limit: Option<usize>,
    pub offset: Option<usize>,
}

impl From<UsageHistoryQuery> for UsageFilter {
    fn from(value: UsageHistoryQuery) -> Self {
        Self {
            provider: value.provider,
            model: value.model,
            project_id: value.project_id,
            workflow_id: value.workflow_id,
            start_micros: value.start_micros,
            end_micros: value.end_micros,
            limit: value.limit,
            offset: value.offset,
        }
    }
}

#[tauri::command]
pub async fn read_usage_summary(
    app: AppHandle,
    query: UsageHistoryQuery,
) -> Result<UsageSummary, String> {
    tauri::async_runtime::spawn_blocking(move || {
        UsageDb::open(usage_db_path(&app)?)?.read_usage_summary(&query.into())
    })
    .await
    .map_err(|error| format!("usage task failed: {error}"))?
}

#[tauri::command]
pub async fn read_usage_breakdown(
    app: AppHandle,
    query: UsageHistoryQuery,
) -> Result<Vec<UsageBreakdownRow>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        UsageDb::open(usage_db_path(&app)?)?.read_usage_breakdown(&query.into())
    })
    .await
    .map_err(|error| format!("usage task failed: {error}"))?
}

/// A run's usage per step attempt and model for its Cost tab, or every run's for the board.
#[tauri::command]
pub async fn read_workflow_usage(
    app: AppHandle,
    run_id: Option<String>,
) -> Result<Vec<crate::usage_db::acp::WorkflowUsageRow>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        UsageDb::open(usage_db_path(&app)?)?.read_workflow_usage(run_id.as_deref())
    })
    .await
    .map_err(|error| format!("usage task failed: {error}"))?
}

#[tauri::command]
pub async fn read_usage_provider_summary(
    app: AppHandle,
    query: UsageHistoryQuery,
) -> Result<Vec<UsageProviderSummaryRow>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        UsageDb::open(usage_db_path(&app)?)?.read_usage_provider_summary(&query.into())
    })
    .await
    .map_err(|error| format!("usage task failed: {error}"))?
}

#[tauri::command]
pub async fn read_usage_daily(
    app: AppHandle,
    query: UsageHistoryQuery,
) -> Result<Vec<UsageDailyRow>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        UsageDb::open(usage_db_path(&app)?)?.read_daily(&query.into())
    })
    .await
    .map_err(|error| format!("usage task failed: {error}"))?
}

#[tauri::command]
pub async fn read_usage_daily_totals(
    app: AppHandle,
    query: UsageHistoryQuery,
) -> Result<Vec<UsageDailyTotalsRow>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        UsageDb::open(usage_db_path(&app)?)?.read_usage_daily_totals(&query.into())
    })
    .await
    .map_err(|error| format!("usage task failed: {error}"))?
}

#[tauri::command]
pub async fn refresh_usage_history(app: AppHandle) -> Result<usize, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let path = usage_db_path(&app)?;
        UsageIndexer::new(path).ingest_local_sources()
    })
    .await
    .map_err(|error| format!("usage task failed: {error}"))?
}

/// Files one helper-model call in the same store the agents' own token counts
/// go to, so the Usage popover counts it without knowing the helper exists.
/// The vendor is the provider ("openai", "anthropic"), the job is the source
/// key, and the identifier is fresh every time so nothing is ever collapsed
/// into an earlier row.
///
/// The daily rollup table is left alone. Nothing reads it — every usage query
/// aggregates `usage_events` directly — and the indexer rebuilds it wholesale
/// on its next pass, so rebuilding it here would only put a full-table
/// aggregate in front of every title the app writes.
pub fn record_helper_call(
    app: &AppHandle,
    job: &str,
    vendor: &str,
    model: &str,
    input_tokens: u64,
    output_tokens: u64,
) -> Result<(), String> {
    let event = UsageEvent {
        provider: vendor.to_string(),
        provider_instance_id: "local".to_string(),
        owned_id: None,
        workflow_id: None,
        turn_id: None,
        project_id: None,
        workspace_id: None,
        occurred_at_micros: now_micros(),
        input_tokens,
        output_tokens,
        cache_read_tokens: 0,
        cache_write_tokens: 0,
        reasoning_tokens: 0,
        model: model.to_string(),
        estimated_cost_micros: None,
        estimate_rate_version: None,
        source_kind: "helper".to_string(),
        source_event_id: uuid::Uuid::new_v4().to_string(),
        source_key: format!("helper/{job}"),
    };
    UsageDb::open(usage_db_path(app)?)?.insert_events_with_cursor_deferred_rollup(&[event], None)
}

/// Rebuilds the rates table, then stores each finished turn's ACP usage in this
/// machine's usage database, attributed to the workflow step attempt that owns
/// the session, if any. The desktop and the remote backend both call it.
pub fn start_acp_usage(path: std::path::PathBuf, store: std::sync::Arc<mcb_core::session_store::SessionStore>) {
    if let Err(error) = UsageDb::open(&path).and_then(|db| db.rebuild_rates(crate::usage_db::acp::SEEDED_RATES)) {
        crate::debug_log::stderr_log!("Could not load usage rates: {error}");
    }
    // One writer, fed in the order turns finish: each turn's cost is measured from the
    // session's previous row, so the rows must be written in that order.
    let (sender, turns) = std::sync::mpsc::channel::<crate::usage_db::acp::AcpTurn>();
    std::thread::spawn(move || {
        for mut turn in turns {
            (turn.node_run_id, turn.workflow_id) = store.workflow_step_for_session(&turn.owned_id).unwrap_or_else(|error| {
                crate::debug_log::stderr_log!("Could not find the session's workflow step: {error}");
                (None, None)
            });
            if let Err(error) = UsageDb::open(&path).and_then(|db| db.insert_acp_turn(&turn)) {
                crate::debug_log::stderr_log!("Could not store turn usage: {error}");
            }
        }
    });
    crate::agent_conversation::manager::turn_usage::set_turn_usage_listener(std::sync::Arc::new(move |turn| {
        let _ = sender.send(turn);
    }));
}

fn now_micros() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|elapsed| elapsed.as_micros() as i64)
        .unwrap_or_default()
}

pub fn usage_db_path(app: &AppHandle) -> Result<std::path::PathBuf, String> {
    if let Some(path) = std::env::var_os("MAC_COMMAND_BAR_USAGE_DB") {
        return Ok(path.into());
    }
    Ok(app
        .path()
        .app_data_dir()
        .map_err(|error| format!("Application data directory is unavailable: {error}"))?
        .join("usage-history.sqlite3"))
}
