//! Remote history uses the local SQLite event journal and paging code.
//! An explicit source column keeps cached sessions out of local runtime startup.
use super::super::protocol::{
    AgentConversationConnection, AgentConversationEvent, AgentConversationEventPage,
    AgentConversationItemPage, AgentConversationPayload, AgentConversationSelectionSnapshot,
    AgentConversationSessionRecord, AgentConversationSnapshot, AgentRuntimeState, ApprovalState,
    ConversationConnectionState,
};
use mcb_core::session_store::{EventCoverage, EventRow, HistoryDownloadFacts, SessionRow, SessionStore};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;

#[derive(Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Coverage {
    #[serde(default)]
    download: Option<HistoryDownloadState>,
    remote_profile_id: String,
    connection: Option<AgentConversationConnection>,
    suspended: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    runtime_state: Option<AgentRuntimeState>,
    #[serde(default)]
    pending_permission: bool,
    #[serde(default)]
    pending_input: bool,
    start_complete: bool,
    #[serde(default)]
    has_earlier_transcript: bool,
    #[serde(default)]
    pending_events: Vec<AgentConversationEvent>,
    #[serde(default)]
    pending_sequence: Option<i64>,
    #[serde(default)]
    active_turn_id: Option<String>,
}

#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct HistoryDownloadState {
    pub total_bytes: u64,
    pub covered_bytes: u64,
    pub complete: bool,
}

#[cfg(test)]
mod tests {
    use super::super::super::protocol::{
        AgentConversationPayload, AgentConversationProvider, ConversationConnectionState,
    };
    use super::super::super::safe_markdown::parse_safe_markdown;
    use super::*;

    fn event(sequence: i64) -> AgentConversationEvent {
        AgentConversationEvent {
            runtime_state: None,
            owned_id: "same-session-id".into(),
            provider: AgentConversationProvider::Codex,
            generation: 1,
            sequence,
            timestamp_ms: 123,
            turn_id: Some("turn".into()),
            payload: AgentConversationPayload::AssistantMessage {
                item_id: format!("message-{sequence}"),
                text: "**prepared**".into(),
                completed: true,
                blocks: Some(parse_safe_markdown("**prepared**")),
            },
        }
    }

    fn control_event(sequence: i64, payload: AgentConversationPayload) -> AgentConversationEvent {
        AgentConversationEvent {
            payload,
            ..event(sequence)
        }
    }

    fn snapshot(start: i64, end: i64) -> AgentConversationSnapshot {
        AgentConversationSnapshot {
            connection: AgentConversationConnection {
                owned_id: "same-session-id".into(),
                provider: AgentConversationProvider::Codex,
                generation: 1,
                native_session_id: Some("native".into()),
                state: ConversationConnectionState::Disconnected,
                config: Default::default(),
            },
            suspended: true,
            last_sequence: end,
            events: (start..=end).map(event).collect(),
            has_earlier_transcript: false,
            pending_events: Vec::new(),
            active_turn_id: None,
        }
    }

    #[test]
    fn empty_start_after_a_live_event_keeps_the_saved_event() {
        let store = Arc::new(SessionStore::open_in_memory().unwrap());
        let history = RemoteHistory::new(store);
        history.live_batch("p", 0, &mut [event(1)]).unwrap();
        // Totals were empty, but metadata was sampled after the first live event.
        let mut metadata = snapshot(2, 1);
        history.begin_download("p", 0, HistoryDownloadFacts {
            low_sequence: None, high_sequence: None, row_count: 0, total_bytes: 0, covered_bytes: 0,
        }, &mut metadata).unwrap();
        history.complete_download("p", 0, "same-session-id").unwrap();
        let saved = history.read_selection_snapshot("p", "same-session-id", 1024).unwrap().unwrap();
        assert_eq!(saved.page.events.iter().map(|event| event.sequence).collect::<Vec<_>>(), [1]);
    }

    #[test]
    fn download_chunks_save_quietly_and_prepare_markdown() {
        let store = Arc::new(SessionStore::open_in_memory().unwrap());
        let history = RemoteHistory::new(store.clone());
        history.prepare_download("p", "same-session-id").unwrap();
        let mut metadata = snapshot(1, 3);
        metadata.events.clear();
        history.begin_download("p", 0, HistoryDownloadFacts { low_sequence: Some(1), high_sequence: Some(3), row_count: 3, total_bytes: 300, covered_bytes: 0 }, &mut metadata).unwrap();
        let mut rows = vec![event(3), event(2)];
        if let AgentConversationPayload::AssistantMessage { blocks, .. } = &mut rows[0].payload { *blocks = None; }
        history.download_chunk("p", 0, "same-session-id", 200, &mut rows).unwrap();
        if let AgentConversationPayload::AssistantMessage { blocks, .. } = &mut rows[0].payload { *blocks = None; }
        history.download_chunk("p", 0, "same-session-id", 200, &mut rows).unwrap();
        assert!(matches!(&rows[0].payload, AgentConversationPayload::AssistantMessage { blocks: Some(_), .. }));
        assert_eq!(store.list_events(&RemoteHistory::key("p", "same-session-id"), 0, 100).unwrap().len(), 2);
        assert_eq!(history.download_state("p", "same-session-id").unwrap().unwrap().covered_bytes, 200);
        assert!(!history.download_state("p", "same-session-id").unwrap().unwrap().complete);
    }

