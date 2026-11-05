//! `GitStatus` is a project's status as `git/status` answers it, with what the Source control tab makes of it: the
//! branch name and how far it is from its remote (scm#9), the Staged, Changes and Untracked lists with each file's letter (scm#10), and what the
//! 1000-file cap leaves out (scm#11). Replaces sourceControlSections.ts and the status half of gitPanelStore.svelte.ts.

use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
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
    #[serde(default)]
    pub total_staged: Option<usize>,
    pub fingerprint: String,
    /// The root the read actually came from; writes send it back unchanged.
    #[serde(default)]
    pub checkout_root: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ChangedFile {
    pub path: String,
    /// Set for a rename or copy.
    pub original_path: Option<String>,
    /// Git's porcelain letters: `.` unchanged, `?` untracked, `U` (either side) a conflict.
    pub index: String,
    pub worktree: String,
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
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
    pub fn untracked(&self) -> bool {
        self.index == "?"
    }

    fn conflicted(&self) -> bool {
        self.index == "U" || self.worktree == "U"
    }

    /// Its letter in `section`, or `None` when it isn't in it. A file staged and then edited again is in both Staged
    /// and Changes; a conflict is only in Changes, as "!", since git's C is a copy.
    fn letter_in(&self, section: Section) -> Option<char> {
        let letter = |side: &str| side.chars().next().filter(|&c| c != '.');
        match section {
            Section::Untracked => self.untracked().then_some('?'),
            _ if self.untracked() => None,
            Section::Changes if self.conflicted() => Some('!'),
            Section::Staged if self.conflicted() => None,
            Section::Staged => letter(&self.index),
            Section::Changes => letter(&self.worktree),
        }
    }

    /// Its path, with where it came from when it was renamed.
    /// A row's tip: where it is, then what each side of git says about it.
    pub fn tip(&self) -> String {
        let mut tip = self.label();
        if !matches!(self.index.as_str(), "." | "?") {
            tip.push_str(&format!("\nStaged: {}", self.index));
        }
        if !matches!(self.worktree.as_str(), "." | "?") {
            tip.push_str(&format!("\nOn disk: {}", self.worktree));
        }
        tip
    }
    pub fn label(&self) -> String {
        match &self.original_path {
            Some(from) => format!("{from} → {}", self.path),
            None => self.path.clone(),
        }
    }
}

/// 1201 as a person writes it, 1,201.
fn grouped(count: usize) -> String {
    let digits = count.to_string();
    let mut out = String::new();
    for (at, digit) in digits.chars().enumerate() {
        if at > 0 && (digits.len() - at).is_multiple_of(3) {
            out.push(',');
        }
        out.push(digit);
    }
    out
}

impl GitStatus {
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

    /// Older payloads establish a total only when every changed file is present.
    pub fn staged_total(&self) -> Option<usize> {
        self.total_staged.or_else(|| {
            (self.total_files == self.files.len()).then(|| {
                self.files
                    .iter()
                    .filter(|file| file.letter_in(Section::Staged).is_some())
                    .count()
            })
        })
    }
    pub fn has_staged_files(&self) -> bool {
        self.staged_total().is_some_and(|total| total > 0)
    }

    /// What the cap leaves out, said above the lists; `None` when every file is listed.
    pub fn more_line(&self) -> Option<String> {
        (self.total_files > self.files.len()).then(|| {
            format!(
                "Showing {} of {} changed files. Commit, stash or ignore files to see the rest.",
                grouped(self.files.len()),
                grouped(self.total_files)
            )
        })
    }
    /// The branch menu's hover text: the whole name, what switching does, and how far it is from its remote.
    pub fn branch_title(&self) -> String {
        let commits = |count: u32| if count == 1 { "commit" } else { "commits" };
        let mut lines = vec![
            format!("Checked-out branch: {}", self.branch_name()),
            "Switching here changes this working directory.".to_string(),
        ];
        if self.behind > 0 {
            lines.push(format!(
                "{} {} behind its remote.",
                self.behind,
                commits(self.behind)
            ));
        }
        if self.ahead > 0 {
            lines.push(format!(
                "{} {} ahead of its remote.",
                self.ahead,
                commits(self.ahead)
            ));
        }
        lines.join("\n")
    }
    /// The trigger's short name: the branch, or the plain words for having none.
    pub fn branch_name(&self) -> String {
        self.branch
            .clone()
            .unwrap_or_else(|| "no branch checked out".to_string())
    }
}

#[cfg(test)]
#[path = "git_status_tests.rs"]
mod tests;
