//! Typed views of actual Engine display rows. Missing presentation stays unavailable;
//! canonical answer bytes and transport provenance are never reconstructed by the UI.
use super::{EngineItem, EngineKey, SessionKey};
use crate::core::ui::ask::presentation::NeedPresentation;
use serde::Deserialize;
use serde_json::Value;

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandOrigin {
    pub channel: String,
    pub device_id: Option<String>,
    pub surface: Option<String>,
}

#[derive(Clone, Debug, PartialEq)]
pub enum NeedAnswerDisplay {
    Pending,
    Complete { answer: Value, text: Option<String> },
    Omitted { utf8_bytes: u64 },
    Unavailable,
}

#[derive(Clone, Debug)]
pub struct NeedItem {
    pub key: EngineKey,
    pub version: i64,
    pub session: SessionKey,
    pub run_id: Option<String>,
    pub step_id: Option<String>,
    pub addressee_session_id: Option<String>,
    pub addressee_machine: Option<String>,
    pub chat_row_id: Option<String>,
    pub kind: String,
    pub text: Option<String>,
    pub display_number: Option<i64>,
    pub presentation: Option<NeedPresentation>,
    pub state: String,
    pub answer: Option<Value>,
    pub answer_text: Option<String>,
    pub answer_complete: Option<bool>,
    pub answer_utf8_bytes: Option<u64>,
    pub answered_by: Option<String>,
    pub answered_ms: Option<i64>,
    pub answered_origin: Option<CommandOrigin>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct NeedFields {
    session_id: String,
    run_id: Option<String>,
    step_id: Option<String>,
    addressee_session_id: Option<String>,
    addressee_machine: Option<String>,
    chat_row_id: Option<String>,
    kind: String,
    text: Option<String>,
    display_number: Option<i64>,
    state: String,
    answer: Option<Value>,
    answer_text: Option<String>,
    answer_complete: Option<bool>,
    answer_utf8_bytes: Option<u64>,
    answered_by: Option<String>,
    answered_ms: Option<i64>,
    answered_origin: Option<CommandOrigin>,
}
impl NeedItem {
    pub fn addressee(&self) -> Option<SessionKey> {
        let machine = self.addressee_machine.as_ref().filter(|value| !value.is_empty())?;
        let id = self.addressee_session_id.as_ref().filter(|value| !value.is_empty())?;
        Some(SessionKey { machine: machine.clone(), id: id.clone() })
    }

    pub fn of(item: &EngineItem) -> Option<Self> {
        if item.key.kind != "needs_me"
            || item.value["entityKind"] != "needs_me"
            || item.value["machine"].as_str() != Some(item.key.machine.as_str())
            || item.value["id"].as_str() != Some(item.key.id.as_str())
            || item.value["version"].as_i64() != Some(item.version)
        {
            return None;
        }
        let row: NeedFields = serde_json::from_value(item.value.clone()).ok()?;
        Some(Self {
            key: item.key.clone(),
            version: item.version,
            session: SessionKey {
                machine: item.key.machine.clone(),
                id: row.session_id,
            },
            run_id: row.run_id,
            step_id: row.step_id,
            addressee_session_id: row.addressee_session_id,
            addressee_machine: row.addressee_machine,
            chat_row_id: row.chat_row_id,
            kind: row.kind,
            text: row.text,
            display_number: row.display_number,
            presentation: serde_json::from_value(item.value.clone()).ok(),
            state: row.state,
            answer: row.answer,
            answer_text: row.answer_text,
            answer_complete: row.answer_complete,
            answer_utf8_bytes: row.answer_utf8_bytes,
            answered_by: row.answered_by,
            answered_ms: row.answered_ms,
            answered_origin: row.answered_origin,
        })
    }
    pub fn presentation_notice(&self) -> Option<&'static str> {
        self.presentation
            .as_ref()
            .filter(|p| {
                matches!(
                    p.input_mode.as_str(),
                    "permission" | "form" | "text" | "choice"
                )
            })
            .is_none()
            .then_some("Answer controls are unavailable for this item.")
    }
    pub fn answer_display(&self) -> NeedAnswerDisplay {
        match (
            self.answer_complete,
            self.answer.as_ref(),
            self.answer_utf8_bytes,
        ) {
            (Some(true), Some(answer), _) => NeedAnswerDisplay::Complete {
                answer: answer.clone(),
                text: self.answer_text.clone(),
            },
            (Some(false), _, Some(utf8_bytes)) => NeedAnswerDisplay::Omitted { utf8_bytes },
            (None, None, _) if self.state == "open" => NeedAnswerDisplay::Pending,
            _ => NeedAnswerDisplay::Unavailable,
        }
    }
    pub fn answer_notice(&self) -> Option<String> {
        match self.answer_display() {
            NeedAnswerDisplay::Omitted { utf8_bytes } => Some(format!(
                "Answer exceeds the display limit ({utf8_bytes} bytes)."
            )),
            NeedAnswerDisplay::Unavailable if self.state == "answered" => {
                Some("Answer details are unavailable.".into())
            }
            _ => None,
        }
    }
}

/// An Engine-owned session display, distinct from persisted provider-native cards.
#[derive(Clone, Debug)]
pub struct SessionItem {
    pub key: EngineKey,
    pub version: i64,
    pub parent_id: Option<String>,
    pub parent_machine: Option<String>,
    pub run_id: Option<String>,
    pub title: Option<String>,
    pub provider: String,
    pub model: Option<String>,
    /// Recorded owner working directory; this is display data, never a filesystem root grant.
    pub folder: String,
    pub state: String,
    pub activity_state: String,
    pub epoch: i64,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SessionFields {
    parent_id: Option<String>,
    parent_machine: Option<String>,
    run_id: Option<String>,
    title: Option<String>,
    provider: String,
    model: Option<String>,
    folder: String,
    state: String,
    activity_state: String,
    epoch: i64,
}
impl SessionItem {
    pub fn of(item: &EngineItem) -> Option<Self> {
        if item.key.kind != "session"
            || item.value["entityKind"] != "session"
            || item.value["machine"].as_str() != Some(item.key.machine.as_str())
            || item.value["id"].as_str() != Some(item.key.id.as_str())
            || item.value["version"].as_i64() != Some(item.version)
        {
            return None;
        }
        let row: SessionFields = serde_json::from_value(item.value.clone()).ok()?;
        Some(Self {
            key: item.key.clone(),
            version: item.version,
            parent_id: row.parent_id,
            parent_machine: row.parent_machine,
            run_id: row.run_id,
            title: row.title,
            provider: row.provider,
            model: row.model,
            folder: row.folder,
            state: row.state,
            activity_state: row.activity_state,
            epoch: row.epoch,
        })
    }
    pub fn session_key(&self) -> SessionKey {
        SessionKey {
            machine: self.key.machine.clone(),
            id: self.key.id.clone(),
        }
    }
}