    #[test]
    fn desktop_markdown_snapshot_and_page_save_pieces_and_keep_received_pieces() {
        let store = Arc::new(SessionStore::open_in_memory().unwrap());
        let history = RemoteHistory::new(store.clone());
        let mut initial = snapshot(2, 2);
        if let AgentConversationPayload::AssistantMessage { blocks, .. } = &mut initial.events[0].payload {
            *blocks = None;
        }
        history.snapshot("a", 0, None, &mut initial).unwrap();
        assert!(matches!(&initial.events[0].payload,
            AgentConversationPayload::AssistantMessage { blocks: Some(_), .. }));
        let mut older = event(1);
        if let AgentConversationPayload::AssistantMessage { blocks, .. } = &mut older.payload {
            *blocks = None;
        }
        let mut page = AgentConversationEventPage { events: vec![older], has_more: false };
        history.page("a", 0, "same-session-id", 2, true, &mut page).unwrap();
        assert!(matches!(&page.events[0].payload,
            AgentConversationPayload::AssistantMessage { blocks: Some(_), .. }));
        let saved = history.read_snapshot("a", 0, "same-session-id").unwrap();
        assert_eq!(saved.events, vec![page.events[0].clone(), initial.events[0].clone()]);
        // An older backend's pieces remain authoritative even if they differ from this parser.
        let mut received = event(3);
        if let AgentConversationPayload::AssistantMessage { blocks, .. } = &mut received.payload {
            *blocks = Some(Vec::new());
        }
        history.live_batch("a", 0, &mut [received.clone()]).unwrap();
        assert_eq!(history.read_snapshot("a", 0, "same-session-id").unwrap().events.last(), Some(&received));
    }

