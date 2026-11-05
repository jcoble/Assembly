use super::task_export::NotionTaskRef;
use super::task_navigation::*;

const ID: &str = "11111111-1111-1111-1111-111111111111";

fn context() -> RouteContext {
    RouteContext {
        source: Some("source-a".into()),
    }
}

fn task_ref(source: &str, page: &str) -> NotionTaskRef {
    NotionTaskRef {
        data_source_id: source.into(),
        page_id: page.into(),
    }
}

#[test]
fn task_url_routes_to_task_without_local_file_guess() {
    assert_eq!(
        route_url(
            "assembly://task/11111111111111111111111111111111",
            &context()
        ),
        Ok(TaskRoute::Task(task_ref("source-a", ID)))
    );
}

#[test]
fn a_task_url_with_no_connection_says_so() {
    assert_eq!(
        route_url(
            "assembly://task/11111111111111111111111111111111",
            &RouteContext::default()
        ),
        Err(RouteRefused::NotConnected)
    );
}

#[test]
fn a_notion_page_keeps_its_address_for_when_it_is_not_a_task() {
    let url = "https://www.notion.so/Some-Title-11111111111111111111111111111111";
    assert_eq!(
        route_url(url, &context()),
        Ok(TaskRoute::NotionPage {
            source: task_ref("source-a", ID),
            url: url.into()
        })
    );
}

#[test]
fn a_notion_page_with_no_connection_is_a_web_page() {
    let url = "https://app.notion.com/p/11111111111111111111111111111111";
    assert_eq!(
        route_url(url, &RouteContext::default()),
        Ok(TaskRoute::Web(url.into()))
    );
}

#[test]
fn a_github_pull_request_goes_to_the_pull_requests_tab() {
    assert_eq!(
        route_url(
            "https://github.com/jcoble/Assembly/pull/125/files",
            &context()
        ),
        Ok(TaskRoute::PullRequest {
            repo: "jcoble/Assembly".into(),
            number: 125
        })
    );
    assert_eq!(
        route_url("https://github.com/jcoble/Assembly/issues/125", &context()),
        Ok(TaskRoute::Web(
            "https://github.com/jcoble/Assembly/issues/125".into()
        ))
    );
}

#[test]
fn a_file_link_is_a_file() {
    assert_eq!(
        route_url("file:///tmp/a%20b.rs", &context()),
        Ok(TaskRoute::File {
            path: "/tmp/a b.rs".into()
        })
    );
    assert_eq!(
        route_url("file://elsewhere/tmp/a.rs", &context()),
        Err(RouteRefused::Unsupported)
    );
}

#[test]
fn a_plain_web_page_stays_one() {
    assert_eq!(
        route_url("https://example.com/a?b=c", &context()),
        Ok(TaskRoute::Web("https://example.com/a?b=c".into()))
    );
}

#[test]
fn unsafe_links_are_refused() {
    for url in [
        "javascript:alert(1)",
        "ftp://example.com/a",
        "https://user:pw@example.com/",
        "https://example.com/a b",
        "assembly://other/thing",
        "ssh://github.com/a",
    ] {
        assert_eq!(
            route_url(url, &context()),
            Err(RouteRefused::Unsupported),
            "{url}"
        );
    }
}

// Spec section 6: a link on a task page that `route_url` refuses, handed on to the app's link handling.
#[test]
fn a_task_page_hands_on_links_the_app_accepts() {
    use crate::core::navigation::external_urls::ExternalRoute;
    assert_eq!(
        passes_on("assembly://session/s1", RouteRefused::Unsupported),
        Some(ExternalRoute::Session {
            id: "s1".into(),
            machine: None
        })
    );
    assert_eq!(
        passes_on(
            "assembly://open?url=https%3A%2F%2Fa.test",
            RouteRefused::Unsupported
        ),
        Some(ExternalRoute::Open {
            url: "https://a.test".into()
        })
    );
    assert_eq!(
        passes_on("assembly://file?path=%2Fa", RouteRefused::Unsupported),
        Some(ExternalRoute::File {
            path: "/a".into(),
            machine: None
        })
    );
}

#[test]
fn a_task_page_hands_on_nothing_else_so_a_task_link_can_not_loop() {
    assert_eq!(
        passes_on(
            "assembly://task/3f4394b0689d81b19819c696dad2fd48",
            RouteRefused::NotConnected
        ),
        None
    );
    assert_eq!(
        passes_on("assembly://nothing", RouteRefused::Unsupported),
        None
    );
    assert_eq!(
        passes_on("file:///etc/passwd", RouteRefused::Unsupported),
        None
    );
}
