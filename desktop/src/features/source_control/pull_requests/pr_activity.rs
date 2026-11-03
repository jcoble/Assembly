//! What the Summary page says about a pull request, as plain data: its state, its activity oldest first, and how its
//! checks and merge stand. The words are the old Pull requests tab's.
use super::pull_request::{
    CheckTone, PullRequestCheck, PullRequestComment, PullRequestDetail, PullRequestFile,
    PullRequestReview,
};
use crate::features::sessions::history::age::age;

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum PrTone {
    Good,
    Merged,
    Bad,
    Idle,
}
pub fn pr_state(state: &str, is_draft: bool) -> (&'static str, PrTone) {
    match state.to_uppercase().as_str() {
        "" => ("", PrTone::Idle),
        "MERGED" => ("Merged", PrTone::Merged),
        "CLOSED" => ("Closed", PrTone::Bad),
        _ if is_draft => ("Draft", PrTone::Idle),
        _ => ("Open", PrTone::Good),
    }
}
#[derive(Clone, Debug)]
pub enum ActivityRow {
    Opened {
        at: String,
        author: String,
    },
    Merged {
        at: String,
        author: String,
    },
    Closed {
        at: String,
    },
    Commit {
        at: String,
        author: String,
        oid: String,
        headline: String,
    },
    Comment {
        at: String,
        comment: PullRequestComment,
    },
    Review {
        at: String,
        review: PullRequestReview,
    },
}
impl ActivityRow {
    pub fn at(&self) -> &str {
        match self {
            Self::Opened { at, .. }
            | Self::Merged { at, .. }
            | Self::Closed { at }
            | Self::Commit { at, .. }
            | Self::Comment { at, .. }
            | Self::Review { at, .. } => at,
        }
    }
}
/// Everything that happened on the pull request, oldest first. A review that only carries line comments isn't a row.
pub fn activity_rows(pr: &PullRequestDetail) -> Vec<ActivityRow> {
    let mut rows = vec![ActivityRow::Opened {
        at: pr.created_at.clone(),
        author: pr.author.clone(),
    }];
    rows.extend(pr.commits.iter().map(|commit| ActivityRow::Commit {
        at: commit.committed_at.clone(),
        author: commit.author.clone(),
        oid: commit.oid.clone(),
        headline: commit.headline.clone(),
    }));
    rows.extend(pr.comments.iter().map(|comment| ActivityRow::Comment {
        at: comment.created_at.clone(),
        comment: comment.clone(),
    }));
    rows.extend(
        pr.reviews
            .iter()
            .filter(|review| !review.body.trim().is_empty() || review.state != "COMMENTED")
            .map(|review| ActivityRow::Review {
                at: review.submitted_at.clone(),
                review: review.clone(),
            }),
    );
    if !pr.merged_at.is_empty() {
        rows.push(ActivityRow::Merged {
            at: pr.merged_at.clone(),
            author: pr.merged_by.clone(),
        });
    } else if !pr.closed_at.is_empty() {
        rows.push(ActivityRow::Closed {
            at: pr.closed_at.clone(),
        });
    }
    rows.sort_by(|left, right| left.at().cmp(right.at()));
    rows
}
pub fn check_tone(check: &PullRequestCheck) -> CheckTone {
    let value = if check.conclusion.is_empty() {
        check.status.to_lowercase()
    } else {
        check.conclusion.to_lowercase()
    };
    if matches!(value.as_str(), "success" | "neutral" | "skipped") {
        return CheckTone::Passing;
    }
    let failed = ["fail", "error", "cancel", "timed_out", "action_required"];
    if failed.iter().any(|word| value.contains(word)) {
        return CheckTone::Failing;
    }
    CheckTone::Pending
}
fn passing(checks: &[PullRequestCheck]) -> usize {
    let is_passing = |check: &&PullRequestCheck| check_tone(check) == CheckTone::Passing;
    checks.iter().filter(is_passing).count()
}
/// The Checks column's heading.
pub fn check_summary(checks: &[PullRequestCheck]) -> String {
    if checks.is_empty() {
        return "No checks".into();
    }
    let is_failing = |check: &&PullRequestCheck| check_tone(check) == CheckTone::Failing;
    let failing = checks.iter().filter(is_failing).count();
    if failing > 0 {
        return format!("{failing} failing");
    }
    if passing(checks) == checks.len() {
        return "All passing".into();
    }
    format!("{} of {} passing", passing(checks), checks.len())
}
pub fn check_word(check: &PullRequestCheck) -> String {
    let word = [&check.conclusion, &check.status]
        .into_iter()
        .find(|value| !value.is_empty())
        .map_or("unknown", String::as_str);
    words_of(word)
}
/// The merge card's first line, and whether it reads as settled.
pub fn checks_fact(checks: &[PullRequestCheck]) -> (bool, String) {
    if checks.is_empty() {
        return (false, "No checks reported".into());
    }
    let good = passing(checks);
    (
        good == checks.len(),
        format!("{good} of {} checks successful", checks.len()),
    )
}
/// The merge card's second line, and whether it reads as settled.
pub fn mergeable_fact(mergeable: &str) -> (bool, &'static str) {
    match mergeable {
        "MERGEABLE" => (true, "This branch has no conflicts with the base branch."),
        "CONFLICTING" => (false, "This branch has conflicts with the base branch."),
        _ => (
            false,
            "GitHub is still checking whether this branch can merge.",
        ),
    }
}
pub fn merge_method_label(method: &str) -> &'static str {
    match method {
        "squash" => "Squash and merge",
        "rebase" => "Rebase and merge",
        _ => "Create merge commit",
    }
}
pub fn post_label(lines: usize) -> String {
    match lines {
        0 => "Comment".into(),
        1 => "Post review · 1 line comment".into(),
        lines => format!("Post review · {lines} line comments"),
    }
}
pub fn change_totals(files: &[PullRequestFile]) -> (usize, usize) {
    files.iter().fold((0, 0), |(added, removed), file| {
        (added + file.additions, removed + file.deletions)
    })
}
/// `CHANGES_REQUESTED` as a person says it.
pub fn words_of(state: &str) -> String {
    state.replace('_', " ").to_lowercase()
}
pub fn repo_name(repository: &str) -> &str {
    repository.rsplit('/').next().unwrap_or(repository)
}
fn millis_of(stamp: &str) -> Option<i64> {
    let at = chrono::DateTime::parse_from_rfc3339(stamp).ok()?;
    Some(at.timestamp_millis())
}
/// How long ago GitHub's stamp was, short; nothing for an empty or unreadable one.
pub fn age_of(stamp: &str, now_millis: i64) -> Option<String> {
    millis_of(stamp).map(|then| age(now_millis, then))
}

#[cfg(test)]
#[path = "pr_activity_tests.rs"]
mod tests;
