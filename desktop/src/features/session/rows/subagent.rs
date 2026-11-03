//! Persisted subagent display payload. For native cards SessionId is the recorded parent correlation;
//! the immutable owner row item supplies the provider-child Stop target. History identity is resolved by the owner.

use serde::Deserialize;

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SubagentPayload {
    pub session_id: String,
    pub title: String,
    pub description: Option<String>,
    /// running, requires_action or idle.
    pub state: String,
    pub stop_reason: Option<String>,
    pub error: Option<String>,
    pub can_stop: bool,
    pub tool_call_id: Option<String>,
    #[serde(default)]
    pub helper: bool,
}
