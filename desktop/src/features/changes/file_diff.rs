//! `FileDiff` is one changed file's diff as `git/diff` answers it, with what the Changes tab makes of it: its rows,
//! the staged part and the working tree part each under its own label when both have lines to show (scm#14), and the
//! notes for a binary file, no changes and a cut diff (scm#16).

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
        let parts: Vec<_> = [("Staged", &self.staged), ("Working tree", &self.unstaged)]
            .into_iter()
            .map(|(label, text)| (label, lines_of(text)))
            .filter(|(_, lines)| !lines.is_empty())
            .collect();
        let both = parts.len() == 2;
        let mut rows = Vec::new();
        for (label, lines) in parts {
            if both {
                rows.push(Row::Label(label));
            }
            rows.extend(lines.into_iter().map(Row::Line));
        }
        rows
    }

    /// What the tab says under its rows, or in their place when there are none.
    pub fn notes(&self, rows: &[Row]) -> Vec<&'static str> {
        let mut notes = Vec::new();
        if self.binary {
            notes.push("Binary file, so there's no line-by-line comparison.");
        } else if rows.is_empty() {
            notes.push("No changes.");
        }
        if self.truncated {
            notes.push("This diff is too long to show in full; the rest is cut.");
        }
        notes
    }
}

#[cfg(test)]
#[path = "file_diff_tests.rs"]
mod tests;
