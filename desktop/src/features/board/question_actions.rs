//! The one question action that isn't an answer: asking the asker to explain. The need stays open.
use serde_json::json;
use uuid::Uuid;

use crate::core::engine::EngineRefusal;
use crate::core::engine::commands::WindowCommand;
use crate::core::engine::engine_items::{NeedItem, SessionItem};

/// A new message to the session that asked, naming the exact need. It answers nothing.
pub fn explain_more(need: &NeedItem, asker: &SessionItem) -> Result<WindowCommand, EngineRefusal> {
    if asker.key.machine != need.session.machine || asker.key.id != need.session.id {
        return Err(EngineRefusal {
            code: "session_unavailable".into(),
            message: "The session that asked is not available.".into(),
        });
    }
    Ok(WindowCommand {
        command_id: Uuid::new_v4().to_string(),
        entity_id: Uuid::new_v4().to_string(),
        expected_version: 0,
        epoch: asker.epoch,
        machine: Some(asker.key.machine.clone()),
        surface: None,
        command: json!({
            "kind": "send_message",
            "fromSessionId": null,
            "toSessionId": asker.key.id,
            "text": format!("Please explain your question {} more.", need.key.id),
            "mode": "prompt",
        }),
    })
}

#[cfg(test)]
#[path = "question_actions_tests.rs"]
mod tests;
