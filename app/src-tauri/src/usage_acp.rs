//! Usage rows from ACP. When a session's turn ends, the `session/prompt` result
//! says what the turn used, per model, and the last `usage_update` before it
//! carries the cost the provider reports. Rows are written already attributed to
//! the Assembly session and the workflow step attempt (`node_run_id`).
//!
//! Every input count is uncached input; cache reads and writes are their own
//! columns. Claude and Codex both report `inputTokens` that way over ACP.

use super::UsageDb;
use crate::agent_conversation::normalization::turn_usage::ModelTokens;
use rusqlite::{params, OptionalExtension, Transaction, TransactionBehavior};

/// Rows written from ACP turns. Their `source_key` is the native session id, so
/// the transcript reader can tell which sessions it must not count again.
pub const ACP_SOURCE_KIND: &str = "acp";

/// Model, then US dollars per million tokens: input, output, cache read, cache
/// write. A stand-in until the provider descriptors carry rates (TSK-1594);
/// `rebuild_rates` copies it into `usage_rates` at startup.
pub const SEEDED_RATES: &[(&str, f64, f64, f64, f64)] = &[
    ("gpt-6.1-sol", 2.0, 10.0, 0.1, 2.5),
    ("gpt-5.6-sol", 5.0, 30.0, 0.5, 6.25), ("codex", 5.0, 30.0, 0.5, 6.25),
    ("gpt-5.6-terra", 2.5, 15.0, 0.25, 3.125), ("gpt-5.6-luna", 1.0, 6.0, 0.1, 1.25),
    ("gpt-5.5", 5.0, 30.0, 0.5, 6.25), ("gpt-5.4", 2.5, 15.0, 0.25, 2.5),
    ("gpt-5.3-codex", 1.75, 14.0, 0.175, 1.75), ("gpt-5.2-codex", 1.75, 14.0, 0.175, 1.75),
    ("claude-opus-5-5", 4.0, 20.0, 0.2, 5.0),
    ("claude-sonnet-5-5", 2.0, 10.0, 0.1, 2.5),
    // Prompts up to 100,000 tokens; longer prompts cost five times as much.
    ("claude-haiku-5-5", 0.1, 0.5, 0.01, 0.125),
    ("claude-opus-5", 5.0, 25.0, 0.5, 6.25), ("claude-opus-4-8", 5.0, 25.0, 0.5, 6.25),
    ("claude-opus-4.8", 5.0, 25.0, 0.5, 6.25), ("claude-opus-4-7", 5.0, 25.0, 0.5, 6.25),
    ("claude-opus-4.7", 5.0, 25.0, 0.5, 6.25), ("claude-opus-4-6", 5.0, 25.0, 0.5, 6.25),
    ("claude-opus-4.6", 5.0, 25.0, 0.5, 6.25), ("claude-opus-4-5", 5.0, 25.0, 0.5, 6.25),
    ("claude-opus-4.5", 5.0, 25.0, 0.5, 6.25), ("claude-sonnet-5", 2.0, 10.0, 0.2, 2.5),
    ("claude-sonnet-4-6", 3.0, 15.0, 0.3, 3.75), ("claude-sonnet-4.6", 3.0, 15.0, 0.3, 3.75),
    ("claude-haiku-4-5", 1.0, 5.0, 0.1, 1.25), ("claude-haiku-4.5", 1.0, 5.0, 0.1, 1.25),
];

/// One finished ACP turn, as the agent manager hands it over.
#[derive(Debug, Clone, Default)]
pub struct AcpTurn {
    pub owned_id: String,
    pub native_session_id: Option<String>,
    pub provider: String,
    pub turn_id: String,
    /// What each model used in the turn.
    pub models: Vec<ModelTokens>,
    /// The latest `usage_update` cost: the session's running total in USD.
    pub cost_total_usd: Option<f64>,
    pub node_run_id: Option<String>,
    /// The workflow whose step the session runs, so per-workflow totals find the rows.
    pub workflow_id: Option<String>,
    /// The session was loaded or resumed rather than created, so the provider's running
    /// cost may already include earlier turns; its first ACP turn only sets the baseline.
    pub resumed: bool,
    /// When the turn's prompt went out. Transcript rows between then and when
    /// the turn's rows are written belong to this turn, and ACP counts them.
    pub occurred_at_micros: i64,
    /// The session's working folder; its last part is the project, as for transcript rows.
    pub cwd: Option<String>,
}

