//! A project's heading or a session, in the backend's order.

use super::rail_page::RailEntry;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ListEntry {
    /// The heading before a project's first parent in the tab: its label, its repo, and that parent's machine, which
    /// the heading's New session fills in.
    Project {
        label: String,
        repo_url: String,
        machine: Option<String>,
    },
    Session(Box<RailEntry>),
}

impl ListEntry {
    /// What the line is, whatever it shows now: the list finds the line at its top again by it.
    pub fn key(&self) -> &str {
        match self {
            ListEntry::Project { repo_url, .. } => repo_url,
            ListEntry::Session(entry) => &entry.session_id,
        }
    }
}
