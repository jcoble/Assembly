//! `WorktreeList` is one read of a project's worktrees: the rows the filter leaves, how many it hides (scm#5), and
//! nothing else. Replaces worktreeManagerRows.ts's filterWorktreeRows.

use serde::Deserialize;

use super::worktree::Worktree;

/// Deserialized straight from `worktrees/list`'s `{worktrees}`.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
pub struct WorktreeList {
    /// In git's order, the main checkout first.
    pub worktrees: Vec<Worktree>,
}

impl WorktreeList {
    /// The worktrees whose branch or folder has `filter` in it, ignoring case; all of them for an empty filter.
    pub fn shown(&self, filter: &str) -> Vec<&Worktree> {
        let needle = filter.trim().to_lowercase();
        self.worktrees
            .iter()
            .filter(|worktree| {
                needle.is_empty()
                    || format!("{} {}", worktree.name(), worktree.path)
                        .to_lowercase()
                        .contains(&needle)
            })
            .collect()
    }

    /// What the filter hides, said as a line under the list; `None` when it hides nothing.
    pub fn hidden_line(&self, filter: &str) -> Option<String> {
        match self.worktrees.len() - self.shown(filter).len() {
            0 => None,
            1 => Some("1 worktree is hidden by the filter.".into()),
            hidden => Some(format!("{hidden} worktrees are hidden by the filter.")),
        }
    }
}