impl UsageDb {
    /// Copies the rates into `usage_rates`, replacing what was there.
    pub fn rebuild_rates(&self, rates: &[(&str, f64, f64, f64, f64)]) -> Result<(), String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|error| self.database_error("could not start rates rebuild", error))?;
        transaction.execute("DELETE FROM usage_rates", [])
            .map_err(|error| self.database_error("could not clear usage rates", error))?;
        for (model, input, output, cache_read, cache_write) in rates {
            transaction.execute("INSERT INTO usage_rates (model, input_rate, output_rate, cache_read_rate, cache_write_rate) VALUES (?1, ?2, ?3, ?4, ?5)",
                params![model, input, output, cache_read, cache_write])
                .map_err(|error| self.database_error("could not write usage rate", error))?;
        }
        transaction.commit().map_err(|error| self.database_error("could not finish rates rebuild", error))
    }

    /// Writes one row per model the turn used. The reported cost is a running
    /// total, so the turn's share is that total less the session's previous
    /// total; both go on the turn's largest row. A total below the previous one
    /// means the provider restarted its count, so the whole total is the share.
    /// The session's remaining transcript rows are attributed to it (the transcript
    /// reader's upsert leaves a stored row's owner as it is).
    /// Transcript rows for the same native session written during the turn are
    /// dropped, since ACP counts it; rows of any other turn stay.
    pub fn insert_acp_turn(&self, turn: &AcpTurn) -> Result<usize, String> {
        let tokens = &turn.models;
        let mut connection = self.connection()?;
        let transaction = connection.transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|error| self.database_error("could not start ACP usage write", error))?;
        if tokens.is_empty() {
            // A failed turn writes no rows (its transcript rows stay), but its cost is in the
            // provider's total, so the session's latest total moves on and the next turn is
            // measured from it.
            if let Some(total) = turn.cost_total_usd.map(|total| (total * 1_000_000.0).round() as i64) {
                transaction.execute("UPDATE usage_events SET reported_cost_total_micros = ?1 WHERE id = (SELECT id FROM usage_events WHERE owned_id = ?2 AND source_kind = ?3 AND reported_cost_total_micros IS NOT NULL ORDER BY id DESC LIMIT 1)",
                    params![total, turn.owned_id, ACP_SOURCE_KIND])
                    .map_err(|error| self.database_error("could not move the reported cost total", error))?;
                transaction.commit().map_err(|error| self.database_error("could not finish ACP usage write", error))?;
            }
            return Ok(0);
        }
        let source_key = turn.native_session_id.clone().unwrap_or_else(|| turn.owned_id.clone());
        let cost = turn.cost_total_usd.map(|total| (total * 1_000_000.0).round() as i64)
            .map(|total| -> rusqlite::Result<(Option<i64>, i64)> {
                let previous: Option<i64> = transaction.query_row(
                    "SELECT reported_cost_total_micros FROM usage_events WHERE owned_id = ?1 AND source_kind = ?2 AND reported_cost_total_micros IS NOT NULL ORDER BY id DESC LIMIT 1",
                    params![turn.owned_id, ACP_SOURCE_KIND], |row| row.get(0)).optional()?;
                // A loaded or resumed session's total includes turns from before this one,
                // so its first ACP turn only sets the baseline.
                let share = match previous {
                    Some(previous) => Some(if total >= previous { total - previous } else { total }),
                    None => (!turn.resumed).then_some(total),
                };
                Ok((share, total))
            }).transpose().map_err(|error| self.database_error("could not read reported cost", error))?;
        let largest = (0..tokens.len()).max_by_key(|&i| {
            let t = &tokens[i];
            t.input + t.output + t.cache_read + t.cache_write
        });
        let project_id = turn.cwd.as_deref().and_then(|cwd| std::path::Path::new(cwd).file_name()).map(|name| name.to_string_lossy().to_string());
        let written_at = super::now_micros();
        for (index, t) in tokens.iter().enumerate() {
            transaction.execute(
                "INSERT INTO usage_events (provider, provider_instance_id, owned_id, turn_id, project_id, workspace_id, node_run_id, workflow_id, occurred_at_micros, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, model, reported_cost_micros, reported_cost_total_micros, source_kind, source_event_id, source_key, inserted_at_micros) VALUES (?1, 'local', ?2, ?3, ?17, ?18, ?4, ?20, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?16, ?13, ?14, ?15, ?19) ON CONFLICT(provider, provider_instance_id, source_kind, source_event_id) DO NOTHING",
                params![turn.provider, turn.owned_id, turn.turn_id, turn.node_run_id, turn.occurred_at_micros,
                    t.input as i64, t.output as i64, t.cache_read as i64, t.cache_write as i64, t.reasoning as i64, t.model,
                    cost.filter(|_| Some(index) == largest).and_then(|(share, _)| share), ACP_SOURCE_KIND,
                    format!("{}:{}:{}", turn.owned_id, turn.turn_id, t.model), source_key,
                    cost.filter(|_| Some(index) == largest).map(|(_, total)| total), project_id, turn.cwd, written_at,
                    turn.workflow_id],
            ).map_err(|error| self.database_error("could not write ACP usage", error))?;
        }
        transaction.execute("DELETE FROM usage_events WHERE owned_id = ?1 AND source_kind <> ?2 AND occurred_at_micros BETWEEN ?3 AND ?4", params![source_key, ACP_SOURCE_KIND, turn.occurred_at_micros, written_at])
            .map_err(|error| self.database_error("could not drop transcript usage", error))?;
        // The rows that stay belong to this Assembly session too, so it counts as one session.
        transaction.execute("UPDATE usage_events SET owned_id = ?1 WHERE owned_id = ?2 AND source_kind <> ?3", params![turn.owned_id, source_key, ACP_SOURCE_KIND])
            .map_err(|error| self.database_error("could not attribute transcript usage", error))?;
        transaction.commit().map_err(|error| self.database_error("could not finish ACP usage write", error))?;
        Ok(tokens.len())
    }
}

