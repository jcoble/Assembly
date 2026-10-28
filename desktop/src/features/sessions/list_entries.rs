//! `list_entries` lays the loaded sessions out as the list draws them: Working, Done, then Settled, each with its
//! projects A to Z and each project's sessions newest first. A group with no sessions is left out; a folded Settled
//! keeps only its header. Replaces the old rail's `buildMyWorkGroups` (myWorkViewOptions.ts) with status and project
//! grouping both on, and without pinning, orchestrator or workflow sections.

use std::collections::BTreeMap;

use super::list_entry::ListEntry;
use super::status::Status;
use crate::core::state::session_item::SessionItem;

/// `items` is newest first, as `Sessions` holds them.
pub fn list_entries(items: &[SessionItem], settled_open: bool) -> Vec<ListEntry> {
    let mut entries = Vec::new();
    for status in Status::ALL {
        // Keyed by the label first so projects read A to Z; the machine and folder keep two that share a label apart.
        let mut projects: BTreeMap<(String, Option<String>, String), Vec<usize>> = BTreeMap::new();
        for (at, item) in items.iter().enumerate() {
            if Status::of(item) == status {
                let key = (
                    project_label(&item.repo_url).to_lowercase(),
                    item.machine.clone().filter(|machine| !machine.is_empty()),
                    item.repo_url.clone(),
                );
                projects.entry(key).or_default().push(at);
            }
        }
        if projects.is_empty() {
            continue;
        }
        let open = status != Status::Settled || settled_open;
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
