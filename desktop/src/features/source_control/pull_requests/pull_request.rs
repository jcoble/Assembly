use crate::core::navigation::web_links::pull_request;
use serde::Deserialize;
use std::collections::HashSet;

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PullRequestSummary {
    pub repository: String,
    pub local_root: Option<String>,
    pub number: u64,
    pub title: String,
    pub url: String,
    pub is_draft: bool,
    pub updated_at: String,
    pub state: String,
    pub author: String,
    pub head_branch: String,
    pub base_branch: String,
    /// `passing`, `failing`, `pending` or `none`, when the backend knows.
    pub checks: Option<String>,
    pub check_summary: Option<String>,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum CheckTone {
    Passing,
    Failing,
    Pending,
    Quiet,
}
impl PullRequestSummary {
    /// The row's check text and how it reads; nothing when the backend didn't say.
    pub fn check_line(&self) -> Option<(String, CheckTone)> {
        let checks = self.checks.as_deref();
        let tone = match checks {
            Some("passing") => CheckTone::Passing,
            Some("failing") => CheckTone::Failing,
            Some("pending") => CheckTone::Pending,
            _ => CheckTone::Quiet,
        };
        if checks == Some("none") {
            return Some(("no checks".into(), tone));
        }
        let text = self
            .check_summary
            .clone()
            .or_else(|| checks.map(|checks| format!("checks {checks}")))?;
        Some((text, tone))
    }
}
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestPage {
    pub items: Vec<PullRequestSummary>,
    pub cursor: Option<String>,
    pub total: usize,
}
impl PullRequestPage {
    pub fn append(&mut self, page: Self) {
        let mut seen: HashSet<(String, u64)> = self
            .items
            .iter()
            .map(|pr| (pr.repository.to_lowercase(), pr.number))
            .collect();
        self.items.extend(
            page.items
                .into_iter()
                .filter(|pr| seen.insert((pr.repository.to_lowercase(), pr.number))),
        );
        self.cursor = page.cursor;
        self.total = page.total;
    }
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestCreated {
    pub number: u64,
    pub url: String,
}
#[derive(Clone, Debug, PartialEq)]
pub struct PrTarget {
    pub repository: String,
    pub number: u64,
}
impl PrTarget {
    /// The same pull request under a project's own spelling of its repository, if one holds it.
    pub fn among(self, repositories: &[String]) -> Option<Self> {
        let repository = repositories
            .iter()
            .find(|known| known.eq_ignore_ascii_case(&self.repository))?;
        Some(Self {
            repository: repository.clone(),
            number: self.number,
        })
    }
    /// A structural target only, by the one pull request link parser. The caller must still validate its project or
    /// actual checkout origin.
    pub fn parse_url(text: &str) -> Result<Self, String> {
        url::Url::parse(text.trim())
            .ok()
            .and_then(|url| pull_request(&url))
            .map(|(repository, number)| Self { repository, number })
            .ok_or_else(|| "Paste a GitHub pull request link.".into())
    }
    /// The pull request's page on GitHub.
    pub fn web_page(&self) -> String {
        format!(
            "https://github.com/{}/pull/{}",
            self.repository, self.number
        )
    }
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestDetail {
    pub repository: String,
    pub local_root: String,
    pub number: u64,
    pub title: String,
    pub body: String,
    pub body_blocks: Vec<crate::core::ui::markdown::payload::Block>,
    pub url: String,
    pub author: String,
    pub head_branch: String,
    pub base_branch: String,
    pub head_sha: String,
    pub base_sha: String,
    pub state: String,
    pub is_draft: bool,
    pub mergeable: String,
    pub created_at: String,
    pub merged_at: String,
    pub merged_by: String,
    pub closed_at: String,
    pub commits: Vec<PullRequestCommit>,
    pub comments: Vec<PullRequestComment>,
    pub reviews: Vec<PullRequestReview>,
    pub reviewers: Vec<String>,
    pub checks: Vec<PullRequestCheck>,
    pub files: Vec<PullRequestFile>,
    pub more_files: bool,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestComment {
    pub id: String,
    pub author: String,
    pub body: String,
    pub body_blocks: Vec<crate::core::ui::markdown::payload::Block>,
    pub created_at: String,
    pub path: Option<String>,
    pub line: Option<usize>,
    pub reply_to_id: Option<u64>,
}
impl PullRequestComment {
    /// GitHub takes a reply only on the comment that opened a thread on a file, by its number. A comment no longer on
    /// a line of the diff still qualifies.
    pub fn reply_id(&self) -> Option<u64> {
        (self.path.is_some() && self.reply_to_id.is_none())
            .then(|| self.id.parse().ok())
            .flatten()
    }
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestReview {
    pub author: String,
    pub body: String,
    pub body_blocks: Vec<crate::core::ui::markdown::payload::Block>,
    pub state: String,
    pub submitted_at: String,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestCommit {
    pub oid: String,
    pub headline: String,
    pub author: String,
    pub committed_at: String,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestCheck {
    pub name: String,
    pub status: String,
    pub conclusion: String,
    pub url: String,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestFile {
    pub path: String,
    pub previous_path: Option<String>,
    pub status: String,
    pub additions: usize,
    pub deletions: usize,
    pub patch: Option<String>,
    pub patch_omitted: bool,
}
#[cfg(test)]
#[path = "pull_request_tests.rs"]
mod tests;