/// One workflow step attempt's usage with one model, for a run's Cost tab.
#[derive(Debug, Clone, PartialEq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkflowUsageRow {
    /// `<run id>:<step id>:<attempt>`.
    pub node_run_id: String,
    pub model: String,
    pub input_tokens: i64,
    pub output_tokens: i64,
    pub cache_read_tokens: i64,
    pub cache_write_tokens: i64,
    /// What the provider reported; None when it reports no cost (Codex).
    pub reported_cost_micros: Option<i64>,
    /// Tokens times `usage_rates`; None when the model has no rate.
    pub estimated_cost_micros: Option<i64>,
}

impl UsageDb {
    /// Usage of every workflow step attempt per model, oldest first; only one run's when `run_id` is given.
    pub fn read_workflow_usage(&self, run_id: Option<&str>) -> Result<Vec<WorkflowUsageRow>, String> {
        let connection = self.connection()?;
        // A turn's reported cost sits on its largest model's row and covers all of the turn's models,
        // so only rows of turns without a reported cost are estimated; a step whose rows are all covered reads 0.
        let sql = format!("WITH events AS (SELECT e.*, EXISTS (SELECT 1 FROM usage_events c WHERE c.owned_id = e.owned_id AND c.turn_id = e.turn_id AND c.reported_cost_micros IS NOT NULL) AS covered FROM usage_events e WHERE e.node_run_id IS NOT NULL AND (?1 IS NULL OR e.node_run_id LIKE ?1 || ':%')), steps AS (SELECT node_run_id, model, {} AS rate_model, MIN(occurred_at_micros) AS first_at, SUM(input_tokens) AS input_tokens, SUM(output_tokens) AS output_tokens, SUM(cache_read_tokens) AS cache_read_tokens, SUM(cache_write_tokens) AS cache_write_tokens, SUM(reported_cost_micros) AS reported, MIN(covered) AS all_covered, SUM(CASE WHEN covered THEN 0 ELSE input_tokens END) AS est_input, SUM(CASE WHEN covered THEN 0 ELSE output_tokens END) AS est_output, SUM(CASE WHEN covered THEN 0 ELSE cache_read_tokens END) AS est_cache_read, SUM(CASE WHEN covered THEN 0 ELSE cache_write_tokens END) AS est_cache_write FROM events GROUP BY node_run_id, model) SELECT node_run_id, steps.model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reported, CASE WHEN all_covered THEN 0 ELSE CAST(ROUND(est_input * rates.input_rate + est_output * rates.output_rate + est_cache_read * rates.cache_read_rate + est_cache_write * rates.cache_write_rate) AS INTEGER) END FROM steps LEFT JOIN usage_rates rates ON rates.model = steps.rate_model ORDER BY first_at, node_run_id, steps.model", super::NORMALIZED_RATE_MODEL_SQL);
        let mut statement = connection.prepare(&sql).map_err(|error| self.database_error("could not read workflow usage", error))?;
        let rows = statement.query_map([run_id], |row| Ok(WorkflowUsageRow {
            node_run_id: row.get(0)?, model: row.get(1)?, input_tokens: row.get(2)?, output_tokens: row.get(3)?,
            cache_read_tokens: row.get(4)?, cache_write_tokens: row.get(5)?, reported_cost_micros: row.get(6)?, estimated_cost_micros: row.get(7)?,
        })).map_err(|error| self.database_error("could not read workflow usage", error))?;
        rows.collect::<rusqlite::Result<_>>().map_err(|error| self.database_error("could not read workflow usage", error))
    }
}

