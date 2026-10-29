//! A project's heading or a session, in the backend's order.

use super::rail_page::RailEntry;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ListEntry {
    Section {
        key: String,
        fold_key: String,
        label: String,
        count: usize,
        folded: bool,
    },
    Project {
        key: String,
        fold_key: String,
        label: String,
        repo_url: String,
        machines: Vec<String>,
        count: usize,
        folded: bool,
    },
    Session(Box<RailEntry>),
}

impl ListEntry {
    /// What the line is, whatever it shows now: the list finds the line at its top again by it.
    pub fn key(&self) -> &str {
        match self {
            ListEntry::Section { key, .. } | ListEntry::Project { key, .. } => key,
            ListEntry::Session(entry) => &entry.session_id,
        }
    }
}
