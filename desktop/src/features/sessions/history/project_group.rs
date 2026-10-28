//! `ProjectGroup` is one project in the History tab: its folder or repo, its machine when it isn't this computer, and
//! its loaded sessions newest first, copied, so a redraw finds each row's session without a search. Every session has its own checkout, so there's no worktree level under it.

use crate::core::state::session_item::SessionItem;
use crate::features::sessions::list_entries::project_label;

#[derive(Clone, Debug)]
pub struct ProjectGroup {
    pub folder: String,
    pub machine: Option<String>,
    /// Newest first.
    pub sessions: Vec<SessionItem>,
}

impl ProjectGroup {
    /// The projects of `items` (newest first, as `Sessions` holds them), the one with the newest session first.
    pub fn all(items: &[SessionItem]) -> Vec<ProjectGroup> {
        let mut groups: Vec<ProjectGroup> = Vec::new();
        for item in items {
            let machine = item.machine.clone().filter(|machine| !machine.is_empty());
            match groups
                .iter_mut()
                .find(|group| group.folder == item.repo_url && group.machine == machine)
            {
                Some(group) => group.sessions.push(item.clone()),
                None => groups.push(ProjectGroup {
                    folder: item.repo_url.clone(),
                    machine,
                    sessions: vec![item.clone()],
                }),
            }
        }
        groups
    }

    /// Its header's name, as the sessions column's project header says it: "owner/repo · Workbox Test".
    pub fn label(&self) -> String {
        match &self.machine {
            Some(machine) => format!("{} · {machine}", project_label(&self.folder)),
            None => project_label(&self.folder).to_string(),
        }
    }

    /// What its fold and its rows shown are kept under.
    pub fn key(&self) -> (Option<String>, String) {
        (self.machine.clone(), self.folder.clone())
    }
}

#[cfg(test)]
#[path = "project_group_tests.rs"]
mod tests;
