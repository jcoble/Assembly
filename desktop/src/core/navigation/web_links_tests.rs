//! Feature 28 parity: the web pages the app opens itself, by the old app's rules (`parseGithubPullRequestUrl` and
//! `parseNotionPageId` in workbenchNavigation.ts).

use super::web_links::{notion_page, pull_request};

const ID: &str = "11111111-1111-1111-1111-111111111111";
const HEX: &str = "11111111111111111111111111111111";

fn parsed(address: &str) -> url::Url {
    url::Url::parse(address).expect("a URL")
}

#[test]
fn a_pull_request_link_names_its_repository_and_number_with_any_trailing_path() {
    for address in [
        "https://github.com/o/r/pull/7",
        "http://github.com/o/r/pull/7",
        "https://www.github.com/o/r/pull/7",
        "https://GitHub.com/o/r/pull/7/files?a=b#diff",
        "https://github.com/o/r/pull/7/",
        "https://github.com/o/r/pull/007",
    ] {
        assert_eq!(
            pull_request(&parsed(address)),
            Some(("o/r".into(), 7)),
            "{address}"
        );
    }
    assert_eq!(
        pull_request(&parsed("https://github.com/my-org/my.repo_1/pull/12")),
        Some(("my-org/my.repo_1".into(), 12))
    );
}

#[test]
fn anything_else_on_or_off_github_is_not_a_pull_request() {
    for address in [
        "https://github.com/o/r/issues/7",
        "https://github.com/o/r/pull/0",
        "https://github.com/o/r/pull/7x",
        "https://github.com/o/r/pull/+7",
        "https://github.com/o/r/pull/",
        "https://github.com/o/pull/7",
        "https://github.com.evil.test/o/r/pull/7",
        "https://gist.github.com/o/r/pull/7",
        "https://gitlab.com/o/r/pull/7",
        "ftp://github.com/o/r/pull/7",
        "https://github.com/o%20x/r/pull/7",
    ] {
        assert_eq!(pull_request(&parsed(address)), None, "{address}");
    }
}

#[test]
fn a_notion_page_on_any_notion_host_names_its_page() {
    for address in [
        format!("https://www.notion.so/Some-Title-{HEX}"),
        format!("https://notion.so/{HEX}"),
        format!("http://app.notion.com/p/{HEX}"),
        format!("https://acme.notion.site/Plan-{HEX}?pvs=4"),
        format!("https://my-team.notion.site/{ID}"),
        format!("https://www.notion.so/Title-{ID}"),
        format!("https://www.notion.so/Title-{}", HEX.to_uppercase()),
    ] {
        assert_eq!(
            notion_page(&parsed(&address)).as_deref(),
            Some(ID),
            "{address}"
        );
    }
}

#[test]
fn other_hosts_and_shapes_are_not_notion_pages() {
    for address in [
        format!("https://notion.site/{HEX}"),
        format!("https://a.b.notion.site/{HEX}"),
        format!("https://evil.test/{HEX}"),
        format!("https://notion.so.evil.test/{HEX}"),
        format!("https://www.notion.so/Title{HEX}"),
        format!("https://www.notion.so/{HEX}/"),
        "https://www.notion.so/Title-1234".to_string(),
        format!("ftp://www.notion.so/{HEX}"),
    ] {
        assert_eq!(notion_page(&parsed(&address)), None, "{address}");
    }
}
