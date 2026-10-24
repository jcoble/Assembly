use crate::{usage_db, usage_history};

#[tauri::command]
pub(crate) async fn read_usage_token_breakdown(
    app: tauri::AppHandle,
    query: usage_history::UsageHistoryQuery,
) -> Result<usage_db::UsageTokenBreakdown, String> {
    tauri::async_runtime::spawn_blocking(move || {
        usage_db::UsageDb::open(usage_history::usage_db_path(&app)?)?
            .read_usage_token_breakdown(&query.into())
    })
    .await
    .map_err(|error| format!("usage task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn read_usage_provider_daily_totals(
    app: tauri::AppHandle,
    query: usage_history::UsageHistoryQuery,
) -> Result<Vec<usage_db::UsageProviderDailyTotalsRow>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        usage_db::UsageDb::open(usage_history::usage_db_path(&app)?)?
            .read_usage_provider_daily_totals(&query.into())
    })
    .await
    .map_err(|error| format!("usage task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn read_usage_cost_inputs(
    app: tauri::AppHandle,
    query: usage_history::UsageHistoryQuery,
) -> Result<Vec<usage_db::UsageCostInputRow>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        usage_db::UsageDb::open(usage_history::usage_db_path(&app)?)?
            .read_usage_cost_inputs(&query.into())
    })
    .await
    .map_err(|error| format!("usage task failed: {error}"))?
}
