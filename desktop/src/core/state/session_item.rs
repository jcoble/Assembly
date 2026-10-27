//! `SessionItem` is one session as the `sessions` topic sends it
//! (dotnet/Assembly.Application/Features/Chat/Sessions/SessionItem.cs).

use serde::Deserialize;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionItem {
    pub session_id: String,
    pub agent: String,
    pub repo_url: String,
    pub title: Option<String>,
    pub machine: Option<String>,
    /// Set on a helper session: it isn't on the sessions list.
    pub parent_session_id: Option<String>,
    pub acp_session_id: Option<String>,
    pub updated_at: i64,
}
