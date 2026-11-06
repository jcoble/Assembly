use super::{CheckTone, PrTarget, PullRequestPage, PullRequestSummary};

#[test]
fn a_pasted_link_selects_only_an_actual_project_repository() {
    let pasted = |text: &str| PrTarget::parse_url(text).unwrap();
    let target = pasted("https://github.com/Owner/Repo/pull/42/files#diff")
        .among(&["owner/repo".into()])
        .unwrap();
    assert_eq!(target.repository, "owner/repo");
    assert_eq!(target.number, 42);
    let elsewhere = pasted("https://github.com/other/repo/pull/42");
    assert_eq!(elsewhere.clone().among(&["owner/repo".into()]), None);
    assert_eq!(
        elsewhere.web_page(),
        "https://github.com/other/repo/pull/42"
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
        assert_eq!(
            PrTarget::parse_url(link),
            Err("Paste a GitHub pull request link.".into()),
            "{link}"
        );
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
fn an_empty_queue_does_not_remove_the_structural_target_needed_for_backend_checkout_validation() {
    let target = PrTarget::parse_url("https://github.com/actual/checkout/pull/7").unwrap();
    assert_eq!(target.repository, "actual/checkout");
    assert_eq!(target.number, 7);
    assert_eq!(target.among(&[]), None);
}
#[test]
fn a_row_says_its_checks_only_when_the_backend_did() {
    let pr = |checks: Option<&str>, summary: Option<&str>| PullRequestSummary {
        checks: checks.map(String::from),
        check_summary: summary.map(String::from),
        ..PullRequestSummary::default()
    };
    assert_eq!(pr(None, None).check_line(), None);
    assert_eq!(
        pr(Some("none"), Some("ignored")).check_line(),
        Some(("no checks".into(), CheckTone::Quiet))
    );
    assert_eq!(
        pr(Some("failing"), None).check_line(),
        Some(("checks failing".into(), CheckTone::Failing))
    );
    assert_eq!(
        pr(Some("passing"), Some("3 of 3 passed")).check_line(),
        Some(("3 of 3 passed".into(), CheckTone::Passing))
    );
    assert_eq!(
        pr(Some("pending"), None).check_line(),
        Some(("checks pending".into(), CheckTone::Pending))
    );
}
