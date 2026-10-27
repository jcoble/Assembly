//! `TurnStats` is one run's numbers, as its `stats` row carries them: the line under a turn and its fold's duration.

use serde::Deserialize;

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TurnStats {
    pub run_id: String,
    /// running, finished, failed or aborted.
    pub status: String,
    pub started_at: Option<i64>,
    pub duration_ms: Option<i64>,
    pub first_token_ms: Option<i64>,
    pub model: Option<String>,
    pub input_tokens: Option<i64>,
    pub output_tokens: Option<i64>,
    pub cache_hit_pct: Option<f64>,
    pub cost_usd: Option<f64>,
    pub tool_calls: i64,
    pub branch: Option<String>,
    pub head: Option<String>,
    pub prev_branch: Option<String>,
    pub prev_head: Option<String>,
}