/// Brings a database made before ACP rows up to date: the new columns, and
/// Codex transcript rows, whose input used to include cached input, stored as
/// uncached input like every other row.
pub(super) fn upgrade(transaction: &Transaction) -> rusqlite::Result<()> {
    let missing = |column: &str| -> rusqlite::Result<bool> {
        transaction.query_row("SELECT COUNT(*) = 0 FROM pragma_table_info('usage_events') WHERE name = ?1", [column], |row| row.get(0))
    };
    let first_upgrade = missing("node_run_id")?;
    for (column, kind) in [("node_run_id", "TEXT"), ("reported_cost_micros", "INTEGER"), ("reported_cost_total_micros", "INTEGER")] {
        if missing(column)? {
            transaction.execute_batch(&format!("ALTER TABLE usage_events ADD COLUMN {column} {kind};"))?;
        }
    }
    if first_upgrade {
        transaction.execute(
            "UPDATE usage_events SET input_tokens = MAX(input_tokens - cache_read_tokens - cache_write_tokens, 0) WHERE source_kind = ?1",
            ["codex-jsonl"])?;
    }
    Ok(())
}


#[cfg(test)]
mod tests {
    use super::super::{UsageEvent, UsageFilter, USAGE_SCHEMA};
    use super::*;
    use crate::agent_conversation::normalization::turn_usage::turn_model_tokens;
    use serde_json::{json, Value};

    fn open() -> (std::path::PathBuf, UsageDb) {
        let path = std::env::temp_dir().join(format!("mcb-usage-acp-{}.sqlite3", uuid::Uuid::new_v4()));
        let db = UsageDb::open(&path).expect("usage database should open");
        (path, db)
    }

    fn model(name: &str, input: u64, cache_read: u64, cache_write: u64, output: u64) -> Value {
        json!({"model": name, "token_count": {"inputTokens": input, "cachedInputTokens": cache_read, "cachedWriteTokens": cache_write, "outputTokens": output, "reasoningOutputTokens": 0}})
    }

    fn turn(turn_id: &str, models: Vec<Value>, cost: Option<f64>) -> AcpTurn {
        AcpTurn {
            owned_id: "assembly-1".into(), native_session_id: Some("native-1".into()), provider: "claude".into(),
            turn_id: turn_id.into(), models: turn_model_tokens(&json!({"stopReason": "end_turn", "_meta": {"quota": {"model_usage": models}}}), None),
            cost_total_usd: cost, node_run_id: Some("run-1:build:1".into()), occurred_at_micros: 1_700_000_000_000_000,
            cwd: Some("/work/alpha".into()),
            ..AcpTurn::default()
        }
    }

