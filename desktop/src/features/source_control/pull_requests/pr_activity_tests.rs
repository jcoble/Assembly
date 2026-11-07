use super::*;
use serde_json::json;

fn detail(extra: serde_json::Value) -> PullRequestDetail {
    let mut value = json!({"repository":"o/r","localRoot":"/r","number":7,"title":"T","body":"","bodyBlocks":[],"url":"","author":"amy","headBranch":"f","baseBranch":"main","headSha":"a","baseSha":"b","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"","createdAt":"2026-01-01T00:00:00Z","mergedAt":"","mergedBy":"","closedAt":"","commits":[],"comments":[],"reviews":[],"reviewers":[],"checks":[],"files":[],"moreFiles":false,"timeline":[]});
    for (key, item) in extra.as_object().unwrap() {
        value[key] = item.clone();
    }
    serde_json::from_value(value).unwrap()
}
fn check(status: &str, conclusion: &str) -> PullRequestCheck {
    PullRequestCheck {
        name: "ci".into(),
        status: status.into(),
        conclusion: conclusion.into(),
        url: String::new(),
        ..Default::default()
    }
}
// Pins: old PullRequestWorkspace.svelte Summary page.
#[test]
fn activity_runs_oldest_first_and_skips_a_review_with_only_line_comments() {
    let pr = detail(json!({
        "commits":[{"oid":"abc","headline":"Fix","author":"amy","committedAt":"2026-01-03T00:00:00Z"}],
        "comments":[{"id":"1","author":"bo","body":"Hi","bodyBlocks":[],"createdAt":"2026-01-02T00:00:00Z"}],
        "reviews":[
            {"author":"bo","body":"","bodyBlocks":[],"state":"COMMENTED","submittedAt":"2026-01-04T00:00:00Z"},
            {"author":"cy","body":"","bodyBlocks":[],"state":"APPROVED","submittedAt":"2026-01-05T00:00:00Z"}
        ],
        "mergedAt":"2026-01-06T00:00:00Z","mergedBy":"amy"
    }));
    let rows = activity_rows(&pr);
    assert_eq!(rows.len(), 5);
    assert!(matches!(rows[0], ActivityRow::Opened { .. }));
    assert!(matches!(rows[1], ActivityRow::Comment { .. }));
    assert!(matches!(rows[2], ActivityRow::Commit { .. }));
    assert!(matches!(&rows[3], ActivityRow::Review { review, .. } if review.author == "cy"));
    assert!(matches!(rows[4], ActivityRow::Merged { .. }));
}
#[test]
fn a_closed_unmerged_pull_request_ends_with_closed() {
    let pr = detail(json!({"state":"CLOSED","closedAt":"2026-01-02T00:00:00Z"}));
    assert!(matches!(
        activity_rows(&pr).last(),
        Some(ActivityRow::Closed { .. })
    ));
}
#[test]
fn the_state_pill_says_what_the_pull_request_is() {
    assert_eq!(pr_state("MERGED", false), ("Merged", PrTone::Merged));
    assert_eq!(pr_state("CLOSED", true), ("Closed", PrTone::Bad));
    assert_eq!(pr_state("OPEN", true), ("Draft", PrTone::Idle));
    assert_eq!(pr_state("open", false), ("Open", PrTone::Good));
    assert_eq!(pr_state("", false), ("", PrTone::Idle));
}
#[test]
fn checks_read_as_failing_passing_or_a_count() {
    assert_eq!(check_summary(&[]), "No checks");
    assert_eq!(
        check_summary(&[check("COMPLETED", "SUCCESS")]),
        "All passing"
    );
    assert_eq!(
        check_summary(&[check("COMPLETED", "SUCCESS"), check("IN_PROGRESS", "")]),
        "1 of 2 passing"
    );
    assert_eq!(
        check_summary(&[
            check("COMPLETED", "TIMED_OUT"),
            check("COMPLETED", "SUCCESS")
        ]),
        "1 failing"
    );
    assert_eq!(check_word(&check("IN_PROGRESS", "")), "in progress");
}
#[test]
fn the_post_button_counts_its_line_comments() {
    assert_eq!(post_label(0), "Comment");
    assert_eq!(post_label(1), "Post review · 1 line comment");
    assert_eq!(post_label(3), "Post review · 3 line comments");
}
#[test]
fn an_age_needs_a_real_stamp() {
    let now = 1_767_225_600_000 + 3 * 3_600_000;
    assert_eq!(age_of("2026-01-01T00:00:00Z", now).as_deref(), Some("3h"));
    assert_eq!(age_of("", now), None);
}

// Pins: old PullRequestWorkspace.svelte Activity: every comment is listed; Reply is on a file comment that opened a thread.
#[test]
fn every_posted_comment_stays_in_the_activity_and_only_a_numbered_root_on_a_file_takes_a_reply() {
    let comment = |id: &str, extra: serde_json::Value| {
        let mut value = json!({"id":id,"author":"bo","body":"Hi","bodyBlocks":[],"createdAt":"2026-01-02T00:00:00Z"});
        for (key, item) in extra.as_object().unwrap() {
            value[key] = item.clone();
        }
        value
    };
    let pr = detail(json!({"comments":[
        comment("1", json!({})),
        comment("2", json!({"path":"logo.png"})),
        comment("3", json!({"path":"a.rs","line":4,"side":"LEFT"})),
        comment("4", json!({"path":"a.rs","line":4,"side":"LEFT","replyToId":3})),
        comment("IC_node", json!({"path":"a.rs"})),
    ]}));
    let listed: Vec<_> = (activity_rows(&pr).into_iter())
        .filter_map(|row| match row {
            ActivityRow::Comment { comment, .. } => Some((comment.id.clone(), comment.reply_id())),
            _ => None,
        })
        .collect();
    let expected = [
        ("1", None),
        ("2", Some(2)),
        ("3", Some(3)),
        ("4", None),
        ("IC_node", None),
    ]
    .map(|(id, reply)| (id.to_string(), reply));
    assert_eq!(listed, expected);
}
#[test]
fn the_side_column_raises_each_words_first_letter() {
    assert_eq!(
        capitalized(&words_of("CHANGES_REQUESTED")),
        "Changes Requested"
    );
    assert_eq!(capitalized(""), "");
}
#[test]
fn a_merge_says_who_or_someone_when_github_does_not() {
    assert_eq!(merged_words("ana"), "ana merged this pull request");
    assert_eq!(merged_words(""), "Someone merged this pull request");
}
#[test]
fn a_repository_shows_by_its_short_name() {
    assert_eq!(repo_name("jcoble/Assembly"), "Assembly");
    assert_eq!(repo_name("Assembly"), "Assembly");
}
