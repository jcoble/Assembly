use super::file_diff::CUT;
use serde::Deserialize;

#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct BranchDiff {
    pub base: String,
    pub diff: String,
    pub truncated: bool,
}
impl BranchDiff {
    pub fn notes(&self) -> Vec<&'static str> {
        let mut notes = Vec::new();
        if self.diff.trim().is_empty() {
            notes.push("No changes.");
        }
        if self.truncated {
            notes.push(CUT);
        }
        notes
    }
}

#[cfg(test)]
#[path = "branch_diff_tests.rs"]
mod tests;
