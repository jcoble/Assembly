//! One-time cutover importer. Delete this module after the SQLite migration window closes.

use std::fs;
use std::path::Path;

use mcb_core::session_store::{EventRow, SessionRow, SessionStore};
use serde_json::Value;

use super::super::protocol::{AgentConversationEvent, AgentEvent};

const OWNED_SESSIONS_FILE: &str = "legacy-owned-sessions.json";
const EVENT_JOURNAL_FILE: &str = "legacy-agent-events.jsonl";
const EVENT_CAP: u32 = 10_000;

pub fn import_if_store_empty(store: &SessionStore, directory: &Path) -> Result<usize, String> {
    if !store
        .list_sessions()
        .map_err(|error| error.to_string())?
        .is_empty()
    {
        return Ok(0);
    }

    let owned_path = directory.join(OWNED_SESSIONS_FILE);
    let events_path = directory.join(EVENT_JOURNAL_FILE);
    if !owned_path.exists() && !events_path.exists() {
        return Ok(0);
    }

    let now = now_ms();
    let owned = if owned_path.exists() {
        serde_json::from_slice::<Vec<Value>>(
            &fs::read(&owned_path)
                .map_err(|error| format!("Could not read {}: {error}", owned_path.display()))?,
        )
        .map_err(|error| format!("Could not decode {}: {error}", owned_path.display()))?
    } else {
        Vec::new()
    };

    for value in &owned {
        let owned_id = required_string(value, &["ownedId", "owned_id"])?;
        let provider = optional_string(value, &["provider", "agent"])
            .unwrap_or_else(|| "codex".to_string())
            .to_ascii_lowercase();
        let cwd = optional_string(value, &["cwd", "projectPath", "project_path"])
            .unwrap_or_else(|| "/".to_string());
        let native_session_id = optional_string(
            value,
            &["nativeSessionId", "native_session_id", "sessionId"],
        );
        let state = if value
            .get("completedAt")
            .or_else(|| value.get("completed_at"))
            .is_some_and(|value| !value.is_null())
        {
            "closed"
        } else {
            "suspended"
        };
        store
            .upsert_session(&SessionRow {
                owned_id,
                native_session_id,
                provider: provider.clone(),
                model: optional_string(value, &["model"]),
                effort: optional_string(value, &["reasoningEffort", "reasoning_effort"]),
                cwd: cwd.clone(),
                worktree: optional_string(value, &["worktreePath", "worktree_path"]).or(Some(cwd)),
                branch: optional_string(value, &["branch"]),
                title: optional_string(value, &["title"]),
                title_source: None,
                project: optional_string(value, &["project", "projectName"]),
                project_id: None,
                state: state.to_string(),
                suspended: state == "suspended",
                created_at_ms: now,
                last_activity_at_ms: now,
                extra_json: serde_json::json!({
                    "generation": 1,
                    "nativeSessionMode": "resume",
                    "owner": "structured",
                    "config": {},
                    "capabilities": legacy_capabilities(&provider),
                })
                .to_string(),
            })
            .map_err(|error| error.to_string())?;
    }

    if events_path.exists() {
        let contents = fs::read_to_string(&events_path)
            .map_err(|error| format!("Could not read {}: {error}", events_path.display()))?;
        for line in contents.lines().filter(|line| !line.trim().is_empty()) {
            let canonical: AgentEvent = serde_json::from_str(line)
                .map_err(|error| format!("Could not decode {}: {error}", events_path.display()))?;
            let frontend = AgentConversationEvent {
                runtime_state: None,
                owned_id: canonical.owned_id.clone(),
                provider: canonical.provider,
                generation: canonical.generation,
                sequence: canonical.sequence,
                timestamp_ms: canonical.timestamp_ms,
                turn_id: canonical.turn_id.clone(),
                payload: super::super::normalization::frontend_payload_from_canonical(&canonical)?,
            };
            let event_type = serde_json::to_value(canonical.event_type)
                .ok()
                .and_then(|value| value.as_str().map(str::to_string))
                .unwrap_or_else(|| "runtime.warning".to_string());
            store
                .append_event(&EventRow {
                    owned_id: canonical.owned_id.clone(),
                    seq: canonical.sequence as i64,
                    turn_id: canonical.turn_id,
                    kind: event_type,
                    payload_json: serde_json::to_string(&frontend).map_err(|error| {
                        format!("Could not encode imported conversation event: {error}")
                    })?,
                    created_at_ms: i64::try_from(canonical.timestamp_ms).unwrap_or(i64::MAX),
                })
                .map_err(|error| error.to_string())?;
            store
                .enforce_event_cap(&canonical.owned_id, EVENT_CAP)
                .map_err(|error| error.to_string())?;
        }
    }

    Ok(owned.len())
}

