//! The web pages the app opens itself rather than in the browser, by the old app's rules (`parseGithubPullRequestUrl`
//! and `parseNotionPageId`): a GitHub pull request and a Notion page. The one parser for both, wherever a link comes
//! from.

use super::external_urls::canonical_notion_id;

/// `github.com/<owner>/<repo>/pull/<n>`, http or https, with or without `www.`, and any trailing path, query or
/// fragment: the repository as `owner/repo` and the number.
pub fn pull_request(url: &url::Url) -> Option<(String, u64)> {
    if !plain_web(url) || !matches!(url.host_str()?, "github.com" | "www.github.com") {
        return None;
    }
    let mut parts = url.path_segments()?;
    let (owner, repo) = (parts.next()?, parts.next()?);
    let (pull, number) = (parts.next()?, parts.next()?);
    let named = |part: &str| {
        !part.is_empty()
            && part
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '.' | '-'))
    };
    if !named(owner)
        || !named(repo)
        || pull != "pull"
        || !number.chars().all(|c| c.is_ascii_digit())
    {
        return None;
    }
    let number = number.parse().ok().filter(|number| *number > 0)?;
    Some((format!("{owner}/{repo}"), number))
}

/// A page on `notion.so`, `www.notion.so`, `app.notion.com` or a `*.notion.site`, http or https: the page id that ends
/// its last path segment, alone or after a title and a dash, in the canonical dashed form.
pub fn notion_page(url: &url::Url) -> Option<String> {
    let host = url.host_str()?;
    let site = host.strip_suffix(".notion.site").is_some_and(|name| {
        !name.is_empty()
            && name
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '-'))
    });
    if !plain_web(url)
        || !(site || matches!(host, "notion.so" | "www.notion.so" | "app.notion.com"))
    {
        return None;
    }
    let last = url.path_segments()?.next_back()?;
    [36, 32].into_iter().find_map(|length| {
        let at = last.len().checked_sub(length)?;
        let (title, id) = (last.get(..at)?, last.get(at..)?);
        (title.is_empty() || title.ends_with('-'))
            .then(|| canonical_notion_id(id))
            .flatten()
    })
}

/// Typed or pasted text that is a web address, http or https with no credentials, as every link here is read.
pub fn web_address(text: &str) -> Option<url::Url> {
    url::Url::parse(text.trim()).ok().filter(plain_web)
}

/// A Notion page's address on the web, as the old app built it for a task link.
pub fn notion_web_page(page_id: &str) -> String {
    format!("https://app.notion.com/p/{}", page_id.replace('-', ""))
}

/// http or https with no user name or password: a link carrying credentials is never one of the app's own pages,
/// wherever it comes from.
fn plain_web(url: &url::Url) -> bool {
    matches!(url.scheme(), "http" | "https")
        && url.username().is_empty()
        && url.password().is_none()
}
