use super::super::super::pull_request::PullRequestDetail;
use super::{Fact, Move, Standing, readiness};

const BASE: &str = r#"{"repository":"o/r","localRoot":"/r","number":7,"title":"T","body":"","bodyBlocks":[],"url":"","author":"amy","headBranch":"feature","baseBranch":"main","headSha":"a","baseSha":"b","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"","createdAt":"","mergedAt":"","mergedBy":"","closedAt":"","commits":[],"comments":[],"reviews":[],"reviewers":[],"checks":[],"files":[],"moreFiles":false}"#;
fn detail(extra: &str) -> PullRequestDetail {
    let mut value: serde_json::Value = serde_json::from_str(BASE).unwrap();
    let extra: serde_json::Value = serde_json::from_str(extra).unwrap();
    for (key, item) in extra.as_object().unwrap() {
        value[key] = item.clone();
    }
    serde_json::from_value(value).unwrap()
}
fn titles(facts: &[Fact]) -> Vec<(&str, Standing, Option<Move>)> {
    facts
        .iter()
        .map(|fact| (fact.title.as_str(), fact.standing, fact.action))
        .collect()
}

#[test]
fn a_blocked_draft_lists_each_thing_in_the_way_with_its_move() {
    let pr = detail(
        r#"{
        "isDraft":true,"mergeable":"CONFLICTING","mergeState":"BEHIND","reviewDecision":"REVIEW_REQUIRED",
        "checks":[{"name":"build","status":"COMPLETED","conclusion":"FAILURE","url":""},{"name":"lint","status":"COMPLETED","conclusion":"SUCCESS","url":""}],
        "threads":[{"id":"t","path":"a.rs","resolved":false,"outdated":false,"commentIds":["1"]}]
    }"#,
    );
    assert_eq!(
        titles(&readiness(&pr)),
        [
            ("This is a draft", Standing::Waiting, Some(Move::Ready)),
            ("Review required", Standing::Waiting, None),
            ("1 unresolved conversation", Standing::Waiting, None),
            ("1 failing check", Standing::Bad, Some(Move::SeeChecks)),
            (
                "This branch has conflicts",
                Standing::Bad,
                Some(Move::ShowConflicts)
            ),
            (
                "This branch is out of date",
                Standing::Waiting,
                Some(Move::UpdateBranch)
            ),
        ]
    );
}
#[test]
fn a_reviewer_who_approved_after_asking_for_changes_counts_as_approved() {
    let pr = detail(
        r#"{"reviews":[
        {"author":"bo","body":"","bodyBlocks":[],"state":"CHANGES_REQUESTED","submittedAt":"1"},
        {"author":"cy","body":"","bodyBlocks":[],"state":"APPROVED","submittedAt":"2"},
        {"author":"bo","body":"","bodyBlocks":[],"state":"COMMENTED","submittedAt":"3"},
        {"author":"bo","body":"","bodyBlocks":[],"state":"APPROVED","submittedAt":"4"}
    ]}"#,
    );
    let reviews = &readiness(&pr)[0];
    assert_eq!(
        (reviews.title.as_str(), reviews.standing),
        ("Approved", Standing::Good)
    );
    assert_eq!(reviews.detail.as_deref(), Some("Approved by cy and bo."));
}