fn required_string(value: &Value, names: &[&str]) -> Result<String, String> {
    optional_string(value, names).ok_or_else(|| format!("Legacy session is missing {}", names[0]))
}

fn optional_string(value: &Value, names: &[&str]) -> Option<String> {
    names.iter().find_map(|name| {
        value
            .get(*name)
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(str::to_string)
    })
}

fn legacy_capabilities(provider: &str) -> Value {
    serde_json::json!({
        "revision": 1,
        "provider": provider,
        "implementation": { "name": "legacy import", "version": "1" },
        "session": { "list": true, "load": true, "resume": true, "close": true, "steering": false },
        "prompt": { "text": true, "image": false, "embeddedContext": false, "resourceLinks": false },
        "interaction": { "permissions": false, "structuredUserInput": false, "toolTerminals": false, "plans": false, "tasks": false, "subagents": false },
        "configOptions": [],
        "commands": [],
    })
}

fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|duration| i64::try_from(duration.as_millis()).unwrap_or(i64::MAX))
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;

    use super::*;
    use crate::agent_conversation::protocol::{
        AgentConversationPayload, AgentConversationProvider, AgentEventType,
    };

    #[test]
    fn importer_roundtrip_from_legacy_fixtures() {
        let root = std::env::temp_dir().join(format!("mcb-legacy-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&root).unwrap();
        fs::write(
            root.join(OWNED_SESSIONS_FILE),
            serde_json::to_vec(&serde_json::json!([{
                "ownedId": "owned-legacy",
                "provider": "codex",
                "cwd": root,
                "nativeSessionId": "native-legacy",
                "title": "Imported"
            }]))
            .unwrap(),
        )
        .unwrap();
        let payload = AgentConversationPayload::Error {
            code: "legacy-warning".into(),
            message: "fixture".into(),
            recoverable: true,
        };
        let payload = serde_json::to_value(payload)
            .unwrap()
            .as_object()
            .unwrap()
            .iter()
            .map(|(key, value)| (key.clone(), value.clone()))
            .collect::<BTreeMap<_, _>>();
        let event = AgentEvent {
            event_type: AgentEventType::RuntimeError,
            owned_id: "owned-legacy".into(),
            provider: AgentConversationProvider::Codex,
            provider_instance_id: "legacy-fixture".into(),
            generation: 1,
            sequence: 7,
            timestamp_ms: 42,
            native_session_id: Some("native-legacy".into()),
            turn_id: None,
            item_id: None,
            request_id: None,
            payload,
            provider_metadata: None,
            raw_frame_reference: None,
        };
        fs::write(
            root.join(EVENT_JOURNAL_FILE),
            format!("{}\n", serde_json::to_string(&event).unwrap()),
        )
        .unwrap();

        let store = SessionStore::open(&root.join("sessions.db")).unwrap();
        assert_eq!(import_if_store_empty(&store, &root).unwrap(), 1);
        let session = store.get_session("owned-legacy").unwrap().unwrap();
        assert_eq!(session.native_session_id.as_deref(), Some("native-legacy"));
        assert_eq!(session.state, "suspended");
        let events = store.list_events("owned-legacy", 0, 10).unwrap();
        assert_eq!(events.len(), 1);
        let restored: AgentConversationEvent =
            serde_json::from_str(&events[0].payload_json).unwrap();
        assert!(matches!(
            restored.payload,
            AgentConversationPayload::Error { ref code, .. } if code == "legacy-warning"
        ));
        assert_eq!(import_if_store_empty(&store, &root).unwrap(), 0);
        drop(store);
        fs::remove_dir_all(root).unwrap();
    }
}
