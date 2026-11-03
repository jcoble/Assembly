use serde::Deserialize;
use std::collections::HashSet;

#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GitCommit {
    pub short_sha: String,
    pub sha: String,
    pub subject: String,
    pub body: String,
    pub author: String,
    pub committed_at: String,
    pub refs: Vec<String>,
    pub parents: Vec<String>,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GitHistory { pub commits: Vec<GitCommit>, pub next_cursor: Option<String> }
#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GitCommitFile { pub path: String, pub original_path: Option<String>, pub letter: char }
impl GitHistory {
    pub fn append(&mut self, page: Self) {
        let mut seen: HashSet<String> = self.commits.iter().map(|commit| commit.sha.clone()).collect();
        self.commits.extend(page.commits.into_iter().filter(|commit| seen.insert(commit.sha.clone())));
        self.next_cursor = page.next_cursor;
    }
}
#[cfg(test)]
#[path = "git_history_tests.rs"]
mod tests;
