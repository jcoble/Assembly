//! `FileDiff` is one changed file's diff as `git/diff` answers it, with what the Changes tab makes of it: its rows,
//! the staged part and the working tree part each under its own label when both have changes (scm#14), and the
//! messages for a binary file, no changes and a cut diff (scm#16).

use serde::Deserialize;

use super::diff_lines::{DiffLine, lines_of};

#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct FileDiff {
    pub path: String,
    pub staged: String,
    pub unstaged: String,
    pub binary: bool,
    pub truncated: bool,
}

/// One row of the tab's list.
#[derive(Clone, Debug, PartialEq)]
pub enum Row {
    Label(&'static str),
    Line(DiffLine),
}

impl FileDiff {
    pub fn rows(&self) -> Vec<Row> {
        let both = !self.staged.is_empty() && !self.unstaged.is_empty();
        let mut rows = Vec::new();
        for (label, text) in [("Staged", &self.staged), ("Working tree", &self.unstaged)] {
            if text.is_empty() {
                continue;
            }
            if both {
                rows.push(Row::Label(label));
            }
            rows.extend(lines_of(text).into_iter().map(Row::Line));
        }
        rows
    }

    /// What the tab says in place of the lines, when there are none to show.
    pub fn message(&self) -> Option<&'static str> {
        if self.binary {
            Some("Binary file, so there's no line-by-line comparison.")
        } else if self.staged.is_empty() && self.unstaged.is_empty() {
            Some("No changes.")
        } else {
            None
        }
    }

    pub fn cut_line(&self) -> Option<&'static str> {
        self.truncated.then_some("This diff is cut at 1 MiB.")
    }
}

#[cfg(test)]
#[path = "file_diff_tests.rs"]
mod tests;
