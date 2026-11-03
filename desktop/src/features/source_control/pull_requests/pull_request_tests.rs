use super::{PrTarget, PullRequestPage};

#[test]
fn a_pasted_link_selects_only_an_actual_project_repository() {
    let target = PrTarget::from_url(
        "https://github.com/Owner/Repo/pull/42/files#diff",
        &["owner/repo".into()],
    )
    .unwrap();
    assert_eq!(target.repository, "owner/repo");
    assert_eq!(target.number, 42);
    assert_eq!(
        PrTarget::from_url(
            "https://github.com/other/repo/pull/42",
            &["owner/repo".into()]
        )
        .unwrap_err(),
        "That PR's repository isn't a project in Assembly."
    );
}
#[test]
fn malformed_or_other_host_links_cannot_select_a_target() {
    for link in [
        "https://github.com.evil.test/o/r/pull/1",
        "https://gitlab.com/o/r/pull/1",
        "https://github.com/o/r/issues/1",
        "https://github.com/o/r/pull/0",
        "https://github.com/o/r/pull/no",
        "file:///o/r/pull/1",
    ] {
        assert!(PrTarget::from_url(link, &["o/r".into()]).is_err(), "{link}");
    }
}
#[test]
fn pages_retain_total_and_cursor_and_deduplicate_by_repository_and_number() {
    let mut page:PullRequestPage=serde_json::from_value(serde_json::json!({"items":[{"repository":"o/r","number":1,"title":"First"}],"cursor":"next","total":31})).unwrap();
    let next=serde_json::from_value(serde_json::json!({"items":[{"repository":"O/R","number":1,"title":"First"},{"repository":"other/r","number":1,"title":"Other"}],"cursor":null,"total":31})).unwrap();
    page.append(next);
    assert_eq!(page.items.len(), 2);
    assert_eq!(page.total, 31);
    assert!(page.cursor.is_none());
}

#[test]
fn binary_or_omitted_patch_is_distinct_from_no_changes() {
    let binary:super::PullRequestFile=serde_json::from_value(serde_json::json!({"path":"image.png","previousPath":null,"status":"modified","additions":0,"deletions":0,"patch":null,"patchOmitted":false})).unwrap();
    assert_eq!(binary.patch_status(), "No patch available");
    let empty = super::PullRequestFile {
        patch: Some(String::new()),
        ..binary
    };
    assert_eq!(empty.patch_status(), "No changes");
}

#[test]
fn an_empty_queue_does_not_remove_the_structural_target_needed_for_backend_checkout_validation() {
    let target = PrTarget::parse_url("https://github.com/actual/checkout/pull/7").unwrap();
    assert_eq!(target.repository, "actual/checkout");
    assert_eq!(target.number, 7);
    assert!(PrTarget::from_url("https://github.com/actual/checkout/pull/7", &[]).is_err());
}
