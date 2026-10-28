//! `BranchDiff` is the session's branch against its base as `git/branchDiff` answers it: one diff text, which the
//! Changes tab shows file under file, each under its own path (scm#18), with its notes (scm#20).

use serde::Deserialize;

use super::diff_lines::lines_of;
use super::file_diff::{CUT, Row};

#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct BranchDiff {
    /// The branch compared with, such as `origin/main`.
    pub base: String,
    pub diff: String,
    pub truncated: bool,
}

impl BranchDiff {
    pub fn rows(&self) -> Vec<Row> {
        let mut rows = Vec::new();
        for file in files(&self.diff) {
            // "diff --git a/old b/new": the path after the last " b/" is the file's name now.
            let header = file.lines().next().unwrap_or_default();
            let path = header.rsplit_once(" b/").map_or(header, |(_, path)| path);
            rows.push(Row::Label(path.to_string()));
            rows.extend(lines_of(file).into_iter().map(Row::Line));
        }
        rows
    }

    /// What the tab says under its rows, or in their place when there are none.
    pub fn notes(&self, rows: &[Row]) -> Vec<&'static str> {
        let mut notes = Vec::new();
        if rows.is_empty() {
            notes.push("No changes.");
        }
        if self.truncated {
            notes.push(CUT);
        }
        notes
    }
}

/// Each file's part of the text, from its `diff --git` line to the next one.
fn files(diff: &str) -> Vec<&str> {
    let mut starts: Vec<usize> = diff
        .match_indices("diff --git ")
        .map(|(at, _)| at)
        .filter(|&at| at == 0 || diff.as_bytes()[at - 1] == b'\n')
        .collect();
    starts.push(diff.len());
    starts.windows(2).map(|at| &diff[at[0]..at[1]]).collect()
}

#[cfg(test)]
#[path = "branch_diff_tests.rs"]
mod tests;
