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
    /// The run going now, from the session's summary; `None` while it's idle.
    pub active_run_id: Option<String>,
    /// The summary's state: "running", "waiting" (an open ask), "failed" (the last turn) or "idle".
    #[serde(default)]
    pub state: String,
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
    /// The session's title, or its repo or folder when it has none yet ("owner/repo" for a GitHub URL).
    pub fn title(&self) -> String {
        match self
            .title
            .as_deref()
            .map(str::trim)
            .filter(|title| !title.is_empty())
        {
            Some(title) => title.to_string(),
            None => self
                .repo_url
                .strip_prefix("https://github.com/")
                .unwrap_or(&self.repo_url)
                .to_string(),
        }
    }

    /// Its agent, and its machine when it isn't this computer: "claude · Workbox Test".
    pub fn place(&self) -> String {
        match &self.machine {
            Some(machine) if !machine.is_empty() => format!("{} · {machine}", self.agent),
            _ => self.agent.clone(),
        }
    }
}

#[cfg(test)]
#[path = "session_item_tests.rs"]
mod tests;
