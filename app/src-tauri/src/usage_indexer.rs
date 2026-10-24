use crate::usage_db::{cursor_lookup_key, UsageDb, UsageSourceCursor};
use crate::usage_sources::{
    discover_local_usage_sources, opaque_source_key, parse_usage_events_for_source_with_context,
    read_incremental_jsonl, read_source_context,
};
#[cfg(test)]
use std::path::Path;

#[derive(Debug, Clone)]
pub struct UsageIndexer {
    pub database_path: std::path::PathBuf,
}

impl UsageIndexer {
    pub fn new(database_path: impl Into<std::path::PathBuf>) -> Self {
        Self {
            database_path: database_path.into(),
        }
    }

    #[cfg(test)]
    pub fn ingest_jsonl(
        &self,
        path: &Path,
        cursor: Option<&UsageSourceCursor>,
        provider: &str,
        provider_instance_id: &str,
        source_kind: &str,
        source_key: &str,
    ) -> Result<usize, String> {
        let db = UsageDb::open(&self.database_path)?;
        let rebuild = db.start_claude_history_rebuild()? && source_kind == "claude-jsonl";
        let count = ingest_source(
            &db,
            path,
            cursor,
            provider,
            provider_instance_id,
            source_kind,
            source_key,
            rebuild,
        )?;
        if rebuild {
            db.finish_claude_history_rebuild()?;
        }
        db.rebuild_daily_rollups()?;
        Ok(count.unwrap_or_default())
    }

    /// Ingest all locally available provider transcripts. Discovery and
    /// cursor lookup happen once; each source tail is committed atomically so
    /// a very large transcript cannot turn into an oversized SQLite command.
    pub fn ingest_local_sources(&self) -> Result<usize, String> {
        let db = UsageDb::open(&self.database_path)?;
        let rebuild_claude = db.start_claude_history_rebuild()?;
        let cursors = db.read_source_cursors()?;
        let mut count = 0_usize;
        let mut rollups_dirty = rebuild_claude;
        let ingest_result = (|| -> Result<(), String> {
            for source in discover_local_usage_sources() {
                let source_key = source.path.to_string_lossy().to_string();
                let lookup = cursor_lookup_key(
                    &source.provider,
                    &source.provider_instance_id,
                    &source.source_kind,
                    &opaque_source_key(&source_key),
                );
                let Some(events) = ingest_source(
                    &db,
                    &source.path,
                    cursors.get(&lookup),
                    &source.provider,
                    &source.provider_instance_id,
                    &source.source_kind,
                    &source_key,
                    rebuild_claude && source.source_kind == "claude-jsonl",
                )?
                else {
                    continue;
                };
                if events > 0 {
                    count = count.saturating_add(events);
                    rollups_dirty = true;
                }
            }
            if rebuild_claude {
                db.finish_claude_history_rebuild()?;
            }
            Ok(())
        })();
        let rollup_result = if rollups_dirty {
            db.rebuild_daily_rollups()
        } else {
            Ok(())
        };
        if let Err(error) = ingest_result {
            if let Err(rollup_error) = rollup_result {
                return Err(format!("{error}; {rollup_error}"));
            }
            return Err(error);
        }
        rollup_result?;
        Ok(count)
    }
}

/// Reads one source's new lines and stores their events with its cursor;
/// `None` means nothing changed. During the Claude history rebuild the
/// transcript is read from the start, and its old rows are deleted only once
/// the replacement events are in hand, so an unreadable file keeps its history.
#[allow(clippy::too_many_arguments)]
fn ingest_source(
    db: &UsageDb,
    path: &std::path::Path,
    cursor: Option<&UsageSourceCursor>,
    provider: &str,
    provider_instance_id: &str,
    source_kind: &str,
    source_key: &str,
    rebuild: bool,
) -> Result<Option<usize>, String> {
    let cursor = cursor.filter(|_| !rebuild);
    let source = read_incremental_jsonl(
        path,
        cursor,
        provider,
        provider_instance_id,
        source_kind,
        source_key,
    )?;
    let context = read_source_context(path, provider);
    let events = parse_usage_events_for_source_with_context(
        &source.lines,
        provider,
        provider_instance_id,
        source_kind,
        source_key,
        &context,
    );
    if rebuild {
        db.delete_claude_source_events(&source.next_cursor.source_key)?;
    } else if events.is_empty() && cursor == Some(&source.next_cursor) {
        return Ok(None);
    }
    db.insert_events_with_cursor_deferred_rollup(&events, Some(&source.next_cursor))?;
    Ok(Some(events.len()))
}

