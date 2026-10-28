//! `list_entries` lays the loaded sessions out as the list draws them: Working, Done, then Settled, each with its
//! projects A to Z and each project's sessions newest first. Working or Done with no sessions is left out; Settled's
//! header is always there once anything is, since its sessions load only once it's opened, and folded it keeps only
//! its header. Replaces the old rail's `buildMyWorkGroups` (myWorkViewOptions.ts) with status and project
//! grouping both on, and without pinning, orchestrator or workflow sections.

use std::collections::BTreeMap;
use std::ops::Range;

use super::list_entry::ListEntry;
use super::status::Status;
use crate::core::state::session_item::SessionItem;

/// `items` is newest first, as `Sessions` holds them.
pub fn list_entries(items: &[SessionItem], settled_open: bool) -> Vec<ListEntry> {
    let mut entries = Vec::new();
    for status in Status::ALL {
        // Keyed by the label first so projects read A to Z; the machine and folder keep two that share a label apart.
        let mut projects: BTreeMap<(String, Option<String>, String), Vec<String>> = BTreeMap::new();
        for item in items {
            if Status::of(item) == status {
                let key = (
                    project_label(&item.repo_url).to_lowercase(),
                    item.machine.clone().filter(|machine| !machine.is_empty()),
                    item.repo_url.clone(),
                );
                projects
                    .entry(key)
                    .or_default()
                    .push(item.session_id.clone());
            }
        }
        let open = status != Status::Settled || settled_open;
        if projects.is_empty() {
            if status == Status::Settled && !items.is_empty() {
                entries.push(ListEntry::Group { status, open });
            }
            continue;
        }
        entries.push(ListEntry::Group { status, open });
        if !open {
            continue;
        }
        for ((_, machine, folder), sessions) in projects {
            entries.push(ListEntry::Project {
                status,
                folder,
                machine,
            });
            entries.extend(sessions.into_iter().map(ListEntry::Session));
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
/// there.
pub fn same_line(
    old: &[ListEntry],
    new: &[ListEntry],
    changed: &Range<usize>,
    at: usize,
) -> Option<usize> {
    let line = old.get(at).filter(|_| changed.contains(&at))?;
    new.iter().position(|entry| entry == line)
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
