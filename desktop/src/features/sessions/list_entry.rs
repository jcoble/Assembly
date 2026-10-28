//! `ListEntry` is one line of the sessions column, in the backend's order: a project's heading, a parent session, one
//! of the expanded parent's helpers, or the line that loads more of them.

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
    Session(RailEntry),
    Helper(RailEntry),
    /// The expanded parent (by id) has more helpers than are loaded, after or before the held ones.
    MoreHelpers(String),
    EarlierHelpers(String),
}

impl ListEntry {
    /// What the line is, whatever it shows now: the list finds the line at its top again by it.
    pub fn key(&self) -> &str {
        match self {
            ListEntry::Project { repo_url, .. } => repo_url,
            ListEntry::Session(entry) | ListEntry::Helper(entry) => &entry.session_id,
            ListEntry::MoreHelpers(parent) | ListEntry::EarlierHelpers(parent) => parent,
        }
    }
}
