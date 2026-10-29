//! `SubagentPayload` is a subagent the agent runs as a child session with its own session id, nested under the tool
//! call that started it when known. Opening it is the "open session" intent (lane g0-host).

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
