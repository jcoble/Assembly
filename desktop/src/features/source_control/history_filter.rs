use super::git_history::GitCommit;

#[derive(Clone, Default)]
pub struct HistoryFilter { pub branch: Option<String>, pub author: Option<String>, pub text: String }
impl HistoryFilter {
    pub fn matches(&self, commit: &GitCommit) -> bool {
        let branch = self.branch.as_ref().is_none_or(|wanted| commit.refs.iter().any(|reference| {
            reference == wanted || reference.strip_prefix("HEAD -> ") == Some(wanted.as_str())
        }));
        let author = self.author.as_ref().is_none_or(|wanted| &commit.author == wanted);
        let text = self.text.trim().to_lowercase();
        branch && author && (text.is_empty() || commit.subject.to_lowercase().contains(&text) || commit.sha.starts_with(&text))
    }
    pub fn summary(&self, matched: usize, loaded: usize) -> String { format!("{matched} of {loaded} loaded commits match") }
}
#[cfg(test)]
#[path = "history_filter_tests.rs"]
mod tests;