    #[test]
    fn remote_history_reopens_with_prepared_markdown_and_isolated_sources() {
        let directory =
            std::env::temp_dir().join(format!("assembly-history-test-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let path = directory.join("sessions.db");
        let store = Arc::new(SessionStore::open(&path).unwrap());
        let history = RemoteHistory::new(store.clone());
        let mut expected = snapshot(-2, 5);
        history.snapshot("a", 0, None, &mut expected).unwrap();
        history.snapshot("a", 0, None, &mut expected).unwrap();
        history.snapshot("b", 0, None, &mut snapshot(20, 22)).unwrap();
        assert_eq!(store.count_sessions().unwrap(), 0);
        drop(history);
        drop(store);
        let mut history = RemoteHistory::new(Arc::new(SessionStore::open(&path).unwrap()));
        assert_eq!(
            history
                .read_snapshot("a", 0, "same-session-id")
                .unwrap()
                .events,
            expected.events
        );
        history.purge("a").unwrap();
        assert!(history.read_selection_snapshot("a", "same-session-id", 1024).unwrap().is_none());
        assert_eq!(
            history
                .read_snapshot("b", 0, "same-session-id")
                .unwrap()
                .events
                .len(),
            3
        );
        assert!(history.live_batch("a", 0, &mut [event(6)]).is_err());
        assert!(history.snapshot("a", 0, None, &mut expected).is_err());
        assert!(history
            .page(
                "a",
                0,
                "same-session-id",
                6,
                true,
                &mut AgentConversationEventPage {
                    events: vec![event(5)],
                    has_more: false
                }
            )
            .is_err());
        drop(history);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn remote_children_reopen_under_two_exact_profile_keys() {
        let directory =
            std::env::temp_dir().join(format!("assembly-child-history-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let path = directory.join("sessions.db");
        let store = Arc::new(SessionStore::open(&path).unwrap());
        let history = RemoteHistory::new(store.clone());
        for profile in ["a", "b"] {
            history.snapshot(profile, 0, None, &mut snapshot(1, 1)).unwrap();
            let child_key = history
                .bind_child(profile, "same-session-id", "same-child", "provider-child")
                .unwrap();
            let mut child_event = event(1);
            child_event.owned_id = "same-child".into();
            history.live_batch(profile, 0, &mut [child_event]).unwrap();
            let source = store
                .private_remote_child_source(&child_key)
                .unwrap()
                .unwrap();
            assert_eq!(source.remote_profile_id, profile);
            assert_eq!(source.source_owned_id, "same-child");
        }
        drop(history);
        drop(store);

        let history = RemoteHistory::new(Arc::new(SessionStore::open(&path).unwrap()));
        let a = history
            .read_child_item_page("a", "same-child", i64::MAX, 1024, true)
            .unwrap()
            .unwrap();
        let b = history
            .read_child_item_page("b", "same-child", i64::MAX, 1024, true)
            .unwrap()
            .unwrap();
        assert_eq!(a.events[0].owned_id, RemoteHistory::key("a", "same-child"));
        assert_eq!(b.events[0].owned_id, RemoteHistory::key("b", "same-child"));
        drop(history);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn a_session_first_opened_empty_pages_its_live_events_locally() {
        let history = RemoteHistory::new(Arc::new(SessionStore::open_in_memory().unwrap()));
        // A session created here opens before its first event, then streams 1..=3.
        history.snapshot("a", 0, None, &mut snapshot(1, 0)).unwrap();
        history.live_batch("a", 0, &mut [event(1), event(2), event(3)]).unwrap();
        let older = history.read_page("a", "same-session-id", 3, 1024, true).unwrap().unwrap();
        assert_eq!(older.events.iter().map(|e| e.sequence).collect::<Vec<_>>(), vec![1, 2]);
        // The copy confirms the empty start once; then the top needs no request.
        assert!(!history.coverage("a", "same-session-id").unwrap().start_complete);
        let mut start = AgentConversationEventPage { events: vec![], has_more: false };
        history.page("a", 0, "same-session-id", 1, true, &mut start).unwrap();
        assert!(history.coverage("a", "same-session-id").unwrap().start_complete);
        assert!(!history.read_page("a", "same-session-id", 3, 1024, true).unwrap().unwrap().has_more);
    }

    #[test]
    fn rows_past_a_hole_read_as_saved() {
        let history = RemoteHistory::new(Arc::new(SessionStore::open_in_memory().unwrap()));
        history.snapshot("a", 0, None, &mut snapshot(10, 12)).unwrap();
        let mut saved_gap = event(15);
        if let AgentConversationPayload::AssistantMessage { blocks, .. } = &mut saved_gap.payload {
            *blocks = Some(Vec::new());
        }
        history.live_batch("a", 0, &mut [saved_gap.clone()]).unwrap();
        let newer = history.read_page("a", "same-session-id", 11, 1024, false).unwrap().unwrap();
        assert_eq!(newer.events, vec![event(12), saved_gap]);
        let mut older = AgentConversationEventPage {
            events: (5..10).map(event).collect(),
            has_more: true,
        };
        history
            .page("a", 0, "same-session-id", 10, true, &mut older)
            .unwrap();
        assert_eq!(
            history
                .read_page("a", "same-session-id", 10, 100_000, true)
                .unwrap()
                .unwrap()
                .events,
            older.events
        );
        assert!(history
            .read_page("a", "same-session-id", 5, 1024, true)
            .unwrap()
            .is_none());
        history
            .page(
                "a",
                0,
                "same-session-id",
                5,
                true,
                &mut AgentConversationEventPage {
                    events: vec![event(4)],
                    has_more: false,
                },
            )
            .unwrap();
        let page = history
            .read_page("a", "same-session-id", 4, 1024, true)
            .unwrap()
            .unwrap();
        assert!(page.events.is_empty());
        assert!(!page.has_more);
        history.imported_older("a", 0, "same-session-id").unwrap();
        assert!(history
            .read_page("a", "same-session-id", 4, 1024, true)
            .unwrap()
            .is_none());
    }

    #[test]
    fn pending_controls_follow_the_authoritative_head_not_older_pages() {
        let history = RemoteHistory::new(Arc::new(SessionStore::open_in_memory().unwrap()));
        let requested = control_event(
            15,
            AgentConversationPayload::Approval {
                request_id: "child-permission".into(),
                state: ApprovalState::Requested,
                summary: "Child permission".into(), options: Vec::new(),
            },
        );
        // The authoritative control cursor can be ahead of the contiguous
        // copied content range while background history catches up.
        let mut initial = snapshot(10, 15);
        initial.events = vec![event(10)];
        initial.pending_events = vec![requested.clone()];
        history.snapshot("a", 0, None, &mut initial).unwrap();
        let raw = history.read_snapshot("a", 0, "same-session-id").unwrap();
        assert_eq!(raw.last_sequence, 10);
        assert_eq!(raw.pending_events, vec![requested.clone()]);
        let selected = history
            .read_selection_snapshot("a", "same-session-id", 1024)
            .unwrap()
            .unwrap();
        assert_eq!(selected.page.watermark, 10);
        assert_eq!(selected.pending_sequence, 15);

        history
            .live_batch(
                "a",
                0,
                &mut [
                    control_event(
                        11,
                        AgentConversationPayload::Turn {
                            turn_id: "root-turn".into(),
                            state: super::super::super::protocol::TurnState::Completed,
                        },
                    ),
                    event(12),
                    event(13),
                    event(14),
                    requested.clone(),
                    control_event(
                        16,
                        AgentConversationPayload::Approval {
                            request_id: "child-permission".into(),
                            state: ApprovalState::Expired,
                            summary: "Child permission".into(), options: Vec::new(),
                        },
                    ),
                ],
            )
            .unwrap();
        let mut older = AgentConversationEventPage {
            events: vec![requested],
            has_more: false,
        };
        history.page("a", 0, "same-session-id", 10, true, &mut older).unwrap();
        assert!(history.read_snapshot("a", 0, "same-session-id").unwrap().pending_events.is_empty());
    }

    #[test]
    fn empty_remote_selection_preserves_head_and_copy_state() {
        let history = RemoteHistory::new(Arc::new(SessionStore::open_in_memory().unwrap()));
        let mut empty = snapshot(1, 0);
        empty.has_earlier_transcript = true;
        history.snapshot("a", 0, None, &mut empty).unwrap();

        let copying = history.read_selection_snapshot("a", "same-session-id", 1024)
            .unwrap().unwrap();
        assert!(copying.page.items.is_empty());
        assert_eq!(copying.page.watermark, 0);
        assert!(copying.page.has_before);
        assert!(copying.page.has_earlier_transcript);
        assert!(copying.page.before_cursor.is_none());

        history.page("a", 0, "same-session-id", 1, true,
            &mut AgentConversationEventPage { events: Vec::new(), has_more: false }).unwrap();
        let copied = history.read_selection_snapshot("a", "same-session-id", 1024)
            .unwrap().unwrap();
        assert!(!copied.page.has_before);
        assert!(copied.page.has_earlier_transcript);
    }

    #[test]
    fn item_pages_use_logical_positions_before_waiting_for_missing_history() {
        let history = RemoteHistory::new(Arc::new(SessionStore::open_in_memory().unwrap()));
        let saved = snapshot(100, 102);
        history.write("a", "same-session-id", &Coverage {
            connection: Some(saved.connection), ..Default::default()
        }, &mut []).unwrap();
        for (sequence, first_sequence) in [(100, 10), (101, 10), (102, 80)] {
            let mut wire = serde_json::to_value(event(sequence)).unwrap();
            wire["payload"] = serde_json::json!({
                "kind": "tool", "itemId": if first_sequence == 10 { "early" } else { "later" },
                "name": "read", "state": "completed", "firstSequence": first_sequence,
                "firstTimestampMs": first_sequence * 10
            });
            history.store.append_event(&EventRow {
                owned_id: RemoteHistory::key("a", "same-session-id"), seq: sequence,
                turn_id: Some("turn".into()), kind: "remote.cached".into(),
                payload_json: wire.to_string(), created_at_ms: 123,
            }).unwrap();
        }
        let older = history.read_item_page("a", "same-session-id", 50, 1024, true)
            .unwrap().unwrap();
        assert_eq!(older.items.iter().map(|item| (item.item_id.as_str(), item.first_sequence))
            .collect::<Vec<_>>(), [("early", 10)]);
        let newer = history.read_item_page("a", "same-session-id", 50, 1024, false)
            .unwrap().unwrap();
        assert_eq!(newer.items.iter().map(|item| (item.item_id.as_str(), item.first_sequence))
            .collect::<Vec<_>>(), [("later", 80)]);
        assert!(history.read_item_page("a", "same-session-id", 10, 1024, true)
            .unwrap().is_none());

        // A copied control record before the first item is also a missing item
        // boundary, even though its physical sequence is below the item cursor.
        let mut controls = snapshot(200, 201);
        controls.events[0] = control_event(200, AgentConversationPayload::Turn {
            turn_id: "turn".into(), state: super::super::super::protocol::TurnState::Started,
        });
        history.forget("a", "same-session-id").unwrap();
        history.snapshot("a", 0, None, &mut controls).unwrap();
        assert!(history.read_item_page("a", "same-session-id", 201, 1024, true)
            .unwrap().is_none());
    }

    #[test]
    fn newer_runtime_generation_preserves_the_contiguous_journal() {
        let history = RemoteHistory::new(Arc::new(SessionStore::open_in_memory().unwrap()));
        history.snapshot("a", 0, None, &mut snapshot(1, 3)).unwrap();
        let mut restarted = snapshot(1, 5);
        restarted.connection.generation = 2;
        for event in restarted.events.iter_mut().filter(|event| event.sequence > 3) {
            event.generation = 2;
        }
        history.snapshot("a", 0, None, &mut restarted).unwrap();

        let saved = history.read_snapshot("a", 0, "same-session-id").unwrap();
        assert_eq!(saved.connection.generation, 2);
        assert_eq!(saved.events.len(), 5);
        assert_eq!(saved.events.first().map(|event| event.sequence), Some(1));
        assert_eq!(saved.events.last().map(|event| event.sequence), Some(5));
    }

    #[test]
    fn adjacent_live_batch_survives_reopen_in_order() {
        let directory = std::env::temp_dir().join(format!("assembly-batch-test-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let path = directory.join("sessions.db");
        let history = RemoteHistory::new(Arc::new(SessionStore::open(&path).unwrap()));
        history.snapshot("a", 0, None, &mut snapshot(1, 1)).unwrap();
        history.live_batch("a", 0, &mut [event(2), event(3), event(4)]).unwrap();
        drop(history);
        let history = RemoteHistory::new(Arc::new(SessionStore::open(&path).unwrap()));
        assert_eq!(history.read_snapshot("a", 0, "same-session-id").unwrap().events,
            vec![event(1), event(2), event(3), event(4)]);
        drop(history);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn remote_history_caches_live_messages_before_a_snapshot_and_bounds_reads() {
        let history = RemoteHistory::new(Arc::new(SessionStore::open_in_memory().unwrap()));
        for sequence in 1..=10 {
            history.live_batch("a", 0, &mut [event(sequence)]).unwrap();
        }
        history
            .snapshot("a", 0, Some(10), &mut snapshot(11, 10))
            .unwrap();
        assert_eq!(
            history
                .read_snapshot("a", 0, "same-session-id")
                .unwrap()
                .events
                .len(),
            10
        );
        let page = history
            .read_page("a", "same-session-id", 11, 400, true)
            .unwrap()
            .unwrap();
        assert!(!page.events.is_empty());
        assert!(page.events.len() < 10);
        assert_eq!(page.events.last().unwrap().sequence, 10);
        assert!(page.has_more);
    }
}

pub(super) struct RemoteHistory {
    store: Arc<SessionStore>,
    epochs: HashMap<String, u64>,
}

impl RemoteHistory {
    pub fn new(store: Arc<SessionStore>) -> Self {
        Self {
            store,
            epochs: HashMap::new(),
        }
    }

    pub fn epoch(&self, profile: &str) -> u64 {
        *self.epochs.get(profile).unwrap_or(&0)
    }

    pub fn check(&self, profile: &str, epoch: u64) -> Result<(), String> {
        if self.epoch(profile) != epoch {
            return Err("Remote history request was disconnected".into());
        }
        Ok(())
    }

    pub fn disconnect(&mut self, profile: &str) {
        *self.epochs.entry(profile.into()).or_default() += 1;
    }

    pub fn purge(&mut self, profile: &str) -> Result<(), String> {
        self.disconnect(profile);
        self.store
            .purge_remote_history(profile)
            .map_err(|e| e.to_string())
    }

    pub(super) fn key(profile: &str, owned: &str) -> String {
        // Tuple encoding prevents two machines with the same owned ID colliding.
        serde_json::to_string(&(profile, owned)).expect("string tuple is serializable")
    }

    pub fn bind_child(
        &self,
        profile: &str,
        parent_owned: &str,
        child_owned: &str,
        child_native_id: &str,
    ) -> Result<String, String> {
        let history_owned_id = Self::key(profile, child_owned);
        let coverage = self.coverage(profile, child_owned)?;
        let row = SessionRow {
            owned_id: history_owned_id.clone(),
            native_session_id: Some(child_native_id.to_string()),
            provider: "remote-cache".into(),
            model: None,
            effort: None,
            cwd: String::new(),
            worktree: None,
            branch: None,
            title: None,
            title_source: None,
            project: None,
            project_id: None,
            state: "cached".into(),
            suspended: true,
            created_at_ms: 0,
            last_activity_at_ms: 0,
            extra_json: serde_json::to_string(&coverage).map_err(|error| error.to_string())?,
        };
        if self
            .store
            .get_session(&history_owned_id)
            .map_err(|error| error.to_string())?
            .is_some()
        {
            self.store
                .bind_cached_remote_child(
                    &Self::key(profile, parent_owned),
                    profile,
                    &history_owned_id,
                    child_native_id,
                )
                .map_err(|error| error.to_string())?;
        } else {
            self.store
                .upsert_child_session(&Self::key(profile, parent_owned), Some(profile), &row)
                .map_err(|error| error.to_string())?;
        }
        Ok(history_owned_id)
    }

    /// Gives every session the machine listed a row here, filed under its
    /// project from the start, before any of its events are copied.
    pub fn file_sessions(&self, profile: &str, sessions: &[AgentConversationSessionRecord]) -> Result<(), String> {
        let rows: Vec<_> = sessions.iter()
            .map(|session| (Self::key(profile, &session.owned_id), &session.project_id, session.last_activity_at_ms,
                Coverage { remote_profile_id: profile.into(), runtime_state: Some(session.state),
                    suspended: session.suspended, active_turn_id: session.active_turn_id.clone(),
                    pending_permission: session.pending_permission, pending_input: session.pending_input,
                    ..Default::default() }))
            .collect();
        self.store
            .file_remote_sessions(
                profile,
                &serde_json::to_string(&rows).map_err(|error| error.to_string())?,
            )
            .map_err(|error| error.to_string())
    }

    pub fn delivery_owned_id(&self, profile: &str, source_owned_id: &str) -> Option<String> {
        let physical = Self::key(profile, source_owned_id);
        self.store
            .private_remote_child_source(&physical)
            .ok()
            .flatten()
            .map(|_| physical)
    }

    /// Sessions whose pushed batch skips a sequence, with their original saved maximum.
    pub fn skipped(&self, profile: &str, events: &[AgentConversationEvent]) -> Result<BTreeMap<String, i64>, String> {
        let mut batches = BTreeMap::new();
        for event in events { batches.entry(event.owned_id.as_str()).or_insert_with(Vec::new).push(event.sequence); }
        let mut skipped = BTreeMap::new();
        for (owned, sequences) in batches {
            let (_, saved) = self.bounds(profile, owned)?;
            let Some(saved) = saved else { continue; };
            let mut highest = saved;
            for sequence in sequences {
                if sequence > highest.saturating_add(1) {
                    skipped.insert(owned.to_string(), saved);
                    break;
                }
                highest = highest.max(sequence);
            }
        }
        Ok(skipped)
    }

    fn bounds(&self, profile: &str, owned: &str) -> Result<(Option<i64>, Option<i64>), String> {
        self.store.remote_saved_bounds(&Self::key(profile, owned)).map_err(|e| e.to_string())
    }

    fn coverage(&self, profile: &str, owned: &str) -> Result<Coverage, String> {
        let Some(row) = self
            .store
            .get_session(&Self::key(profile, owned))
            .map_err(|e| e.to_string())?
        else {
            return Ok(Coverage {
                remote_profile_id: profile.into(),
                ..Default::default()
            });
        };
        serde_json::from_str(&row.extra_json).map_err(|e| e.to_string())
    }

    pub fn forget_if_shrunk(&self, profile: &str, owned: &str, head: i64, saved: Option<i64>) -> Result<bool, String> {
        if saved.is_none_or(|saved| saved <= head) { return Ok(false); }
        self.forget(profile, owned)?;
        Ok(true)
    }

    pub fn forget(&self, profile: &str, owned: &str) -> Result<(), String> {
        self.store
            .delete_session(&Self::key(profile, owned))
            .map_err(|e| e.to_string())
    }

    fn write(
        &self,
        profile: &str,
        owned: &str,
        coverage: &Coverage,
        events: &mut [AgentConversationEvent],
    ) -> Result<(), String> {
        let key = Self::key(profile, owned);
        let sequences: Vec<_> = events.iter().filter_map(|event| matches!(
            event.payload, AgentConversationPayload::AssistantMessage { blocks: None, .. }
        ).then_some(event.sequence)).collect();
        let mut saved_blocks = HashMap::new();
        if !sequences.is_empty() {
            for row in self.store.get_events_by_sequences(&key, &sequences).map_err(|e| e.to_string())? {
                let saved: AgentConversationEvent = serde_json::from_str(&row.payload_json).map_err(|e| e.to_string())?;
                if let AgentConversationPayload::AssistantMessage { blocks: Some(blocks), .. } = saved.payload {
                    saved_blocks.insert(row.seq, blocks);
                }
            }
        }
        for event in events.iter_mut() {
            if let AgentConversationPayload::AssistantMessage { text, blocks, .. } = &mut event.payload {
                if blocks.is_none() {
                    *blocks = Some(saved_blocks.get(&event.sequence).cloned()
                        .unwrap_or_else(|| super::super::safe_markdown::parse_safe_markdown(text)));
                }
            }
        }
        let rows = events
            .iter()
            .map(|event| {
                Ok(EventRow {
                    owned_id: key.clone(),
                    seq: event.sequence,
                    turn_id: event.turn_id.clone(),
                    kind: "remote.cached".into(),
                    // Prepare on the displaying desktop, then save and publish the same pieces.
                    payload_json: serde_json::to_string(event).map_err(|e| e.to_string())?,
                    created_at_ms: i64::try_from(event.timestamp_ms).unwrap_or(i64::MAX),
                })
            })
            .collect::<Result<Vec<_>, String>>()?;
        let row = SessionRow {
            owned_id: key,
            native_session_id: coverage
                .connection
                .as_ref()
                .and_then(|c| c.native_session_id.clone()),
            provider: "remote-cache".into(),
            model: None,
            effort: None,
            cwd: String::new(),
            worktree: None,
            branch: None,
            title: None,
            title_source: None,
            project: None,
            project_id: None,
            state: "cached".into(),
            suspended: coverage.suspended,
            created_at_ms: 0,
            last_activity_at_ms: rows.last().map_or(0, |row| row.created_at_ms),
            extra_json: serde_json::to_string(coverage).map_err(|e| e.to_string())?,
        };
        self.store
            .cache_remote_events(profile, &row, &rows)
            .map_err(|e| e.to_string())
    }

    pub fn live_batch(
        &self,
        profile: &str,
        epoch: u64,
        events: &mut [AgentConversationEvent],
    ) -> Result<(), String> {
        self.check(profile, epoch)?;
        let Some(owned) = events.first().map(|event| event.owned_id.clone()) else { return Ok(()); };
        if events.iter().any(|event| event.owned_id != owned) {
            return Err("remote history batch contains multiple sessions".into());
        }
        let mut coverage = self.coverage(profile, &owned)?;
        // Control state moves forward only, so a replayed duplicate can't revive a finished turn.
        let control_start = coverage.pending_sequence.unwrap_or(i64::MIN);
        for event in events.iter().filter(|event| event.sequence > control_start) {
            apply_pending_event(&mut coverage.pending_events, event);
            apply_active_turn(&mut coverage.active_turn_id, event);
            if let Some(state) = event.runtime_state {
                coverage.runtime_state = Some(state);
                coverage.suspended = state == AgentRuntimeState::Suspended;
            }
            coverage.pending_permission = coverage.pending_events.iter().any(|event|
                matches!(event.payload, AgentConversationPayload::Approval { state: ApprovalState::Requested, .. }));
            coverage.pending_input = coverage.pending_events.iter().any(|event|
                matches!(event.payload, AgentConversationPayload::UserInputRequested { .. }));
            coverage.pending_sequence = Some(event.sequence);
        }
        self.write(profile, &owned, &coverage, events)
    }

    pub fn snapshot(
        &self,
        profile: &str,
        epoch: u64,
        previous: Option<i64>,
        snapshot: &mut AgentConversationSnapshot,
    ) -> Result<i64, String> {
        self.check(profile, epoch)?;
        let owned = &snapshot.connection.owned_id;
        let mut coverage = self.coverage(profile, owned)?;
        coverage.connection = Some(snapshot.connection.clone());
        coverage.has_earlier_transcript = snapshot.has_earlier_transcript;
        if coverage
            .pending_sequence
            .is_none_or(|sequence| snapshot.last_sequence >= sequence)
        {
            coverage.pending_events.clone_from(&snapshot.pending_events);
            coverage.pending_sequence = Some(snapshot.last_sequence);
            coverage.active_turn_id.clone_from(&snapshot.active_turn_id);
            coverage.suspended = snapshot.suspended;
            coverage.pending_permission = snapshot.pending_events.iter().any(|event|
                matches!(event.payload, AgentConversationPayload::Approval { state: ApprovalState::Requested, .. }));
            coverage.pending_input = snapshot.pending_events.iter().any(|event|
                matches!(event.payload, AgentConversationPayload::UserInputRequested { .. }));
            coverage.runtime_state = Some(if snapshot.connection.state == ConversationConnectionState::Failed {
                    AgentRuntimeState::Failed
                }
                else if snapshot.suspended { AgentRuntimeState::Suspended }
                else if coverage.pending_permission { AgentRuntimeState::WaitingApproval }
                else if coverage.pending_input { AgentRuntimeState::WaitingInput }
                else if snapshot.active_turn_id.is_some() { AgentRuntimeState::Working }
                else { AgentRuntimeState::Ready });
        }
        let end = snapshot
            .events
            .last()
            .map(|event| event.sequence)
            .or(previous)
            .unwrap_or(snapshot.last_sequence);
        self.write(profile, owned, &coverage, &mut snapshot.events)?;
        Ok(end)
    }

    #[cfg(test)]
    pub fn read_snapshot(
        &self,
        profile: &str,
        epoch: u64,
        owned: &str,
    ) -> Result<AgentConversationSnapshot, String> {
        self.check(profile, epoch)?;
        let coverage = self.coverage(profile, owned)?;
        let (low, high) = self.bounds(profile, owned)?;
        let (through, oldest) = (high.unwrap_or(0), low.unwrap_or(i64::MIN));
        let page = self
            .store
            .list_events_before(
                &Self::key(profile, owned),
                through.saturating_add(1),
                512 * 1024,
                oldest,
            )
            .map_err(|e| e.to_string())?;
        Ok(AgentConversationSnapshot {
            connection: coverage
                .connection
                .ok_or("Remote history has no connection metadata")?,
            suspended: coverage.suspended,
            last_sequence: through,
            events: Self::decode(page.events)?,
            has_earlier_transcript: coverage.has_earlier_transcript,
            pending_events: coverage.pending_events,
            active_turn_id: coverage.active_turn_id,
        })
    }

    /// The saved tail of a session opened here before, or None when the
    /// Mac has never saved its connection details.
    #[cfg(test)]
    pub fn cached_snapshot(
        &self,
        profile: &str,
        owned: &str,
    ) -> Result<Option<AgentConversationSnapshot>, String> {
        if self.coverage(profile, owned)?.connection.is_none() {
            return Ok(None);
        }
        self.read_snapshot(profile, self.epoch(profile), owned).map(Some)
    }

    pub fn download_state(&self, profile: &str, owned: &str) -> Result<Option<HistoryDownloadState>, String> {
        Ok(self.coverage(profile, owned)?.download)
    }

    pub fn prepare_download(&self, profile: &str, owned: &str) -> Result<(), String> {
        let mut coverage = self.coverage(profile, owned)?;
        coverage.download.get_or_insert_with(Default::default).complete = false;
        self.write(profile, owned, &coverage, &mut [])
    }

    pub fn begin_download(&self, profile: &str, epoch: u64, facts: HistoryDownloadFacts,
        snapshot: &mut AgentConversationSnapshot) -> Result<(), String> {
        self.check(profile, epoch)?;
        let owned = &snapshot.connection.owned_id;
        let mut coverage = self.coverage(profile, owned)?;
        coverage.connection = Some(snapshot.connection.clone());
        coverage.suspended = snapshot.suspended;
        coverage.has_earlier_transcript = snapshot.has_earlier_transcript;
        if coverage.pending_sequence.is_none_or(|sequence| sequence <= snapshot.last_sequence) {
            coverage.pending_sequence = Some(snapshot.last_sequence);
            coverage.pending_events.clone_from(&snapshot.pending_events);
            coverage.active_turn_id.clone_from(&snapshot.active_turn_id);
        }
        coverage.download = Some(HistoryDownloadState { total_bytes: facts.total_bytes, covered_bytes: facts.covered_bytes, complete: false });
        self.write(profile, owned, &coverage, &mut [])
    }

    pub fn download_chunk(&self, profile: &str, epoch: u64, owned: &str, covered_bytes: u64,
        events: &mut [AgentConversationEvent]) -> Result<(), String> {
        self.check(profile, epoch)?;
        let mut coverage = self.coverage(profile, owned)?;
        if let Some(download) = &mut coverage.download { download.covered_bytes = covered_bytes; }
        if events.iter().any(|event| event.owned_id != owned) { return Err("History chunk contains another session".into()); }
        self.write(profile, owned, &coverage, events)
    }

    pub fn complete_download(&self, profile: &str, epoch: u64, owned: &str) -> Result<(), String> {
        self.check(profile, epoch)?;
        let mut coverage = self.coverage(profile, owned)?;
        if let Some(download) = &mut coverage.download {
            download.complete = true;
            download.covered_bytes = download.total_bytes;
        }
        coverage.start_complete = true;
        self.write(profile, owned, &coverage, &mut [])
    }

    pub fn read_selection_snapshot(
        &self,
        profile: &str,
        owned: &str,
        max_bytes: u32,
    ) -> Result<Option<AgentConversationSelectionSnapshot>, String> {
        let coverage = self.coverage(profile, owned)?;
        let Some(connection) = coverage.connection.clone() else { return Ok(None); };
        let (Some(low), Some(high)) = self.bounds(profile, owned)? else {
            let head = coverage.pending_sequence.unwrap_or(0);
            return Ok(Some(AgentConversationSelectionSnapshot {
                connection,
                suspended: coverage.suspended,
                page: AgentConversationItemPage {
                    items: Vec::new(),
                    events: Vec::new(),
                    turns: Vec::new(),
                    before_cursor: None,
                    after_cursor: None,
                    has_before: !coverage.start_complete,
                    has_earlier_transcript: coverage.has_earlier_transcript,
                    has_after: false,
                    watermark: head,
                    transfer_bytes: 0,
                    oversized: false,
                    coverage: None,
                },
                pending_events: coverage.pending_events,
                pending_sequence: head,
                active_turn_id: coverage.active_turn_id,
            }));
        };
        let page = self
            .store
            .list_items_before(
                &Self::key(profile, owned),
                i64::MAX,
                max_bytes,
                Some(EventCoverage {
                    low,
                    high,
                    start_complete: coverage.start_complete,
                }),
            )
            .map_err(|error| error.to_string())?;
        let mut page = super::super::manager::selected_item_page(page)?;
        page.has_before |= !coverage.start_complete;
        page.has_earlier_transcript = coverage.has_earlier_transcript;
        Ok(Some(AgentConversationSelectionSnapshot {
            connection,
            suspended: coverage.suspended,
            page,
            pending_events: coverage.pending_events,
            pending_sequence: coverage.pending_sequence.unwrap_or(high),
            active_turn_id: coverage.active_turn_id,
        }))
    }

    pub fn read_child_selection_snapshot(
        &self,
        profile: &str,
        source_owned_id: &str,
        max_bytes: u32,
    ) -> Result<Option<AgentConversationSelectionSnapshot>, String> {
        let history_owned_id = Self::key(profile, source_owned_id);
        let mut snapshot = self.read_selection_snapshot(profile, source_owned_id, max_bytes)?;
        if let Some(snapshot) = &mut snapshot {
            snapshot.connection.owned_id.clone_from(&history_owned_id);
            for event in &mut snapshot.page.events {
                event.owned_id.clone_from(&history_owned_id);
            }
            for event in &mut snapshot.pending_events {
                event.owned_id.clone_from(&history_owned_id);
            }
        }
        Ok(snapshot)
    }

    pub fn read_item_page(
        &self,
        profile: &str,
        owned: &str,
        cursor: i64,
        bytes: u32,
        before: bool,
    ) -> Result<Option<AgentConversationItemPage>, String> {
        let coverage = self.coverage(profile, owned)?;
        let (Some(low), Some(high)) = self.bounds(profile, owned)? else {
            return Ok(coverage.start_complete.then(|| AgentConversationItemPage {
                items: Vec::new(),
                events: Vec::new(),
                turns: Vec::new(),
                before_cursor: None,
                after_cursor: None,
                has_before: false,
                has_earlier_transcript: coverage.has_earlier_transcript,
                has_after: false,
                watermark: coverage.pending_sequence.unwrap_or(0),
                transfer_bytes: 0,
                oversized: false,
                coverage: None,
            }));
        };
        let start_complete = coverage.start_complete;
        let has_earlier_transcript = coverage.has_earlier_transcript;
        let event_coverage = Some(EventCoverage {
            low,
            high,
            start_complete,
        });
        let page = if before {
            self.store
                .list_items_before(&Self::key(profile, owned), cursor, bytes, event_coverage)
        } else {
            self.store
                .list_items_after(&Self::key(profile, owned), cursor, bytes, event_coverage)
        }
        .map_err(|error| error.to_string())?;
        // Item positions can precede the physical copied range (for example,
        // a tool's firstSequence). Return saved items before deciding to wait.
        if page.items.is_empty() && !start_complete && (before || cursor < low) {
            return Ok(None);
        }
        let mut page = super::super::manager::selected_item_page(page)?;
        page.has_before |= before && !start_complete;
        page.has_earlier_transcript = has_earlier_transcript;
        Ok(Some(page))
    }

    pub fn read_child_item_page(
        &self,
        profile: &str,
        source_owned_id: &str,
        cursor: i64,
        bytes: u32,
        before: bool,
    ) -> Result<Option<AgentConversationItemPage>, String> {
        let history_owned_id = Self::key(profile, source_owned_id);
        let mut page = self.read_item_page(profile, source_owned_id, cursor, bytes, before)?;
        if let Some(page) = &mut page {
            for event in &mut page.events {
                event.owned_id.clone_from(&history_owned_id);
            }
        }
        Ok(page)
    }

    fn decode(rows: Vec<EventRow>) -> Result<Vec<AgentConversationEvent>, String> {
        rows.into_iter()
            .map(|row| serde_json::from_str(&row.payload_json).map_err(|e| e.to_string()))
            .collect()
    }

    pub fn read_page(
        &self,
        profile: &str,
        owned: &str,
        cursor: i64,
        bytes: u32,
        before: bool,
    ) -> Result<Option<AgentConversationEventPage>, String> {
        let coverage = self.coverage(profile, owned)?;
        let (Some(oldest), Some(through)) = self.bounds(profile, owned)? else {
            return Ok(coverage.start_complete.then(|| AgentConversationEventPage { events: Vec::new(), has_more: false }));
        };
        if before
            && (cursor > through.saturating_add(1)
                || (cursor <= oldest && !coverage.start_complete))
            || !before && cursor < oldest
        {
            return Ok(None);
        }
        let key = Self::key(profile, owned);
        let page = if before {
            self.store.list_events_before(&key, cursor, bytes, oldest)
        } else {
            self.store.list_events_after(&key, cursor, bytes, through)
        }
        .map_err(|e| e.to_string())?;
        Ok(Some(AgentConversationEventPage {
            events: Self::decode(page.events)?,
            has_more: page.has_more || (before && !coverage.start_complete),
        }))
    }

    pub fn imported_older(&self, profile: &str, epoch: u64, owned: &str) -> Result<(), String> {
        self.check(profile, epoch)?;
        let mut coverage = self.coverage(profile, owned)?;
        coverage.start_complete = false;
        if let Some(download) = &mut coverage.download { download.complete = false; }
        self.write(profile, owned, &coverage, &mut [])
    }

    #[cfg(test)]
    pub fn page(
        &self,
        profile: &str,
        epoch: u64,
        owned: &str,
        _cursor: i64,
        before: bool,
        page: &mut AgentConversationEventPage,
    ) -> Result<(), String> {
        self.check(profile, epoch)?;
        let mut coverage = self.coverage(profile, owned)?;
        if before && !page.has_more { coverage.start_complete = true; }
        self.write(profile, owned, &coverage, &mut page.events)
    }
}

fn apply_pending_event(pending: &mut Vec<AgentConversationEvent>, event: &AgentConversationEvent) {
    if let AgentConversationPayload::ChildUpdate { child_id, .. } = &event.payload {
        pending.retain(|current| {
            !matches!(
                &current.payload,
                AgentConversationPayload::ChildUpdate {
                    child_id: current_child,
                    ..
                } if current_child == child_id
            )
        });
        pending.push(event.clone());
        return;
    }
    let (request_id, kind, requested) = match &event.payload {
        AgentConversationPayload::Approval {
            request_id,
            state,
            ..
        } => (request_id, "approval", *state == ApprovalState::Requested),
        AgentConversationPayload::UserInputRequested { request_id, .. } => {
            (request_id, "input", true)
        }
        AgentConversationPayload::UserInputResolved { request_id, .. } => {
            (request_id, "input", false)
        }
        _ => return,
    };
    pending.retain(|current| {
        current.generation != event.generation
            || pending_request_key(current).is_none_or(|(current_kind, id)| {
                current_kind != kind || id != request_id
            })
    });
    if requested {
        pending.push(event.clone());
    }
}

fn pending_request_key(event: &AgentConversationEvent) -> Option<(&'static str, &str)> {
    match &event.payload {
        AgentConversationPayload::Approval { request_id, .. } => Some(("approval", request_id)),
        AgentConversationPayload::UserInputRequested { request_id, .. }
        | AgentConversationPayload::UserInputResolved { request_id, .. } => {
            Some(("input", request_id))
        }
        _ => None,
    }
}

fn apply_active_turn(active_turn_id: &mut Option<String>, event: &AgentConversationEvent) {
    let AgentConversationPayload::Turn { turn_id, state } = &event.payload else {
        return;
    };
    match state {
        super::super::protocol::TurnState::Started => active_turn_id.clone_from(&Some(turn_id.clone())),
        super::super::protocol::TurnState::Completed
        | super::super::protocol::TurnState::Interrupted
        | super::super::protocol::TurnState::Failed
            if active_turn_id.as_deref() == Some(turn_id) =>
        {
            *active_turn_id = None;
        }
        _ => {}
    }
}
