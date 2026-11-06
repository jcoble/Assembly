//! The backend retains the staged and working patches independently.
use serde::Deserialize;

#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct FileDiff {
    pub path: String,
    pub staged: String,
    pub unstaged: String,
    pub binary: bool,
    pub truncated: bool,
}

pub const CUT: &str = "This diff is too long to show in full; the rest is cut.";
impl FileDiff {
    pub fn parts(&self) -> Vec<(&'static str, &str)> {
        [
            ("Staged", self.staged.as_str()),
            ("Working tree", self.unstaged.as_str()),
        ]
        .into_iter()
        .filter(|(_, patch)| !patch.trim().is_empty())
        .collect()
    }
    pub fn notes(&self) -> Vec<&'static str> {
        let mut notes = Vec::new();
        if self.binary {
            notes.push("Binary file, so there is no line-by-line comparison.");
        } else if self.parts().is_empty() {
            notes.push("No line changes to show.");
        }
        if self.truncated {
            notes.push(CUT);
        }
        notes
    }
}

#[cfg(test)]
#[path = "file_diff_tests.rs"]
mod tests;
