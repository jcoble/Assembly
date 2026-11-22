//! `SessionItem` is one session as the `sessions` topic sends it
//! (dotnet/Assembly.Application/Features/Chat/Sessions/SessionItem.cs).

use crate::core::ui::provider_name::provider_name;
use serde::Deserialize;

/// What a session in no project is filed under.
pub const NO_PROJECT: &str = "No project";

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionItem {
    pub session_id: String,
    pub agent: String,
    pub repo_url: String,
    /// The project it was started in; older repository sessions may also have no project id.
    #[serde(default)]
    pub project_id: Option<String>,
    /// Classified by the owning backend, which knows its actual home.
    #[serde(default)]
    pub is_bare: bool,
    /// Existing owning-machine paths; a remote root is never resolved on this Mac.
    pub root: Option<String>,
    pub checkout_root: Option<String>,
    pub title: Option<String>,
    pub machine: Option<String>,
    /// Set on a started session (on the list) and a native subagent (not on it).
    pub parent_session_id: Option<String>,
    pub acp_session_id: Option<String>,
    pub updated_at: i64,
    /// The run going now, from the session's summary; `None` while it's idle.
    pub active_run_id: Option<String>,
    /// The summary's state: "running", "waiting" (an open ask), "failed" (the last turn) or "idle".
    #[serde(default)]
    pub state: String,
    /// When the earliest still-open ask opened (Unix ms) while `state` is `"waiting"`.
    #[serde(default)]
    pub state_since: Option<i64>,
    /// A turn ended past what a window has shown.
    #[serde(default)]
    pub unread: bool,
    /// When the user first marked it done, and when they settled it (Unix ms); both `None` while it's working.
    #[serde(default)]
    pub completed_at: Option<i64>,
    #[serde(default)]
    pub settled_at: Option<i64>,
}

impl SessionItem {
    /// The session's title, or its repo/folder; bare sessions use the untitled-session label.
    pub fn title(&self) -> String {
        match self
            .title
            .as_deref()
            .map(str::trim)
            .filter(|title| !title.is_empty())
        {
            Some(title) => title.to_string(),
            None if self.is_bare => "Untitled session".to_string(),
            None => self
                .repo_url
                .strip_prefix("https://github.com/")
                .unwrap_or(&self.repo_url)
                .to_string(),
        }
    }

    /// Its provider as people read it, and its machine when it isn't this computer: "Claude · Workbox Test".
    pub fn place(&self) -> String {
        let provider = provider_name(&self.agent);
        match &self.machine {
            Some(machine) if !machine.is_empty() => format!("{provider} · {machine}"),
            _ => provider,
        }
    }
}

#[cfg(test)]
#[path = "session_item_tests.rs"]
mod tests;
