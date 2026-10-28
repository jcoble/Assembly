//! `Worktree` is one worktree as `worktrees/list` answers it, with what its row says about it: its name, and the
//! short marks for what would be lost if it went (scm#1, scm#2). Replaces worktreeManagerRows.ts's row and chips.

use chrono::{Local, TimeZone};
use serde::Deserialize;

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Worktree {
    pub path: String,
    /// `None` when detached.
    pub branch: Option<String>,
    /// The full commit id; `None` for a bare entry or a branch with no commits yet.
    pub head: Option<String>,
    pub is_main: bool,
    pub is_bare: bool,
    pub is_dirty: bool,
    /// Commits on HEAD that no remote has.
    pub unpushed: u32,
    pub locked: bool,
    /// Git's mark for a worktree whose folder is gone.
    pub prunable: bool,
    pub head_subject: Option<String>,
    /// Unix milliseconds.
    pub head_committed_at: Option<i64>,
}

/// How loudly a mark means it.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Tone {
    /// Names the folder: main checkout, locked.
    Quiet,
    /// Work that would be lost: uncommitted changes, commits not pushed.
    Warn,
    /// The folder is gone.
    Danger,
}

#[derive(Clone, Debug, PartialEq)]
pub struct Mark {
    pub text: String,
    pub tone: Tone,
}

impl Worktree {
    /// The row's name: its branch, or "detached" with the short commit id.
    pub fn name(&self) -> String {
        match (&self.branch, &self.head) {
            (Some(branch), _) => branch.clone(),
            (None, Some(head)) => format!("detached {}", short(head)),
            (None, None) if self.is_bare => "bare".into(),
            (None, None) => "detached".into(),
        }
    }

    /// The row's marks, in the order it draws them. A clean worktree with every commit pushed has no warning.
    pub fn marks(&self) -> Vec<Mark> {
        let mark = |text: String, tone| Mark { text, tone };
        let mut marks = Vec::new();
        if self.is_main {
            marks.push(mark("main checkout".into(), Tone::Quiet));
        }
        if self.prunable {
            marks.push(mark("Folder gone".into(), Tone::Danger));
        }
        if self.is_dirty {
            marks.push(mark("Uncommitted".into(), Tone::Warn));
        }
        if self.unpushed > 0 {
            marks.push(mark(format!("{} not pushed", self.unpushed), Tone::Warn));
        }
        if self.locked {
            marks.push(mark("Locked".into(), Tone::Quiet));
        }
        marks
    }

    /// Nothing here would be lost if it went: not the main checkout, its folder there, clean, pushed and not locked.
    pub fn safe_to_remove(&self) -> bool {
        !self.is_main
            && !self.is_bare
            && !self.prunable
            && !self.is_dirty
            && self.unpushed == 0
            && !self.locked
    }

    /// The head commit's line in the open row: short id and subject.
    pub fn head_line(&self) -> Option<String> {
        let head = short(self.head.as_deref()?);
        Some(match &self.head_subject {
            Some(subject) => format!("{head} {subject}"),
            None => head.into(),
        })
    }

    /// When the head commit was made, in local time.
    pub fn committed(&self) -> Option<String> {
        let at = Local
            .timestamp_millis_opt(self.head_committed_at?)
            .single()?;
        Some(at.format("%Y-%m-%d %H:%M").to_string())
    }
}

fn short(head: &str) -> &str {
    &head[..head.len().min(7)]
}

#[cfg(test)]
#[path = "worktree_tests.rs"]
mod tests;
