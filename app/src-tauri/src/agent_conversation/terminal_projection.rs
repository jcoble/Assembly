use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap, HashSet, VecDeque};
use std::path::PathBuf;
use std::sync::mpsc::{self, Sender};
use std::sync::Mutex;
use std::thread::{self, JoinHandle};
use std::time::Duration;

use super::manager::AgentRuntimeManager;
use super::protocol::{
    AgentConversationProvider, AgentRawFrameReference, TerminalProjectionPayload,
};
use super::history::transcript::{self, FileIdentity, ProjectedRecord, TranscriptLocation};

const WATCH_INTERVAL: Duration = Duration::from_millis(250);
const MAX_INCREMENTAL_BYTES: u64 = 1024 * 1024;
const RECONCILE_EVERY_POLLS: u32 = 40;
const MAX_RECONCILIATION_EVENTS: usize = 512;
const MAX_SEEN_RECORDS: usize = 4096;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StartTerminalProjectionRequest {
    pub owned_id: String,
    pub provider: AgentConversationProvider,
    pub native_session_id: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TerminalProjectionRegistration {}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ReconcileReason {
    Initial,
    Periodic,
    Truncated,
    Rotated,
    Archived,
    Gap,
}

#[derive(Default)]
struct ProjectionCursor {
    canonical_path: Option<PathBuf>,
    identity: Option<FileIdentity>,
    offset: u64,
    partial_line: Vec<u8>,
    seen: HashSet<String>,
    seen_order: VecDeque<String>,
    polls_since_reconcile: u32,
    last_reconcile: Option<ReconcileReason>,
}

struct TranscriptProjector {
    provider: AgentConversationProvider,
    native_session_id: String,
    cursor: ProjectionCursor,
}

impl TranscriptProjector {
    fn new(provider: AgentConversationProvider, native_session_id: String) -> Self {
        Self {
            provider,
            native_session_id,
            cursor: ProjectionCursor::default(),
        }
    }

    fn poll(&mut self) -> Result<Vec<ProjectedRecord>, String> {
        let cached = self
            .cursor
            .canonical_path
            .as_deref()
            .map(transcript::inspect)
            .transpose()?
            .flatten();
        let Some(location) = cached.or(transcript::discover(
            self.provider,
            &self.native_session_id,
        )?) else {
            return Ok(Vec::new());
        };
        self.poll_location(location)
    }

    fn poll_location(
        &mut self,
        location: TranscriptLocation,
    ) -> Result<Vec<ProjectedRecord>, String> {
        let reason = match (&self.cursor.identity, &self.cursor.canonical_path) {
            (None, _) => Some(ReconcileReason::Initial),
            (Some(identity), Some(path))
                if identity == &location.identity && path != &location.canonical_path =>
            {
                Some(ReconcileReason::Archived)
            }
            (Some(identity), _) if identity != &location.identity => Some(ReconcileReason::Rotated),
            (Some(_), _) if location.len < self.cursor.offset => Some(ReconcileReason::Truncated),
            (Some(_), _)
                if location.len.saturating_sub(self.cursor.offset) > MAX_INCREMENTAL_BYTES =>
            {
                Some(ReconcileReason::Gap)
            }
            _ if self.cursor.polls_since_reconcile >= RECONCILE_EVERY_POLLS => {
                Some(ReconcileReason::Periodic)
            }
            _ => None,
        };
        if let Some(reason) = reason {
            return self.reconcile(location, reason);
        }
        self.cursor.polls_since_reconcile = self.cursor.polls_since_reconcile.saturating_add(1);
        if location.len == self.cursor.offset {
            return Ok(Vec::new());
        }
        let bytes = transcript::read_range(
            &location.canonical_path,
            self.cursor.offset,
            location.len.saturating_sub(self.cursor.offset),
        )?;
        self.cursor.offset = location.len;
        self.cursor.canonical_path = Some(location.canonical_path);
        self.cursor.identity = Some(location.identity);
        let mut combined = std::mem::take(&mut self.cursor.partial_line);
        combined.extend_from_slice(&bytes);
        let (lines, partial) = transcript::complete_lines(&combined, false);
        let records = self.project_lines(lines);
        self.cursor.partial_line = partial;
        Ok(records)
    }

    fn reconcile(
        &mut self,
        location: TranscriptLocation,
        reason: ReconcileReason,
    ) -> Result<Vec<ProjectedRecord>, String> {
        let (start, bytes) = transcript::read_bounded(&location.canonical_path, location.len)?;
        let (lines, partial) = transcript::complete_lines(&bytes, start > 0);
        let mut records = self.project_lines(lines);
        if records.len() > MAX_RECONCILIATION_EVENTS {
            records = records.split_off(records.len() - MAX_RECONCILIATION_EVENTS);
        }
        self.cursor.canonical_path = Some(location.canonical_path);
        self.cursor.identity = Some(location.identity);
        self.cursor.offset = location.len;
        self.cursor.partial_line = partial;
        self.cursor.polls_since_reconcile = 0;
        self.cursor.last_reconcile = Some(reason);
        Ok(records)
    }

    fn project_lines(&mut self, lines: Vec<&[u8]>) -> Vec<ProjectedRecord> {
        let mut projected = Vec::new();
        for line in lines {
            for record in
                transcript::parse_durable_line(self.provider, &self.native_session_id, line)
            {
                if self.cursor.seen.insert(record.key.clone()) {
                    self.cursor.seen_order.push_back(record.key.clone());
                    projected.push(record);
                }
            }
        }
        while self.cursor.seen_order.len() > MAX_SEEN_RECORDS {
            if let Some(expired) = self.cursor.seen_order.pop_front() {
                self.cursor.seen.remove(&expired);
            }
        }
        projected
    }
}

struct WatcherHandle {
    stop: Sender<()>,
    join: Option<JoinHandle<()>>,
}

impl WatcherHandle {
    fn stop(mut self) {
        let _ = self.stop.send(());
        if let Some(join) = self.join.take() {
            let _ = join.join();
        }
    }
}

#[derive(Default)]
pub struct TerminalProjectionRegistry {
    watchers: Mutex<HashMap<String, WatcherHandle>>,
}

impl TerminalProjectionRegistry {
    pub fn live_watcher_count(&self) -> Result<usize, String> {
        self.watchers
            .lock()
            .map(|watchers| watchers.len())
            .map_err(|_| "Terminal projection registry is unavailable".to_string())
    }

    pub fn start(
        &self,
        manager: AgentRuntimeManager,
        request: StartTerminalProjectionRequest,
    ) -> Result<TerminalProjectionRegistration, String> {
        let owned_id = required_id(&request.owned_id, "Owned session id")?;
        let native_session_id = transcript::safe_session_id(&request.native_session_id)?;
        let previous = self
            .watchers
            .lock()
            .map_err(|_| "Terminal projection registry is unavailable".to_string())?
            .remove(&owned_id);
        if let Some(previous) = previous {
            previous.stop();
        }
        let mut projector = TranscriptProjector::new(request.provider, native_session_id.clone());
        let initial = projector.poll()?;
        for record in initial {
            manager.submit_terminal_projection(
                &owned_id,
                projection_payload(&native_session_id, record),
            )?;
        }
        let (stop, stopped) = mpsc::channel();
        let thread_owned_id = owned_id.clone();
        let join = thread::Builder::new()
            .name(format!("terminal-projection-{owned_id}"))
            .spawn(move || loop {
                match stopped.recv_timeout(WATCH_INTERVAL) {
                    Ok(()) | Err(mpsc::RecvTimeoutError::Disconnected) => break,
                    Err(mpsc::RecvTimeoutError::Timeout) => {}
                }
                let Ok(records) = projector.poll() else {
                    continue;
                };
                for record in records {
                    let _ = manager.submit_terminal_projection(
                        &thread_owned_id,
                        projection_payload(&native_session_id, record),
                    );
                }
            })
            .map_err(|error| format!("Could not start terminal transcript projection: {error}"))?;
        self.watchers
            .lock()
            .map_err(|_| "Terminal projection registry is unavailable".to_string())?
            .insert(
                owned_id,
                WatcherHandle {
                    stop,
                    join: Some(join),
                },
            );
        Ok(TerminalProjectionRegistration {})
    }

    pub fn stop(&self, owned_id: &str) -> Result<bool, String> {
        let owned_id = required_id(owned_id, "Owned session id")?;
        let watcher = self
            .watchers
            .lock()
            .map_err(|_| "Terminal projection registry is unavailable".to_string())?
            .remove(&owned_id);
        if let Some(watcher) = watcher {
            watcher.stop();
            Ok(true)
        } else {
            Ok(false)
        }
    }
}

impl Drop for TerminalProjectionRegistry {
    fn drop(&mut self) {
        let watchers = self
            .watchers
            .get_mut()
            .ok()
            .map(std::mem::take)
            .unwrap_or_default();
        for (_, watcher) in watchers {
            watcher.stop();
        }
    }
}

fn projection_payload(
    native_session_id: &str,
    record: ProjectedRecord,
) -> TerminalProjectionPayload {
    TerminalProjectionPayload {
        event_type: record.event_type,
        provider_instance_id: format!("terminal-transcript:{native_session_id}"),
        timestamp_ms: u64::try_from(record.timestamp_ms)
            .ok()
            .filter(|value| *value != 0),
        native_session_id: native_session_id.to_string(),
        item_id: record.item_id.clone(),
        payload: record.payload,
        provider_metadata: BTreeMap::from([
            (
                "source".into(),
                serde_json::Value::String("terminal-transcript".into()),
            ),
            ("historical".into(), serde_json::Value::Bool(true)),
        ]),
        raw_frame_reference: AgentRawFrameReference {
            id: record.key,
            redacted: true,
        },
    }
}

fn required_id(value: &str, label: &str) -> Result<String, String> {
    let value = value.trim();
    if value.is_empty() {
        Err(format!("{label} is required"))
    } else {
        Ok(value.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::io::Write;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::Arc;

    fn temp_file(name: &str, contents: &[u8]) -> PathBuf {
        let root =
            std::env::temp_dir().join(format!("mcb-terminal-projection-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&root).unwrap();
        let path = root.join(name);
        fs::write(&path, contents).unwrap();
        path
    }

    fn location(path: &PathBuf) -> TranscriptLocation {
        let canonical_path = fs::canonicalize(path).unwrap();
        let metadata = canonical_path.metadata().unwrap();
        #[cfg(unix)]
        let identity = {
            use std::os::unix::fs::MetadataExt;
            FileIdentity {
                device: metadata.dev(),
                inode: metadata.ino(),
            }
        };
        #[cfg(not(unix))]
        let identity = FileIdentity {
            device: 0,
            inode: metadata.len(),
        };
        TranscriptLocation {
            canonical_path,
            identity,
            len: metadata.len(),
        }
    }

    #[test]
    fn append_only_parse_reads_only_new_durable_lines() {
        let path = temp_file(
            "codex.jsonl",
            include_bytes!(
                "../../fixtures/agent_conversation/terminal_projection/codex_append.jsonl"
            ),
        );
        let mut projector =
            TranscriptProjector::new(AgentConversationProvider::Codex, "codex-session".into());
        assert_eq!(projector.poll_location(location(&path)).unwrap().len(), 1);
        fs::OpenOptions::new().append(true).open(&path).unwrap().write_all(b"{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"id\":\"a2\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"second\"}]}}\n").unwrap();
        let records = projector.poll_location(location(&path)).unwrap();
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].item_id.as_deref(), Some("a2"));
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }

    #[test]
    fn offset_and_partial_line_resume_after_append() {
        let fixture = include_bytes!(
            "../../fixtures/agent_conversation/terminal_projection/codex_partial.jsonl"
        );
        let path = temp_file(
            "codex.jsonl",
            fixture.strip_suffix(b"\n").unwrap_or(fixture),
        );
        let mut projector =
            TranscriptProjector::new(AgentConversationProvider::Codex, "codex-session".into());
        assert!(projector.poll_location(location(&path)).unwrap().is_empty());
        assert!(!projector.cursor.partial_line.is_empty());
        fs::OpenOptions::new()
            .append(true)
            .open(&path)
            .unwrap()
            .write_all(b"}]} }\n")
            .unwrap();
        assert_eq!(projector.poll_location(location(&path)).unwrap().len(), 1);
        assert!(projector.cursor.partial_line.is_empty());
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }

    #[test]
    fn detects_truncation_rotation_and_archive_move() {
        let path = temp_file(
            "session.jsonl",
            include_bytes!(
                "../../fixtures/agent_conversation/terminal_projection/codex_append.jsonl"
            ),
        );
        let mut projector =
            TranscriptProjector::new(AgentConversationProvider::Codex, "codex-session".into());
        projector.poll_location(location(&path)).unwrap();
        fs::write(&path, b"\n").unwrap();
        projector.poll_location(location(&path)).unwrap();
        assert_eq!(
            projector.cursor.last_reconcile,
            Some(ReconcileReason::Truncated)
        );
        let rotated = path.with_file_name("session.jsonl.new");
        fs::write(
            &rotated,
            include_bytes!(
                "../../fixtures/agent_conversation/terminal_projection/codex_append.jsonl"
            ),
        )
        .unwrap();
        fs::rename(&rotated, &path).unwrap();
        projector.poll_location(location(&path)).unwrap();
        assert_eq!(
            projector.cursor.last_reconcile,
            Some(ReconcileReason::Rotated)
        );
        let archived = path.with_file_name("archived.jsonl");
        fs::rename(&path, &archived).unwrap();
        projector.poll_location(location(&archived)).unwrap();
        assert_eq!(
            projector.cursor.last_reconcile,
            Some(ReconcileReason::Archived)
        );
        fs::remove_dir_all(archived.parent().unwrap()).unwrap();
    }

    #[test]
    fn suppresses_duplicates_during_reconciliation() {
        let path = temp_file(
            "codex.jsonl",
            include_bytes!(
                "../../fixtures/agent_conversation/terminal_projection/codex_append.jsonl"
            ),
        );
        let mut projector =
            TranscriptProjector::new(AgentConversationProvider::Codex, "codex-session".into());
        assert_eq!(projector.poll_location(location(&path)).unwrap().len(), 1);
        projector.cursor.polls_since_reconcile = RECONCILE_EVERY_POLLS;
        assert!(projector.poll_location(location(&path)).unwrap().is_empty());
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }

    #[test]
    fn detects_incremental_gap_and_uses_bounded_reconciliation() {
        let path = temp_file(
            "codex.jsonl",
            include_bytes!(
                "../../fixtures/agent_conversation/terminal_projection/codex_append.jsonl"
            ),
        );
        let mut projector =
            TranscriptProjector::new(AgentConversationProvider::Codex, "codex-session".into());
        projector.poll_location(location(&path)).unwrap();
        projector.cursor.offset = 0;
        let mut file = fs::OpenOptions::new().append(true).open(&path).unwrap();
        file.write_all(&vec![b' '; MAX_INCREMENTAL_BYTES as usize + 1])
            .unwrap();
        file.write_all(b"\n").unwrap();
        drop(file);
        projector.poll_location(location(&path)).unwrap();
        assert_eq!(projector.cursor.last_reconcile, Some(ReconcileReason::Gap));
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }

    #[test]
    fn claude_fixture_projects_only_completed_historical_messages() {
        let input = include_bytes!(
            "../../fixtures/agent_conversation/terminal_projection/claude_append.jsonl"
        );
        let records = input
            .split(|byte| *byte == b'\n')
            .flat_map(|line| {
                transcript::parse_durable_line(
                    AgentConversationProvider::Claude,
                    "claude-session",
                    line,
                )
            })
            .collect::<Vec<_>>();
        assert!(records.iter().any(|record| {
            record.event_type == super::super::protocol::AgentEventType::ItemCompleted
                && record.item_id.as_deref() == Some("claude-a1")
        }));
        assert!(records.iter().all(|record| {
            record.payload.get("historical") == Some(&serde_json::Value::Bool(true))
        }));
    }

    #[test]
    fn bounded_reconciliation_caps_emitted_records() {
        let path = temp_file("codex.jsonl", b"");
        let line = b"{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"id\":\"ITEM\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"x\"}]}}\n";
        let mut file = fs::OpenOptions::new().append(true).open(&path).unwrap();
        for index in 0..(MAX_RECONCILIATION_EVENTS + 20) {
            file.write_all(
                &String::from_utf8_lossy(line)
                    .replace("ITEM", &format!("item-{index}"))
                    .into_bytes(),
            )
            .unwrap();
        }
        drop(file);
        let mut projector =
            TranscriptProjector::new(AgentConversationProvider::Codex, "codex-session".into());
        assert_eq!(
            projector.poll_location(location(&path)).unwrap().len(),
            MAX_RECONCILIATION_EVENTS
        );
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }

    #[test]
    fn no_invented_live_state_from_historical_frames() {
        let input = include_bytes!(
            "../../fixtures/agent_conversation/terminal_projection/no_invented_state.jsonl"
        );
        let records = input
            .split(|byte| *byte == b'\n')
            .flat_map(|line| {
                transcript::parse_durable_line(
                    AgentConversationProvider::Codex,
                    "codex-session",
                    line,
                )
            })
            .collect::<Vec<_>>();
        assert_eq!(records.len(), 1);
        assert!(records.iter().all(|record| !matches!(
            record.event_type,
            super::super::protocol::AgentEventType::TurnStarted
                | super::super::protocol::AgentEventType::ApprovalRequested
                | super::super::protocol::AgentEventType::ItemStarted
                | super::super::protocol::AgentEventType::ItemUpdated
        )));
        assert!(
            records
                .iter()
                .all(|record| record.payload.get("historical")
                    == Some(&serde_json::Value::Bool(true)))
        );
    }

    #[test]
    fn registry_drop_stops_and_joins_every_watcher() {
        let stopped = Arc::new(AtomicBool::new(false));
        let thread_stopped = Arc::clone(&stopped);
        let (stop, receiver) = mpsc::channel();
        let join = thread::spawn(move || {
            let _ = receiver.recv();
            thread_stopped.store(true, Ordering::SeqCst);
        });
        let registry = TerminalProjectionRegistry::default();
        registry.watchers.lock().unwrap().insert(
            "owned-a".into(),
            WatcherHandle {
                stop,
                join: Some(join),
            },
        );
        drop(registry);
        assert!(stopped.load(Ordering::SeqCst));
    }
}
