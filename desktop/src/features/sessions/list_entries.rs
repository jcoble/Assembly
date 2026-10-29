//! `list_entries` lays the rail's explicit section, project and session entries out in the backend's order. Nothing here
//! groups, sorts or counts: the backend's one read (`sessions/rail`) did.

use std::ops::Range;

use super::list_entry::ListEntry;
use super::rail_page::Rail;

pub fn list_entries(rail: &Rail) -> Vec<ListEntry> {
    rail.sessions
        .iter()
        .map(|entry| match entry.kind.as_str() {
            "section" => ListEntry::Section {
                key: entry.session_id.clone(),
                fold_key: entry.section.clone(),
                label: entry.group.clone(),
                count: entry.count,
                folded: rail.folded.contains(&entry.section),
            },
            "project" => {
                let fold_key = entry
                    .session_id
                    .strip_prefix("project:")
                    .unwrap_or(&entry.session_id)
                    .to_owned();
                ListEntry::Project {
                    key: entry.session_id.clone(),
                    folded: rail.folded.contains(&fold_key),
                    fold_key,
                    label: entry.group.clone(),
                    count: entry.count,
                    machines: entry.machines.clone(),
                    repo_url: entry.repo_url.clone(),
                }
            }
            _ => ListEntry::Session(Box::new(entry.clone())),
        })
        .collect()
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

/// Where line `at` of `old` is in `new`, when it lies in the `changed` lines or new lines go in just before it (a
/// splice would lose it, or leave an unscrolled list above it) and is still there, by its key: a row whose state
/// changed is still the line it was. The kind distinguishes a heading from a session.
pub fn same_line(
    old: &[ListEntry],
    new: &[ListEntry],
    changed: &Range<usize>,
    at: usize,
) -> Option<usize> {
    let line = old
        .get(at)
        .filter(|_| changed.start == at || changed.contains(&at))?;
    let kind = std::mem::discriminant(line);
    new.iter()
        .position(|entry| std::mem::discriminant(entry) == kind && entry.key() == line.key())
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
