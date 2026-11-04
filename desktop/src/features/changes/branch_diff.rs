use serde::Deserialize;

#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct BranchDiff {
    pub base: String,
    pub diff: String,
    pub truncated: bool,
}

#[cfg(test)]
#[path = "branch_diff_tests.rs"]
mod tests;
