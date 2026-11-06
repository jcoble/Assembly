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
        Some(TaskRoute::Task(task_ref("source-a", ID)))
    );
}

// Feature 28 parity item 3: with Notion not connected, a task link opens its Notion page, as the old app did.
#[test]
fn a_task_url_with_no_connection_is_its_notion_page() {
    assert_eq!(
        route_url(
            "assembly://task/11111111-1111-1111-1111-111111111111",
            &RouteContext::default()
        ),
        Some(TaskRoute::Web(
            "https://app.notion.com/p/11111111111111111111111111111111".into()
        ))
    );
}

#[test]
fn a_notion_page_keeps_its_address_for_when_it_is_not_a_task() {
    let url = "https://www.notion.so/Some-Title-11111111111111111111111111111111";
    assert_eq!(
        route_url(url, &context()),
        Some(TaskRoute::NotionPage {
            source: task_ref("source-a", ID),
            url: url.into()
        })
    );
}

// Item 3: any Notion host the old app knew, over http or https.
#[test]
fn a_notion_page_on_any_notion_host_is_a_task_to_try() {
    for url in [
        "http://notion.so/11111111111111111111111111111111",
        "https://acme.notion.site/Plan-11111111111111111111111111111111",
    ] {
        assert_eq!(
            route_url(url, &context()),
            Some(TaskRoute::NotionPage {
                source: task_ref("source-a", ID),
                url: url.into()
            }),
            "{url}"
        );
    }
}

#[test]
fn a_notion_page_with_no_connection_is_a_web_page() {
    let url = "https://app.notion.com/p/11111111111111111111111111111111";
    assert_eq!(
        route_url(url, &RouteContext::default()),
        Some(TaskRoute::Web(url.into()))
    );
}

// Item 2: the one shared parser, so http and www links count too.
#[test]
fn a_pull_request_link_by_the_old_rules_goes_to_the_pull_requests_tab() {
    for url in [
        "http://github.com/jcoble/Assembly/pull/125",
        "https://www.github.com/jcoble/Assembly/pull/125/files",
    ] {
        assert_eq!(
            route_url(url, &context()),
            Some(TaskRoute::PullRequest {
                repo: "jcoble/Assembly".into(),
                number: 125
            }),
            "{url}"
        );
    }
}

#[test]
fn a_github_pull_request_goes_to_the_pull_requests_tab() {
    assert_eq!(
        route_url(
            "https://github.com/jcoble/Assembly/pull/125/files",
            &context()
        ),
        Some(TaskRoute::PullRequest {
            repo: "jcoble/Assembly".into(),
            number: 125
        })
    );
    assert_eq!(
        route_url("https://github.com/jcoble/Assembly/issues/125", &context()),
        Some(TaskRoute::Web(
            "https://github.com/jcoble/Assembly/issues/125".into()
        ))
    );
}

// Brief item 5: a `file:` link isn't the task page's own; it's handed on as the same link from the system is.
#[test]
fn a_file_link_is_handed_on_as_a_file() {
    use crate::core::navigation::external_urls::ExternalRoute;
    assert_eq!(route_url("file:///tmp/a%20b.rs", &context()), None);
    assert_eq!(
        passes_on("file:///tmp/a%20b.rs"),
        Some(ExternalRoute::File {
            path: "/tmp/a b.rs".into(),
            machine: None
        })
    );
    assert_eq!(passes_on("file://elsewhere/tmp/a.rs"), None);
}

#[test]
fn a_plain_web_page_stays_one() {
    assert_eq!(
        route_url("https://example.com/a?b=c", &context()),
        Some(TaskRoute::Web("https://example.com/a?b=c".into()))
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
        assert_eq!(route_url(url, &context()), None, "{url}");
    }
}

// Spec section 6: a link on a task page that `route_url` refuses, handed on to the app's link handling.
#[test]
fn a_task_page_hands_on_links_the_app_accepts() {
    use crate::core::navigation::external_urls::ExternalRoute;
    assert_eq!(
        passes_on("assembly://session/s1"),
        Some(ExternalRoute::Session {
            id: "s1".into(),
            machine: None
        })
    );
    assert_eq!(
        passes_on("assembly://open?url=https%3A%2F%2Fa.test"),
        Some(ExternalRoute::Open {
            url: "https://a.test".into()
        })
    );
    assert_eq!(
        passes_on("assembly://file?path=%2Fa"),
        Some(ExternalRoute::File {
            path: "/a".into(),
            machine: None
        })
    );
}

#[test]
fn a_task_page_hands_on_nothing_else_so_a_task_link_can_not_loop() {
    assert_eq!(
        passes_on("assembly://task/3f4394b0689d81b19819c696dad2fd48"),
        None
    );
    assert_eq!(passes_on("assembly://nothing"), None);
}
