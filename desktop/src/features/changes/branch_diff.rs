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
            rows.push(Row::Label(name_of(file)));
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

/// The file's name now: a rename's or copy's "to" line, or else X in "diff --git a/X b/X", which is the same name
/// twice, so a folder named "a b" can't split it in the wrong place. A name git quotes (it holds a tab, a quote or a
/// backslash) shows as the file is named.
fn name_of(file: &str) -> String {
    let to = file.lines().find_map(|line| {
        line.strip_prefix("rename to ")
            .or(line.strip_prefix("copy to "))
    });
    if let Some(to) = to {
        return unquoted(to);
    }
    let header = file.lines().next().unwrap_or_default();
    let names = header.strip_prefix("diff --git ").unwrap_or(header);
    let half = names.len().saturating_sub(1) / 2;
    let name = names
        .get(..half)
        .zip(names.get(half..).and_then(|rest| rest.strip_prefix(' ')))
        .and_then(|(a, b)| {
            let name = unquoted(a).strip_prefix("a/")?.to_string();
            (unquoted(b).strip_prefix("b/")? == name).then_some(name)
        });
    name.unwrap_or_else(|| header.to_string())
}

/// A name as git wrote it, read back: a quoted one loses its quotes and its escapes (`\t`, `\"`, `\\`, and `\303`
/// octal bytes) turn back into what they stand for.
fn unquoted(name: &str) -> String {
    let Some(quoted) = name
        .strip_prefix('"')
        .and_then(|name| name.strip_suffix('"'))
    else {
        return name.to_string();
    };
    let (mut bytes, mut rest) = (Vec::new(), quoted.as_bytes());
    while let Some((&byte, after)) = rest.split_first() {
        rest = after;
        if byte != b'\\' {
            bytes.push(byte);
            continue;
        }
        let Some((&escaped, after)) = rest.split_first() else {
            break;
        };
        rest = after;
        let octal = |b: &u8| (b'0'..=b'7').contains(b);
        match escaped {
            b't' => bytes.push(b'\t'),
            b'n' => bytes.push(b'\n'),
            b'r' => bytes.push(b'\r'),
            b'0'..=b'3' if rest.len() >= 2 && rest[..2].iter().all(octal) => {
                bytes.push((escaped - b'0') * 64 + (rest[0] - b'0') * 8 + (rest[1] - b'0'));
                rest = &rest[2..];
            }
            other => bytes.push(other),
        }
    }
    String::from_utf8_lossy(&bytes).into_owned()
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
