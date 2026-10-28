//! `GitStatus` is a project's status as `git/status` answers it, with what the Source control tab makes of it: the
//! branch line (scm#9), the Staged, Changes and Untracked lists with each file's letter (scm#10), and what the
//! 1000-file cap leaves out (scm#11). Replaces sourceControlSections.ts and the status half of gitPanelStore.svelte.ts.

use serde::Deserialize;

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GitStatus {
    /// `None` when detached.
    pub branch: Option<String>,
    /// `None` before the first commit.
    pub head: Option<String>,
    pub ahead: u32,
    pub behind: u32,
    pub has_upstream: bool,
    /// At most 1000.
    pub files: Vec<ChangedFile>,
    pub total_files: usize,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ChangedFile {
    pub path: String,
    /// Set for a rename or copy.
    pub original_path: Option<String>,
    /// Git's porcelain letters: `.` unchanged, `?` untracked, `U` (either side) a conflict.
    pub index: String,
    pub worktree: String,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Section {
    Staged,
    Changes,
    Untracked,
}

impl Section {
    pub const ALL: [Section; 3] = [Section::Staged, Section::Changes, Section::Untracked];

    pub fn label(self) -> &'static str {
        match self {
            Section::Staged => "Staged",
            Section::Changes => "Changes",
            Section::Untracked => "Untracked",
        }
    }
}

impl ChangedFile {
    fn untracked(&self) -> bool {
        self.index == "?"
    }

    fn conflicted(&self) -> bool {
        self.index == "U" || self.worktree == "U"
    }

    /// Its letter in `section`, or `None` when it isn't in it. A file staged and then edited again is in both Staged
    /// and Changes; a conflict is only in Changes, as C.
    fn letter_in(&self, section: Section) -> Option<char> {
        let letter = |side: &str| side.chars().next().filter(|&c| c != '.');
        match section {
            Section::Untracked => self.untracked().then_some('U'),
            _ if self.untracked() => None,
            Section::Changes if self.conflicted() => Some('C'),
            Section::Staged if self.conflicted() => None,
            Section::Staged => letter(&self.index),
            Section::Changes => letter(&self.worktree),
        }
    }

    /// Its path, with where it came from when it was renamed.
    pub fn label(&self) -> String {
        match &self.original_path {
            Some(from) => format!("{from} → {}", self.path),
            None => self.path.clone(),
        }
    }
}

impl GitStatus {
    /// "lane/scm ↑2 ↓1", or "detached 0123456, no upstream".
    pub fn branch_line(&self) -> String {
        let name = match (&self.branch, &self.head) {
            (Some(branch), _) => branch.clone(),
            (None, Some(head)) => format!("detached {}", &head[..head.len().min(7)]),
            (None, None) => "detached".into(),
        };
        if self.has_upstream {
            format!("{name} ↑{} ↓{}", self.ahead, self.behind)
        } else {
            format!("{name}, no upstream")
        }
    }

    /// The section's files with their letters, sorted by path.
    pub fn section(&self, section: Section) -> Vec<(char, &ChangedFile)> {
        let mut files: Vec<(char, &ChangedFile)> = self
            .files
            .iter()
            .filter_map(|file| file.letter_in(section).map(|letter| (letter, file)))
            .collect();
        files.sort_by(|a, b| a.1.path.cmp(&b.1.path));
        files
    }

    /// What the cap leaves out, said as a line under the lists; `None` when every file is listed.
    pub fn more_line(&self) -> Option<String> {
        match self.total_files.saturating_sub(self.files.len()) {
            0 => None,
            1 => Some("1 more file isn't listed.".into()),
            more => Some(format!("{more} more files aren't listed.")),
        }
    }
}

#[cfg(test)]
#[path = "git_status_tests.rs"]
mod tests;
