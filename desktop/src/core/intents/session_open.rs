//! One source-qualified open action. Shape checks are not authorization: the owning backend resolves the target.
use crate::core::engine::{EngineRefusal, SessionKey};
use gpui_kit::Action;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SessionOpenTarget {
    Engine,
    /// Key is the parent record reference. Only the backend may resolve this persisted row to a readable target.
    Native {
        parent: SessionKey,
        item_id: String,
        seq: i64,
        payload_session_id: String,
    },
    Historical,
}

#[derive(Clone, Debug, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct SessionOpenRequest {
    pub key: SessionKey,
    pub request_id: String,
    pub target: SessionOpenTarget,
}

/// Starts the request id of an open a link made.
pub const LINK_REQUEST: &str = "link:";
/// Starts the request id of an open a reconnect made.
pub const RECONNECT_REQUEST: &str = "reconnect:";

#[derive(Clone, Debug)]
pub struct SessionOpenResult {
    pub request_id: String,
    /// Original requested key when known, not descriptor proof; None only for pre-discovery refusal/cancel.
    pub key: Option<SessionKey>,
    pub result: Result<(), EngineRefusal>,
}

impl SessionOpenResult {
    /// The answer to an open someone asked for. One that was cancelled no longer stands: what replaced it answers too.
    pub fn is_from_a_person(&self) -> bool {
        !self.request_id.starts_with(LINK_REQUEST)
            && !self.request_id.starts_with(RECONNECT_REQUEST)
            && !matches!(&self.result, Err(refusal) if refusal.code == "cancelled")
    }
}

impl SessionOpenRequest {
    pub fn validate_shape(&self) -> Result<(), EngineRefusal> {
        let valid = !self.request_id.is_empty()
            && !self.key.machine.is_empty()
            && !self.key.id.is_empty()
            && match &self.target {
                SessionOpenTarget::Native {
                    parent,
                    item_id,
                    seq,
                    ..
                } => parent == &self.key && !item_id.is_empty() && *seq > 0,
                SessionOpenTarget::Engine | SessionOpenTarget::Historical => true,
            };
        if valid {
            Ok(())
        } else {
            Err(EngineRefusal {
                code: "invalid_open_target".into(),
                message:
                    "The session open reference is missing or contradicts its recorded parent."
                        .into(),
            })
        }
    }
}
