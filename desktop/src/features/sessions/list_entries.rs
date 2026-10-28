//! `list_entries` lays the rail's answer out as the column draws it, in the backend's order: a project's heading where
//! the backend marked a project's first parent, each parent, and under the expanded one its helpers. Nothing here
//! groups, sorts or counts: the backend's one read (`sessions/rail`) did.

use std::ops::Range;

use super::list_entry::ListEntry;
use super::rail_page::Rail;

pub fn list_entries(rail: &Rail) -> Vec<ListEntry> {
    let mut entries = Vec::new();
    for entry in &rail.sessions {
        if entry.group_start {
            entries.push(ListEntry::Project {
                label: entry.group.clone(),
                repo_url: entry.repo_url.clone(),
                machine: entry.machine.clone(),
            });
        }
        entries.push(ListEntry::Session(entry.clone()));
        if rail.expanded.as_deref() == Some(entry.session_id.as_str()) {
            entries.extend(rail.helpers.iter().cloned().map(ListEntry::Helper));
            if rail.has_more_helpers() {
                entries.push(ListEntry::MoreHelpers(entry.session_id.clone()));
            }
        }
    }
    entries
}

/// Which lines changed from `old` to `new`: the old range to replace and how many new lines go in its place. Only
/// what lies between the lines both share at the start and at the end counts, so the list keeps its place when a
/// change is below or above what's in view.
pub fn changed_lines(old: &[ListEntry], new: &[ListEntry]) -> (Range<usize>, usize) {
    let start = old.iter().zip(new).take_while(|(a, b)| a == b).count();
    let end = old[start..]
        .iter()
        .rev()
        .zip(new[start..].iter().rev())
        .take_while(|(a, b)| a == b)
        .count();
    (start..old.len() - end, new.len() - start - end)
}

/// Where line `at` of `old` is in `new`, when it lies in the `changed` lines (a splice would lose it) and is still
/// there, by its key: a row whose state changed is still the line it was.
pub fn same_line(
    old: &[ListEntry],
    new: &[ListEntry],
    changed: &Range<usize>,
    at: usize,
) -> Option<usize> {
    let line = old.get(at).filter(|_| changed.contains(&at))?;
    new.iter().position(|entry| entry.key() == line.key())
}

/// What a project's header calls it: "owner/repo" for a GitHub URL, else the folder's last name.
pub fn project_label(folder: &str) -> &str {
    if let Some(repo) = folder.strip_prefix("https://github.com/") {
        return repo.trim_end_matches('/');
    }
    let trimmed = folder.trim_end_matches('/');
    match trimmed.rsplit('/').next() {
        Some(name) if !name.is_empty() => name,
        _ => folder,
    }
}

#[cfg(test)]
#[path = "list_entries_tests.rs"]
mod tests;