/// Claude rows used to be keyed by transcript line, so a message written over
/// several lines was counted once per line. Until one indexing pass has
/// re-read every Claude transcript still on disk under the message key
/// (`user_version` 1), each one replaces its old rows. Rows whose transcript
/// is gone cannot be re-read and are kept.
impl UsageDb {
    /// Reports whether the rebuild is still pending. While it is, a
    /// temporary index on `source_key` keeps each transcript's delete from
    /// scanning the whole table.
    fn start_claude_history_rebuild(&self) -> Result<bool, String> {
        let connection = self.connection()?;
        let version = connection
            .query_row("PRAGMA user_version", [], |row| row.get::<_, i64>(0))
            .map_err(|error| self.database_error("could not read usage schema version", error))?;
        if version >= 1 {
            return Ok(false);
        }
        connection
            .execute_batch("CREATE INDEX IF NOT EXISTS usage_events_rebuild_source_idx ON usage_events (source_key);")
            .map_err(|error| self.database_error("could not start Claude history rebuild", error))?;
        Ok(true)
    }

    fn delete_claude_source_events(&self, source_key: &str) -> Result<(), String> {
        self.connection()?
            .execute(
                "DELETE FROM usage_events WHERE source_kind = ?1 AND source_key = ?2",
                ["claude-jsonl", source_key],
            )
            .map(|_| ())
            .map_err(|error| self.database_error("could not clear Claude usage history", error))
    }

