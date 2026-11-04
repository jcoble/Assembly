//! What the Git history tab's toolbar narrows: a branch and everything its tip descends from among the loaded commits,
//! an author, and text in the message, hash, author or names. Replaces gitHistoryFilters.ts.
use super::git_history::GitCommit;
use super::git_labels::is_remote_ref;
use std::collections::{HashMap, HashSet};

#[derive(Clone, Default)]
pub struct HistoryFilter {
    pub branch: Option<String>,
    pub author: Option<String>,
    pub text: String,
}
/// A commit's branch name for this decoration, local or remote; tags and a bare HEAD name no line of history.
fn branch_of(reference: &str) -> Option<&str> {
    let name = reference.strip_prefix("HEAD -> ").unwrap_or(reference);
    (name != "HEAD" && !name.starts_with("tag: ")).then_some(name)
}
/// Every branch the loaded commits carry, once each, local names before the settled order of the rest.
pub fn branch_options(commits: &[GitCommit]) -> Vec<String> {
    let mut names: Vec<String> = commits
        .iter()
        .flat_map(|commit| commit.refs.iter().filter_map(|name| branch_of(name)))
        .map(str::to_owned)
        .collect();
    names.sort_by(|a, b| (is_remote_ref(a), a).cmp(&(is_remote_ref(b), b)));
    names.dedup();
    names
}
/// Every author in the loaded commits, once each.
pub fn author_options(commits: &[GitCommit]) -> Vec<String> {
    let mut names: Vec<String> = commits
        .iter()
        .map(|commit| commit.author.trim().to_owned())
        .filter(|name| !name.is_empty())
        .collect();
    names.sort();
    names.dedup();
    names
}
impl HistoryFilter {
    pub fn active(&self) -> bool {
        self.branch.is_some() || self.author.is_some() || !self.text.trim().is_empty()
    }
    /// Author and text hide the uncommitted row, which has neither; a branch keeps it.
    pub fn hides_uncommitted(&self) -> bool {
        self.author.is_some() || !self.text.trim().is_empty()
    }
    /// The commits a branch reaches among the loaded ones; loading more history widens the answer.
    fn reached<'a>(&self, commits: &'a [GitCommit]) -> Option<HashSet<&'a str>> {
        let wanted = self.branch.as_deref()?;
        let by_sha: HashMap<&str, &GitCommit> = commits
            .iter()
            .map(|commit| (commit.sha.as_str(), commit))
            .collect();
        let mut queue: Vec<&str> = commits
            .iter()
            .filter(|commit| {
                commit
                    .refs
                    .iter()
                    .any(|name| branch_of(name) == Some(wanted))
            })
            .map(|commit| commit.sha.as_str())
            .collect();
        let mut reached: HashSet<&str> = queue.iter().copied().collect();
        while let Some(sha) = queue.pop() {
            for parent in by_sha
                .get(sha)
                .map_or(&[][..], |commit| &commit.parents[..])
            {
                if let Some(known) = by_sha.get(parent.as_str()) {
                    if reached.insert(known.sha.as_str()) {
                        queue.push(known.sha.as_str());
                    }
                }
            }
        }
        Some(reached)
    }
    /// The positions of the commits that pass all three, in the order they came.
    pub fn visible(&self, commits: &[GitCommit]) -> Vec<usize> {
        let reached = self.reached(commits);
        let author = self.author.as_deref().map(str::trim);
        let text = self.text.trim().to_lowercase();
        commits
            .iter()
            .enumerate()
            .filter(|(_, commit)| {
                reached
                    .as_ref()
                    .is_none_or(|reached| reached.contains(commit.sha.as_str()))
                    && author.is_none_or(|wanted| commit.author.trim() == wanted)
                    && (text.is_empty()
                        || [&commit.subject, &commit.sha, &commit.author]
                            .into_iter()
                            .chain(&commit.refs)
                            .any(|field| field.to_lowercase().contains(&text)))
            })
            .map(|(index, _)| index)
            .collect()
    }
}
#[cfg(test)]
#[path = "history_filter_tests.rs"]
mod tests;
