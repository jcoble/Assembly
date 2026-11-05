//! Where a link on a task page goes, from the URL alone. Nothing is guessed: a file link needs a session to resolve
//! against, an unknown Notion page stays a web page, and anything else that isn't http or https is refused.

use super::task_export::NotionTaskRef;
use crate::core::navigation::external_urls::canonical_notion_id;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum TaskRoute {
    /// A task in the connected source.
    Task(NotionTaskRef),
    /// Looks like a Notion page; it may not be in the connected source, so it keeps its address to open instead.
    NotionPage {
        source: NotionTaskRef,
        url: String,
    },
    PullRequest {
        repo: String,
        number: u64,
    },
    File {
        path: String,
    },
    Web(String),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RouteRefused {
    NotConnected,
    Unsupported,
}

#[derive(Clone, Debug, Default)]
pub struct RouteContext {
    /// The connected data source.
    pub source: Option<String>,
}

/// Whether a link `route_url` refused is one to hand on to the app's link handling, and as what.
pub fn passes_on(
    _address: &str,
    _refused: RouteRefused,
) -> Option<crate::core::navigation::external_urls::ExternalRoute> {
    None
}

pub fn route_url(address: &str, context: &RouteContext) -> Result<TaskRoute, RouteRefused> {
    if address.chars().any(|c| c.is_control() || c.is_whitespace()) {
        return Err(RouteRefused::Unsupported);
    }
    let url = url::Url::parse(address).map_err(|_| RouteRefused::Unsupported)?;
    if !url.username().is_empty() || url.password().is_some() {
        return Err(RouteRefused::Unsupported);
    }
    match url.scheme() {
        "assembly" if url.host_str() == Some("task") => {
            let page_id = url
                .path()
                .strip_prefix('/')
                .and_then(canonical_notion_id)
                .ok_or(RouteRefused::Unsupported)?;
            let data_source_id = context.source.clone().ok_or(RouteRefused::NotConnected)?;
            Ok(TaskRoute::Task(NotionTaskRef {
                data_source_id,
                page_id,
            }))
        }
        "file" if url.host_str().is_none_or(str::is_empty) => {
            let path = url.to_file_path().map_err(|_| RouteRefused::Unsupported)?;
            Ok(TaskRoute::File {
                path: path.to_string_lossy().into_owned(),
            })
        }
        "http" | "https" => Ok(web_route(&url, context)),
        _ => Err(RouteRefused::Unsupported),
    }
}

fn web_route(url: &url::Url, context: &RouteContext) -> TaskRoute {
    if let Some((repo, number)) = pull_request(url) {
        return TaskRoute::PullRequest { repo, number };
    }
    if let (Some(data_source_id), Some(page_id)) = (&context.source, notion_page(url)) {
        return TaskRoute::NotionPage {
            source: NotionTaskRef {
                data_source_id: data_source_id.clone(),
                page_id,
            },
            url: url.to_string(),
        };
    }
    TaskRoute::Web(url.to_string())
}

fn pull_request(url: &url::Url) -> Option<(String, u64)> {
    if url.scheme() != "https" || url.host_str() != Some("github.com") {
        return None;
    }
    let parts: Vec<&str> = url.path_segments()?.collect();
    let [owner, repo, "pull", number, ..] = parts.as_slice() else {
        return None;
    };
    Some((format!("{owner}/{repo}"), number.parse().ok()?))
}

/// The page id ends the last path segment, after any title (`Some-Title-<32 digits>`).
fn notion_page(url: &url::Url) -> Option<String> {
    let host = url.host_str()?;
    if url.scheme() != "https" || !matches!(host, "www.notion.so" | "notion.so" | "app.notion.com")
    {
        return None;
    }
    let last = url.path_segments()?.rfind(|part| !part.is_empty())?;
    let tail = last.len().checked_sub(32).and_then(|at| last.get(at..))?;
    canonical_notion_id(last).or_else(|| canonical_notion_id(tail))
}
