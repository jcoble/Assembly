//! What stands between an open pull request and its merge, one line each: draft, reviews, conversations, checks,
//! conflicts and an out-of-date branch. Each line that can be dealt with here names the move that deals with it.
use super::super::{
    pr_activity::check_tone,
    pull_request::{CheckTone, PrConflicts},
};
use super::*;

#[derive(Clone, Copy, Debug, PartialEq)]
pub(super) enum Standing {
    Good,
    Waiting,
    Bad,
    /// Nothing to do and nothing to celebrate, such as a repository that asks for no reviews.
    Quiet,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub(super) enum Move {
    Ready,
    SeeChecks,
    ShowConflicts,
    UpdateBranch,
}
#[derive(Clone, Debug, PartialEq)]
pub(super) struct Fact {
    pub standing: Standing,
    pub title: String,
    pub detail: Option<String>,
    pub action: Option<Move>,
}
fn fact(
    standing: Standing,
    title: impl Into<String>,
    detail: Option<String>,
    action: Option<Move>,
) -> Fact {
    Fact {
        standing,
        title: title.into(),
        detail,
        action,
    }
}
fn names(names: &[&str]) -> String {
    match names {
        [] => String::new(),
        [one] => (*one).to_string(),
        [rest @ .., last] => format!("{} and {last}", rest.join(", ")),
    }
}
/// Each reviewer's latest say, oldest first: what GitHub's own decision is made of.
fn latest_reviews(detail: &PullRequestDetail) -> Vec<(&str, &str)> {
    let mut latest: Vec<(&str, &str)> = Vec::new();
    for review in &detail.reviews {
        if !matches!(
            review.state.as_str(),
            "APPROVED" | "CHANGES_REQUESTED" | "DISMISSED"
        ) {
            continue;
        }
        latest.retain(|(author, _)| *author != review.author);
        latest.push((&review.author, &review.state));
    }
    latest
}
fn reviews_fact(detail: &PullRequestDetail) -> Fact {
    let latest = latest_reviews(detail);
    let by = |state: &str| {
        latest
            .iter()
            .filter(|(_, said)| *said == state)
            .map(|(who, _)| *who)
            .collect::<Vec<_>>()
    };
    let (approved, blocked) = (by("APPROVED"), by("CHANGES_REQUESTED"));
    let requested = (!detail.reviewers.is_empty()).then(|| {
        format!(
            "Waiting on {}.",
            names(
                &detail
                    .reviewers
                    .iter()
                    .map(String::as_str)
                    .collect::<Vec<_>>()
            )
        )
    });
    match detail.review_decision.as_str() {
        "CHANGES_REQUESTED" => fact(
            Standing::Bad,
            "Changes requested",
            Some(format!("{} asked for changes.", names(&blocked))).filter(|_| !blocked.is_empty()),
            None,
        ),
        "APPROVED" => fact(
            Standing::Good,
            "Approved",
            Some(format!("Approved by {}.", names(&approved))).filter(|_| !approved.is_empty()),
            None,
        ),
        "REVIEW_REQUIRED" => fact(
            Standing::Waiting,
            "Review required",
            requested.or(Some(
                "An approving review is required before merging.".into(),
            )),
            None,
        ),
        _ if !blocked.is_empty() => fact(
            Standing::Waiting,
            "Changes requested",
            Some(format!("{} asked for changes.", names(&blocked))),
            None,
        ),
        _ if !approved.is_empty() => fact(
            Standing::Good,
            "Approved",
            Some(format!("Approved by {}.", names(&approved))),
            None,
        ),
        _ => fact(
            Standing::Quiet,
            "No reviews yet",
            requested.or(Some("This repository doesn't require one.".into())),
            None,
        ),
    }
}
fn checks_fact(detail: &PullRequestDetail) -> Option<Fact> {
    let checks = &detail.checks;
    if checks.is_empty() {
        return Some(fact(
            Standing::Quiet,
            "No checks",
            Some("GitHub reports no checks for this head.".into()),
            None,
        ));
    }
    let count = |tone: CheckTone| {
        checks
            .iter()
            .filter(|check| check_tone(check) == tone)
            .count()
    };
    let (failing, pending, passing) = (
        count(CheckTone::Failing),
        count(CheckTone::Pending),
        count(CheckTone::Passing),
    );
    let tally = Some(format!("{passing} of {} successful.", checks.len()));
    let see = Some(Move::SeeChecks);
    Some(match (failing, pending) {
        (0, 0) => fact(Standing::Good, "All checks passed", tally, see),
        (0, pending) => fact(
            Standing::Waiting,
            format!(
                "{pending} {} running",
                if pending == 1 { "check" } else { "checks" }
            ),
            tally,
            see,
        ),
        (failing, _) => fact(
            Standing::Bad,
            format!(
                "{failing} failing {}",
                if failing == 1 { "check" } else { "checks" }
            ),
            tally,
            see,
        ),
    })
}
fn conversations_fact(detail: &PullRequestDetail) -> Option<Fact> {
    if detail.threads.is_empty() {
        return None;
    }
    let open = detail
        .threads
        .iter()
        .filter(|thread| !thread.resolved)
        .count();
    Some(match open {
        0 => fact(Standing::Good, "All conversations resolved", None, None),
        1 => fact(
            Standing::Waiting,
            "1 unresolved conversation",
            Some("Resolve it in the activity below.".into()),
            None,
        ),
        open => fact(
            Standing::Waiting,
            format!("{open} unresolved conversations"),
            Some("Resolve them in the activity below.".into()),
            None,
        ),
    })
}
fn conflicts_fact(detail: &PullRequestDetail) -> Fact {
    match detail.mergeable.as_str() {
        "MERGEABLE" => fact(
            Standing::Good,
            "No conflicts with the base branch",
            None,
            None,
        ),
        "CONFLICTING" => fact(
            Standing::Bad,
            "This branch has conflicts",
            Some(format!(
                "{} and {} change the same lines.",
                detail.head_branch, detail.base_branch
            )),
            Some(Move::ShowConflicts),
        ),
        _ => fact(
            Standing::Waiting,
            "GitHub is checking for conflicts",
            Some("Refresh in a moment.".into()),
            None,
        ),
    }
}
/// The lines for an open pull request, in the order they're dealt with.
pub(super) fn readiness(detail: &PullRequestDetail) -> Vec<Fact> {
    let draft = detail.is_draft.then(|| {
        fact(
            Standing::Waiting,
            "This is a draft",
            Some("Mark it ready when it's ready to be reviewed and merged.".into()),
            Some(Move::Ready),
        )
    });
    let behind = (detail.merge_state == "BEHIND").then(|| {
        fact(
            Standing::Waiting,
            "This branch is out of date",
            Some(format!(
                "{} has moved on. Merge it into {} before merging.",
                detail.base_branch, detail.head_branch
            )),
            Some(Move::UpdateBranch),
        )
    });
    draft
        .into_iter()
        .chain([reviews_fact(detail)])
        .chain(conversations_fact(detail))
        .chain(checks_fact(detail))
        .chain([conflicts_fact(detail)])
        .chain(behind)
        .collect()
}
/// What an agent is asked to resolve the conflicts: the branches, the files and what Git sees in them.
pub(super) fn resolve_prompt(detail: &PullRequestDetail, conflicts: &PrConflicts) -> String {
    let files: Vec<String> = conflicts
        .files
        .iter()
        .map(|file| {
            let regions = file
                .regions
                .iter()
                .take(3)
                .cloned()
                .collect::<Vec<_>>()
                .join("\n…\n");
            if regions.is_empty() {
                format!("- {}", file.path)
            } else {
                format!("- {}\n```\n{regions}\n```", file.path)
            }
        })
        .collect();
    format!(
        "Pull request #{} ({}) can't merge: {} conflicts with {}.\n\nThe conflicting files, with Git's view of the merge:\n{}\n\nMerge {} into {}, resolve each conflict keeping what both sides meant, run the checks and push.",
        detail.number,
        detail.repository,
        detail.head_branch,
        detail.base_branch,
        files.join("\n"),
        detail.base_branch,
        detail.head_branch
    )
}
#[cfg(test)]
#[path = "readiness_tests.rs"]
mod tests;