    /// (turn, model, input, cache read, cache write, reported cost, node run) for every ACP row, in order.
    fn rows(db: &UsageDb) -> Vec<(String, String, i64, i64, i64, Option<i64>, Option<String>)> {
        let connection = db.connection().unwrap();
        let mut statement = connection.prepare("SELECT turn_id, model, input_tokens, cache_read_tokens, cache_write_tokens, reported_cost_micros, node_run_id FROM usage_events WHERE source_kind = 'acp' ORDER BY id").unwrap();
        statement.query_map([], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?, row.get(4)?, row.get(5)?, row.get(6)?)))
            .unwrap().collect::<rusqlite::Result<_>>().unwrap()
    }

    // Recorded from claude-agent-acp 0.94.0 (TSK-1595 check 4): the cost is the session's running
    // total, and it carries on after the adapter restarts and loads the session for turn 3.
    #[test]
    fn a_running_cost_total_becomes_each_turns_share() {
        let (path, db) = open();
        db.insert_acp_turn(&turn("turn-1", vec![model("claude-haiku-5-5", 1175, 0, 0, 19), model("claude-opus-5-5", 2, 10482, 10191, 5)], Some(0.0838594))).unwrap();
        db.insert_acp_turn(&turn("turn-2", vec![model("claude-opus-5-5", 2, 12823, 12793, 5)], Some(0.188876))).unwrap();
        db.insert_acp_turn(&turn("turn-3", vec![model("claude-opus-5-5", 2, 20690, 5863, 5)], Some(0.240026))).unwrap();
        let run = Some("run-1:build:1".to_string());
        assert_eq!(rows(&db), vec![
            ("turn-1".into(), "claude-haiku-5-5".into(), 1175, 0, 0, None, run.clone()),
            ("turn-1".into(), "claude-opus-5-5".into(), 2, 10482, 10191, Some(83_859), run.clone()),
            ("turn-2".into(), "claude-opus-5-5".into(), 2, 12823, 12793, Some(105_017), run.clone()),
            ("turn-3".into(), "claude-opus-5-5".into(), 2, 20690, 5863, Some(51_150), run),
        ]);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn after_the_provider_restarts_its_count_each_turn_is_measured_from_the_new_count() {
        let (path, db) = open();
        for (turn_id, total) in [("turn-1", 0.2), ("turn-2", 0.05), ("turn-3", 0.08)] {
            db.insert_acp_turn(&turn(turn_id, vec![model("claude-opus-5-5", 2, 10, 10, 5)], Some(total))).unwrap();
        }
        assert_eq!(rows(&db).iter().map(|row| row.5).collect::<Vec<_>>(), [Some(200_000), Some(50_000), Some(30_000)]);
        let _ = std::fs::remove_file(path);
    }

    // A turn that fails still reports the provider's total; it writes no rows (its transcript rows
    // stay), but the next turn is measured from it.
    #[test]
    fn a_failed_turn_moves_the_cost_baseline_without_writing_rows() {
        let (path, db) = open();
        db.insert_acp_turn(&turn("turn-1", vec![model("claude-opus-5-5", 2, 10, 10, 5)], Some(0.10))).unwrap();
        assert_eq!(db.insert_acp_turn(&turn("turn-2", vec![], Some(0.15))).unwrap(), 0);
        db.insert_acp_turn(&turn("turn-3", vec![model("claude-opus-5-5", 2, 10, 10, 5)], Some(0.18))).unwrap();
        assert_eq!(rows(&db).iter().map(|row| row.5).collect::<Vec<_>>(), [Some(100_000), Some(30_000)]);
        let _ = std::fs::remove_file(path);
    }

    // Recorded from codex-acp 2.2.2: per-turn counts, its own name for cache writes, no cost.
    #[test]
    fn per_turn_usage_without_a_cost_is_stored_unpriced() {
        let (path, db) = open();
        let mut codex = turn("turn-1", vec![], None);
        codex.provider = "codex".into();
        codex.models = turn_model_tokens(&json!({"stopReason": "end_turn", "usage": {"totalTokens": 16045, "inputTokens": 3752, "cachedReadTokens": 12288, "cachedWriteTokens": 0, "outputTokens": 5, "thoughtTokens": 0},
            "_meta": {"quota": {"model_usage": [{"model": "gpt-6.1-sol", "token_count": {"totalTokens": 16045, "inputTokens": 3752, "cachedInputTokens": 12288, "cacheWriteInputTokens": 7, "outputTokens": 5, "reasoningOutputTokens": 0}}]}}}), None);
        assert_eq!(db.insert_acp_turn(&codex).unwrap(), 1);
        assert_eq!(rows(&db), vec![("turn-1".into(), "gpt-6.1-sol".into(), 3752, 12288, 7, None, Some("run-1:build:1".into()))]);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn standard_usage_is_used_when_there_is_no_breakdown_and_no_usage_writes_nothing() {
        let (path, db) = open();
        let mut plain = turn("turn-1", vec![], None);
        plain.models = turn_model_tokens(&json!({"stopReason": "end_turn", "usage": {"inputTokens": 9, "outputTokens": 3, "cachedReadTokens": 4}}), Some("some-model"));
        assert_eq!(db.insert_acp_turn(&plain).unwrap(), 1);
        // agy-acp 1.4.1 answers a prompt with the stop reason alone.
        plain.turn_id = "turn-2".into();
        plain.models = turn_model_tokens(&json!({"stopReason": "end_turn"}), Some("some-model"));
        assert_eq!(db.insert_acp_turn(&plain).unwrap(), 0);
        assert_eq!(rows(&db), vec![("turn-1".into(), "some-model".into(), 9, 4, 0, None, Some("run-1:build:1".into()))]);
        let _ = std::fs::remove_file(path);
    }

    fn transcript_row(id: &str, owned_id: &str, occurred_at_micros: i64) -> UsageEvent {
        UsageEvent {
            provider: "claude".into(), provider_instance_id: "local".into(), owned_id: Some(owned_id.into()),
            workflow_id: None, turn_id: None, project_id: None, workspace_id: None, occurred_at_micros,
            input_tokens: 5, output_tokens: 5, cache_read_tokens: 0, cache_write_tokens: 0, reasoning_tokens: 0,
            model: "claude-opus-5-5".into(), estimated_cost_micros: None, estimate_rate_version: None,
            source_kind: "claude-jsonl".into(), source_event_id: id.into(), source_key: "transcript-file".into(),
        }
    }

    // The turn starts at 1_700_000_000_000_000 (see `turn`); its transcript lines come after that.
    #[test]
    fn only_transcript_rows_written_during_an_acp_turn_are_replaced() {
        let (path, db) = open();
        let start = 1_700_000_000_000_000;
        // A later turn with no ACP row (one that ended in an error) keeps its transcript usage.
        let later = super::super::now_micros() + 3_600_000_000;
        db.insert_events_with_cursor(&[transcript_row("old-1", "native-1", start - 2_000_000), transcript_row("old-2", "native-1", start - 1_000_000),
            transcript_row("other", "native-2", start + 1), transcript_row("this-turn-early", "native-1", start + 1)], None).unwrap();
        db.insert_acp_turn(&turn("turn-1", vec![model("claude-opus-5-5", 2, 10, 10, 5)], None)).unwrap();
        db.insert_events_with_cursor(&[transcript_row("this-turn-late", "native-1", start + 2), transcript_row("old-3", "native-1", start - 500_000),
            transcript_row("later-turn", "native-1", later)], None).unwrap();
        let connection = db.connection().unwrap();
        let mut statement = connection.prepare("SELECT source_event_id FROM usage_events WHERE source_kind = 'claude-jsonl' ORDER BY id").unwrap();
        let kept: Vec<String> = statement.query_map([], |row| row.get(0)).unwrap().collect::<rusqlite::Result<_>>().unwrap();
        assert_eq!(kept, ["old-1", "old-2", "other", "old-3", "later-turn"]);
        // Five transcript rows (5 in, 5 out each), plus the ACP turn once, under its project.
        let summary = db.read_usage_summary(&UsageFilter { project_id: Some("alpha".into()), ..UsageFilter::default() }).unwrap();
        assert_eq!((summary.input_tokens, summary.output_tokens, summary.cache_read_tokens), (2, 5, 10));
        let summary = db.read_usage_summary(&UsageFilter::default()).unwrap();
        assert_eq!((summary.input_tokens, summary.output_tokens, summary.cache_read_tokens), (5 * 5 + 2, 5 * 5 + 5, 10));
        let _ = std::fs::remove_file(path);
    }

    // Claude writes a sub-agent's calls to their own transcript file under the parent session id.
    // The turn's `model_usage` already counts them (Claude's result `modelUsage` is the main
    // thread's usage plus the sub-agent's), so they are replaced like the main transcript's rows.
    #[test]
    fn sub_agent_transcript_rows_of_an_acp_turn_are_replaced_because_its_usage_counts_them() {
        let (path, db) = open();
        let start = 1_700_000_000_000_000;
        let mut sub_agent = transcript_row("sub-agent", "native-1", start + 1);
        sub_agent.source_key = "sub-agent-file".into();
        db.insert_events_with_cursor(&[transcript_row("main", "native-1", start + 1), sub_agent], None).unwrap();
        db.insert_acp_turn(&turn("turn-1", vec![model("claude-opus-5-5", 2, 10, 10, 5)], None)).unwrap();
        let left: i64 = db.connection().unwrap().query_row("SELECT COUNT(*) FROM usage_events WHERE source_kind = 'claude-jsonl'", [], |row| row.get(0)).unwrap();
        assert_eq!(left, 0);
        let _ = std::fs::remove_file(path);
    }

    // A chat started before ACP rows: Claude's running total already includes its earlier turns.
    #[test]
    fn an_older_chat_stays_one_session_and_its_first_acp_turn_only_sets_the_cost_baseline() {
        let (path, db) = open();
        db.insert_events_with_cursor(&[transcript_row("old", "native-1", 1_600_000_000_000_000)], None).unwrap();
        let mut first = turn("turn-1", vec![model("claude-opus-5-5", 2, 10, 10, 5)], Some(0.55));
        first.resumed = true;
        db.insert_acp_turn(&first).unwrap();
        let mut next = turn("turn-2", vec![model("claude-opus-5-5", 2, 10, 10, 5)], Some(0.60));
        next.occurred_at_micros += 1;
        db.insert_acp_turn(&next).unwrap();
        assert_eq!(rows(&db).iter().map(|row| row.5).collect::<Vec<_>>(), [None, Some(50_000)]);
        assert_eq!(db.read_usage_summary(&UsageFilter::default()).unwrap().session_count, 1);
        let _ = std::fs::remove_file(path);
    }

    // The provider's running total of a loaded or resumed session includes turns from before
    // Assembly saw it, whether or not the transcript indexer has read them yet.
    #[test]
    fn a_resumed_sessions_first_turn_only_sets_the_cost_baseline_and_a_new_ones_takes_the_total() {
        let (path, db) = open();
        let mut resumed = turn("turn-1", vec![model("claude-opus-5-5", 2, 10, 10, 5)], Some(0.55));
        resumed.resumed = true;
        db.insert_acp_turn(&resumed).unwrap();
        let mut fresh = turn("turn-1", vec![model("claude-opus-5-5", 2, 10, 10, 5)], Some(0.05));
        (fresh.owned_id, fresh.native_session_id) = ("assembly-2".into(), Some("native-2".into()));
        db.insert_acp_turn(&fresh).unwrap();
        assert_eq!(rows(&db).iter().map(|row| row.5).collect::<Vec<_>>(), [None, Some(50_000)]);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn rows_of_a_workflow_step_carry_its_workflow() {
        let (path, db) = open();
        let mut step = turn("turn-1", vec![model("claude-opus-5-5", 2, 10, 10, 5)], None);
        step.workflow_id = Some("spec-to-pr".into());
        db.insert_acp_turn(&step).unwrap();
        let found: i64 = db.connection().unwrap().query_row("SELECT COUNT(*) FROM usage_events WHERE workflow_id = ?1 AND node_run_id = ?2", ["spec-to-pr", "run-1:build:1"], |row| row.get(0)).unwrap();
        assert_eq!(found, 1);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn reading_a_transcript_again_keeps_the_assembly_session_as_owner() {
        let (path, db) = open();
        db.insert_events_with_cursor(&[transcript_row("old", "native-1", 1_600_000_000_000_000)], None).unwrap();
        db.insert_acp_turn(&turn("turn-1", vec![model("claude-opus-5-5", 2, 10, 10, 5)], None)).unwrap();
        db.insert_events_with_cursor(&[transcript_row("old", "native-1", 1_600_000_000_000_000)], None).unwrap();
        let owner: String = db.connection().unwrap().query_row("SELECT owned_id FROM usage_events WHERE source_event_id = 'old'", [], |row| row.get(0)).unwrap();
        assert_eq!(owner, "assembly-1");
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn the_models_the_agents_send_are_priced() {
        let (path, db) = open();
        db.rebuild_rates(SEEDED_RATES).unwrap();
        let events: Vec<UsageEvent> = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "gpt-6.1-sol"].iter().enumerate().map(|(index, name)| {
            let mut event = transcript_row(name, "native-9", index as i64 + 1);
            event.model = name.to_string();
            event
        }).collect();
        db.insert_events_with_cursor(&events, None).unwrap();
        let rows = db.read_usage_provider_summary(&UsageFilter::default()).unwrap();
        assert_eq!((rows[0].unpriced_percent, rows[0].estimated_cost_micros), (0, Some(243)));
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn workflow_usage_is_one_row_per_step_attempt_and_model_with_reported_and_estimated_cost() {
        let (path, db) = open();
        db.rebuild_rates(SEEDED_RATES).unwrap();
        let mut build = turn("t1", vec![model("claude-opus-5-5", 100, 10, 0, 20)], Some(0.5));
        build.node_run_id = Some("run-1:build:1".into());
        db.insert_acp_turn(&build).unwrap();
        let mut review = turn("t2", vec![model("gpt-6.1-sol", 1000, 0, 0, 100)], None);
        (review.owned_id, review.node_run_id, review.occurred_at_micros) = ("agent-2".into(), Some("run-1:code-review:1".into()), build.occurred_at_micros + 1);
        db.insert_acp_turn(&review).unwrap();
        let mut other = turn("t3", vec![model("gpt-6.1-sol", 5, 0, 0, 5)], None);
        (other.owned_id, other.node_run_id) = ("agent-3".into(), Some("run-10:build:1".into()));
        db.insert_acp_turn(&other).unwrap();
        let rows = db.read_workflow_usage(Some("run-1")).unwrap();
        assert_eq!(rows.iter().map(|row| row.node_run_id.as_str()).collect::<Vec<_>>(), ["run-1:build:1", "run-1:code-review:1"]);
        assert_eq!((rows[0].input_tokens, rows[0].output_tokens, rows[0].cache_read_tokens), (100, 20, 10));
        assert_eq!(rows[0].reported_cost_micros, Some(500_000));
        // 1000 in at $2 and 100 out at $10 per million tokens.
        assert_eq!((rows[1].reported_cost_micros, rows[1].estimated_cost_micros), (None, Some(3000)));
        assert_eq!(db.read_workflow_usage(None).unwrap().len(), 3);
        // A turn's reported cost covers its smaller models too, so they get no estimate on top.
        let mut two = turn("t4", vec![model("claude-opus-5-5", 900, 0, 0, 90), model("gpt-6.1-sol", 50, 0, 0, 5)], Some(0.3));
        (two.owned_id, two.node_run_id) = ("agent-4".into(), Some("run-2:build:1".into()));
        db.insert_acp_turn(&two).unwrap();
        let rows = db.read_workflow_usage(Some("run-2")).unwrap();
        assert_eq!(rows.iter().map(|row| (row.reported_cost_micros, row.estimated_cost_micros)).collect::<Vec<_>>(), [(Some(300_000), Some(0)), (None, Some(0))]);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn estimated_cost_joins_the_rates_table() {
        let (path, db) = open();
        db.rebuild_rates(SEEDED_RATES).unwrap();
        let mut priced = transcript_row("priced", "native-9", 1);
        (priced.model, priced.input_tokens, priced.output_tokens) = ("gpt-5.6-sol".into(), 10, 4);
        db.insert_events_with_cursor(&[priced], None).unwrap();
        let rows = db.read_usage_provider_summary(&UsageFilter::default()).unwrap();
        assert_eq!((rows[0].estimated_cost_micros, rows[0].unpriced_percent), (Some(170), 0));
        db.rebuild_rates(&[]).unwrap();
        let rows = db.read_usage_provider_summary(&UsageFilter::default()).unwrap();
        assert_eq!((rows[0].estimated_cost_micros, rows[0].unpriced_percent), (None, 100));
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn an_older_database_gets_the_columns_and_uncached_codex_input() {
        let path = std::env::temp_dir().join(format!("mcb-usage-acp-{}.sqlite3", uuid::Uuid::new_v4()));
        let connection = rusqlite::Connection::open(&path).unwrap();
        connection.execute_batch(&USAGE_SCHEMA.replace("    node_run_id TEXT,\n    reported_cost_micros INTEGER,\n    -- The provider's running cost total the share was taken from.\n    reported_cost_total_micros INTEGER,\n", "")).unwrap();
        connection.execute("INSERT INTO usage_events (provider, provider_instance_id, occurred_at_micros, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, model, source_kind, source_event_id, source_key, inserted_at_micros) VALUES ('codex', 'local', 1, 100, 1, 80, 5, 0, 'gpt-5.6-sol', 'codex-jsonl', 'e', 'k', 1)", []).unwrap();
        drop(connection);
        let db = UsageDb::open(&path).expect("an older database should upgrade");
        UsageDb::open(&path).expect("an upgraded database should open again");
        let input: i64 = db.connection().unwrap().query_row("SELECT input_tokens FROM usage_events", [], |row| row.get(0)).unwrap();
        assert_eq!(input, 15);
        db.insert_acp_turn(&turn("turn-1", vec![model("claude-opus-5-5", 2, 10, 10, 5)], Some(0.1))).unwrap();
        let _ = std::fs::remove_file(path);
    }
}