    fn finish_claude_history_rebuild(&self) -> Result<(), String> {
        self.connection()?
            .execute_batch(
                "DROP INDEX IF EXISTS usage_events_rebuild_source_idx;\nPRAGMA user_version = 1;",
            )
            .map_err(|error| self.database_error("could not finish Claude history rebuild", error))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::io::Write;
    use std::time::{SystemTime, UNIX_EPOCH};

    #[test]
    fn fixture_jsonl_is_indexed_incrementally_with_non_zero_totals() {
        let root = std::env::temp_dir().join(format!(
            "mcb-usage-indexer-{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&root).unwrap();
        let source_path = root.join("session.jsonl");
        let database_path = root.join("usage.sqlite3");
        let mut source = fs::File::create(&source_path).unwrap();
        writeln!(source, r#"{{"type":"assistant","uuid":"assistant-1","sessionId":"session-1","timestamp":"2026-08-08T12:34:56Z","cwd":"/tmp/project/workspace","message":{{"model":"model-a","usage":{{"input_tokens":12,"output_tokens":7}}}}}}"#).unwrap();
        let indexer = UsageIndexer::new(&database_path);
        let first = indexer
            .ingest_jsonl(
                &source_path,
                None,
                "claude",
                "local",
                "claude-jsonl",
                &source_path.to_string_lossy(),
            )
            .unwrap();
        assert_eq!(first, 1);
        let db = UsageDb::open(&database_path).unwrap();
        let cursor = db
            .read_source_cursors()
            .unwrap()
            .into_values()
            .next()
            .unwrap();
        let second = indexer
            .ingest_jsonl(
                &source_path,
                Some(&cursor),
                "claude",
                "local",
                "claude-jsonl",
                &source_path.to_string_lossy(),
            )
            .unwrap();
        assert_eq!(second, 0);
        let summary = db.read_usage_summary(&Default::default()).unwrap();
        assert_eq!(summary.event_count, 1);
        assert_eq!(summary.input_tokens, 12);
        assert_eq!(summary.output_tokens, 7);
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn codex_incremental_tail_retains_session_context_and_unique_events() {
        let root = std::env::temp_dir().join(format!(
            "mcb-usage-codex-indexer-{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&root).unwrap();
        let source_path = root.join("rollout.jsonl");
        let database_path = root.join("usage.sqlite3");
        let mut source = fs::File::create(&source_path).unwrap();
        writeln!(source, r#"{{"type":"session_meta","payload":{{"id":"session-2","cwd":"/tmp/project/workspace"}}}}"#).unwrap();
        writeln!(source, r#"{{"type":"event_msg","timestamp":"2026-08-08T12:34:56Z","payload":{{"type":"token_count","info":{{"last_token_usage":{{"input_tokens":20,"output_tokens":9}}}}}}}}"#).unwrap();
        source.flush().unwrap();

        let indexer = UsageIndexer::new(&database_path);
        assert_eq!(
            indexer
                .ingest_jsonl(
                    &source_path,
                    None,
                    "codex",
                    "local",
                    "codex-jsonl",
                    &source_path.to_string_lossy(),
                )
                .unwrap(),
            1
        );
        let db = UsageDb::open(&database_path).unwrap();
        let cursor = db
            .read_source_cursors()
            .unwrap()
            .into_values()
            .next()
            .unwrap();
        writeln!(source, r#"{{"type":"event_msg","timestamp":"2026-08-08T12:35:56Z","payload":{{"type":"token_count","info":{{"last_token_usage":{{"input_tokens":21,"output_tokens":10}}}}}}}}"#).unwrap();
        source.flush().unwrap();

        assert_eq!(
            indexer
                .ingest_jsonl(
                    &source_path,
                    Some(&cursor),
                    "codex",
                    "local",
                    "codex-jsonl",
                    &source_path.to_string_lossy(),
                )
                .unwrap(),
            1
        );
        let summary = db.read_usage_summary(&Default::default()).unwrap();
        assert_eq!(summary.event_count, 2);
        assert_eq!(summary.session_count, 1);
        assert_eq!(summary.input_tokens, 41);
        assert_eq!(summary.output_tokens, 19);
        let _ = fs::remove_dir_all(root);
    }

    fn claude_test_root(name: &str) -> std::path::PathBuf {
        let root = std::env::temp_dir().join(format!(
            "mcb-usage-{name}-{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&root).unwrap();
        root
    }

    fn claude_line(uuid: &str, message_id: &str, output_tokens: u64) -> String {
        format!(
            r#"{{"type":"assistant","uuid":"{uuid}","sessionId":"session-1","timestamp":"2026-08-08T12:34:56Z","message":{{"id":"{message_id}","model":"model-a","usage":{{"input_tokens":2,"output_tokens":{output_tokens},"cache_read_input_tokens":31538}}}}}}"#
        )
    }

    fn ingest_claude(
        indexer: &UsageIndexer,
        path: &Path,
        cursor: Option<&crate::usage_db::UsageSourceCursor>,
    ) -> usize {
        indexer
            .ingest_jsonl(
                path,
                cursor,
                "claude",
                "local",
                "claude-jsonl",
                &path.to_string_lossy(),
            )
            .unwrap()
    }

    #[test]
    fn claude_usage_second_line_in_a_later_pass_updates_the_stored_event() {
        let root = claude_test_root("claude-incremental");
        let source_path = root.join("session.jsonl");
        let indexer = UsageIndexer::new(root.join("usage.sqlite3"));
        let mut source = fs::File::create(&source_path).unwrap();
        writeln!(source, "{}", claude_line("line-1", "msg-1", 16)).unwrap();
        source.flush().unwrap();
        assert_eq!(ingest_claude(&indexer, &source_path, None), 1);

        let db = UsageDb::open(&indexer.database_path).unwrap();
        let cursor = db
            .read_source_cursors()
            .unwrap()
            .into_values()
            .next()
            .unwrap();
        writeln!(source, "{}", claude_line("line-2", "msg-1", 214)).unwrap();
        source.flush().unwrap();
        assert_eq!(ingest_claude(&indexer, &source_path, Some(&cursor)), 1);

        let summary = db.read_usage_summary(&Default::default()).unwrap();
        assert_eq!(summary.event_count, 1);
        assert_eq!(summary.output_tokens, 214);
        assert_eq!(summary.cache_read_tokens, 31538);
        let _ = fs::remove_dir_all(root);
    }

    /// A resumed or forked Claude session copies earlier messages into a new
    /// transcript under the same message id. It is one API response, so it is
    /// counted once across files.
    #[test]
    fn claude_usage_same_message_in_two_session_files_counts_once() {
        let root = claude_test_root("claude-two-files");
        let indexer = UsageIndexer::new(root.join("usage.sqlite3"));
        let first = root.join("first.jsonl");
        let resumed = root.join("resumed.jsonl");
        fs::write(&first, format!("{}\n", claude_line("line-1", "msg-1", 214))).unwrap();
        fs::write(
            &resumed,
            format!("{}\n", claude_line("line-9", "msg-1", 214)),
        )
        .unwrap();
        ingest_claude(&indexer, &first, None);
        ingest_claude(&indexer, &resumed, None);

        let db = UsageDb::open(&indexer.database_path).unwrap();
        let summary = db.read_usage_summary(&Default::default()).unwrap();
        assert_eq!(summary.event_count, 1);
        assert_eq!(summary.output_tokens, 214);
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn claude_history_rebuild_replaces_old_line_keyed_rows_once() {
        let root = claude_test_root("claude-rebuild");
        let source_path = root.join("session.jsonl");
        let gone_path = root.join("gone.jsonl");
        let source_key = opaque_source_key(&source_path.to_string_lossy());
        let indexer = UsageIndexer::new(root.join("usage.sqlite3"));
        let mut source = fs::File::create(&source_path).unwrap();
        writeln!(source, "{}", claude_line("line-1", "msg-1", 16)).unwrap();
        writeln!(source, "{}", claude_line("line-2", "msg-1", 214)).unwrap();
        source.flush().unwrap();
        let db = UsageDb::open(&indexer.database_path).unwrap();
        // Seed what the old indexer stored: one row per transcript line, plus
        // a row whose transcript cannot be read.
        let old_row = |id: &str, key: String, output_tokens| crate::usage_db::UsageEvent {
            provider: "claude".into(),
            provider_instance_id: "local".into(),
            owned_id: Some("session-1".into()),
            workflow_id: None,
            turn_id: None,
            project_id: None,
            workspace_id: None,
            occurred_at_micros: 1_786_192_496_000_000,
            input_tokens: 2,
            output_tokens,
            cache_read_tokens: 31538,
            cache_write_tokens: 0,
            reasoning_tokens: 0,
            model: "model-a".into(),
            estimated_cost_micros: None,
            estimate_rate_version: None,
            source_kind: "claude-jsonl".into(),
            source_event_id: id.into(),
            source_key: key,
        };
        let cursor = crate::usage_sources::read_incremental_jsonl(
            &source_path,
            None,
            "claude",
            "local",
            "claude-jsonl",
            &source_path.to_string_lossy(),
        )
        .unwrap()
        .next_cursor;
        db.insert_events_with_cursor(
            &[
                old_row("line-1", source_key.clone(), 16),
                old_row("line-2", source_key, 214),
                old_row(
                    "gone-line",
                    opaque_source_key(&gone_path.to_string_lossy()),
                    5,
                ),
            ],
            Some(&cursor),
        )
        .unwrap();

        // A transcript that cannot be read keeps its history.
        assert!(indexer
            .ingest_jsonl(
                &gone_path,
                None,
                "claude",
                "local",
                "claude-jsonl",
                &gone_path.to_string_lossy(),
            )
            .is_err());
        assert_eq!(
            db.read_usage_summary(&Default::default())
                .unwrap()
                .event_count,
            3
        );

        // The stored cursor is ignored: the transcript is re-read from the
        // start and replaces its line-keyed rows.
        assert_eq!(ingest_claude(&indexer, &source_path, Some(&cursor)), 1);
        let summary = db.read_usage_summary(&Default::default()).unwrap();
        assert_eq!(summary.event_count, 2);
        assert_eq!(summary.output_tokens, 219);
        assert_eq!(db.read_daily(&Default::default()).unwrap().len(), 1);

        // Once rebuilt, the next pass reads only the new line.
        let cursor = db
            .read_source_cursors()
            .unwrap()
            .into_values()
            .next()
            .unwrap();
        writeln!(source, "{}", claude_line("line-3", "msg-2", 9)).unwrap();
        source.flush().unwrap();
        assert_eq!(ingest_claude(&indexer, &source_path, Some(&cursor)), 1);
        let summary = db.read_usage_summary(&Default::default()).unwrap();
        assert_eq!(summary.event_count, 3);
        assert_eq!(summary.output_tokens, 228);
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    #[ignore = "reads a bounded sample of the machine's real local transcript stores"]
    fn live_local_usage_sample_has_non_zero_totals() {
        let root = std::env::temp_dir().join(format!("mcb-live-usage-{}", std::process::id()));
        let indexer = UsageIndexer::new(root.join("usage.sqlite3"));
        let sources = crate::usage_sources::discover_local_usage_sources();
        assert!(
            !sources.is_empty(),
            "the machine should expose at least one local usage source"
        );
        let mut indexed_files = 0_usize;
        for source in sources.into_iter().take(32) {
            let key = source.path.to_string_lossy().to_string();
            let count = indexer
                .ingest_jsonl(
                    &source.path,
                    None,
                    &source.provider,
                    &source.provider_instance_id,
                    &source.source_kind,
                    &key,
                )
                .unwrap();
            indexed_files += 1;
            if count > 0 {
                break;
            }
        }
        let db = UsageDb::open(indexer.database_path.clone()).unwrap();
        let summary = db.read_usage_summary(&Default::default()).unwrap();
        crate::debug_log::stderr_log!("live usage sample indexed_files={indexed_files} events={} input={} output={} cache_read={} cache_write={} reasoning={}", summary.event_count, summary.input_tokens, summary.output_tokens, summary.cache_read_tokens, summary.cache_write_tokens, summary.reasoning_tokens);
        assert!(summary.event_count > 0);
        assert!(
            summary.input_tokens
                + summary.output_tokens
                + summary.cache_read_tokens
                + summary.cache_write_tokens
                + summary.reasoning_tokens
                > 0
        );
        let _ = fs::remove_dir_all(root);
    }
}
