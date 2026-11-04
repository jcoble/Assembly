use super::external_urls::*;

const PAGE: &str = "40d394b0689d80bea989f918bf93b69f";
const CANONICAL: &str = "40d394b0-689d-80be-a989-f918bf93b69f";

#[test]
fn a_task_link_names_its_page() {
    let route = parse_external(&format!("assembly://task/{PAGE}"));
    assert_eq!(
        route,
        ExternalRoute::Task {
            page_id: CANONICAL.into()
        }
    );
    assert_eq!(
        parse_external(&format!("assembly://task/{CANONICAL}")),
        route
    );
}

#[test]
fn a_sign_in_callback_gives_its_state_and_code() {
    assert_eq!(
        parse_external("assembly://notion-oauth?state=abc&claimCode=def"),
        ExternalRoute::NotionOAuth {
            state: "abc".into(),
            claim_code: "def".into()
        }
    );
}

#[test]
fn a_callback_never_prints_its_secrets() {
    let route = parse_external("assembly://notion-oauth?state=abc&claimCode=def");
    assert_eq!(format!("{route:?}"), "NotionOAuth");
}

#[test]
fn malicious_or_unknown_links_are_refused() {
    for url in [
        "assembly://notion-oauth?state=abc",
        "assembly://notion-oauth?state=abc&claimCode=",
        "assembly://notion-oauth?state=a&state=b&claimCode=c",
        "assembly://notion-oauth?state=a&claimCode=c&extra=1",
        "assembly://user:pw@notion-oauth?state=a&claimCode=c",
        "assembly://notion-oauth/evil?state=a&claimCode=c",
        "assembly://task/not-an-id",
        "assembly://task/../../etc/passwd",
        "assembly://other/thing",
        "https://notion-oauth?state=a&claimCode=c",
        "file:///etc/passwd",
        "",
    ] {
        assert_eq!(parse_external(url), ExternalRoute::Unsupported, "{url}");
    }
}

#[test]
fn a_startup_link_is_queued_once() {
    let urls = ExternalUrls::new();
    urls.receive(vec!["assembly://task/a".into()]);
    urls.receive(vec!["assembly://task/a".into()]);
    assert_eq!(urls.take(), vec!["assembly://task/a".to_string()]);
    assert!(urls.take().is_empty());
}

#[test]
fn the_queue_is_bounded_by_count_and_size() {
    let urls = ExternalUrls::new();
    urls.receive(vec!["x".repeat(MAX_URL_BYTES + 1)]);
    assert!(urls.take().is_empty());
    urls.receive((0..MAX_QUEUED + 5).map(|at| at.to_string()).collect());
    assert_eq!(urls.take().len(), MAX_QUEUED);
}
