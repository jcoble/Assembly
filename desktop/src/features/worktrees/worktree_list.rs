//! `WorktreeList` is one read of a project's worktrees: the rows the filter leaves, how many it hides (scm#5), and
//! the header's summary. Replaces worktreeManagerRows.ts's filterWorktreeRows and summarizeWorktreeManager.

use super::worktree::Worktree;

#[derive(Clone, Debug, Default, PartialEq)]
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

    /// "3 worktrees: 1 main checkout, 1 safe to remove, 1 needs a decision".
    pub fn summary(&self) -> String {
        let total = self.worktrees.len();
        if total == 0 {
            return "No worktrees".into();
        }
        let main = self.worktrees.iter().filter(|w| w.is_main).count();
        let safe = self.worktrees.iter().filter(|w| w.safe_to_remove()).count();
        let deciding = total - main - safe;
        let mut parts = Vec::new();
        if main > 0 {
            parts.push(format!("{main} main checkout"));
        }
        if safe > 0 {
            parts.push(format!("{safe} safe to remove"));
        }
        if deciding > 0 {
            let verb = if deciding == 1 { "needs" } else { "need" };
            parts.push(format!("{deciding} {verb} a decision"));
        }
        let head = format!(
            "{total} {}",
            if total == 1 { "worktree" } else { "worktrees" }
        );
        format!("{head}: {}", parts.join(", "))
    }
}
