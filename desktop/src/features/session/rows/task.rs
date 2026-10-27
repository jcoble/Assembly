//! `TaskPayload` is background work: a backgrounded command or another task the agent runs past its turn. One that
//! can stop is stopped through `send/stopTask`.

use serde::Deserialize;

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskPayload {
    pub name: String,
    /// running, completed, failed or stopped.
    pub state: String,
    pub can_stop: bool,
    pub tool_call_id: Option<String>,
}
