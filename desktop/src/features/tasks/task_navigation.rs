//! Where a link on a task page goes, from the URL alone. Nothing is guessed: a file link needs a session to resolve
//! against, a Notion page with no connection stays a web page, and anything else that isn't http or https is refused.

use super::task_export::NotionTaskRef;
use crate::core::navigation::external_urls::{ExternalRoute, canonical_notion_id, parse_external};
use crate::core::navigation::web_links::{notion_page, pull_request};

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

#[derive(Clone, Debug, Default)]
pub struct RouteContext {
    /// The connected data source.
    pub source: Option<String>,
}

/// Whether a link `route_url` refused is one to hand on to the app's link handling, and as what. A task link never is,
/// so it can't come back here.
pub fn passes_on(address: &str) -> Option<ExternalRoute> {
    match parse_external(address) {
        ExternalRoute::Unsupported | ExternalRoute::Task { .. } => None,
        route => Some(route),
    }
}

/// Where a link goes, or `None` when it's refused.
pub fn route_url(address: &str, context: &RouteContext) -> Option<TaskRoute> {
    if address.chars().any(|c| c.is_control() || c.is_whitespace()) {
        return None;
    }
    let url = url::Url::parse(address).ok()?;
    if !url.username().is_empty() || url.password().is_some() {
        return None;
    }
    match url.scheme() {
        "assembly" if url.host_str() == Some("task") => {
            let page_id = url.path().strip_prefix('/').and_then(canonical_notion_id)?;
            let Some(data_source_id) = context.source.clone() else {
                return Some(TaskRoute::Web(notion_web_page(&page_id)));
            };
            Some(TaskRoute::Task(NotionTaskRef {
                data_source_id,
                page_id,
            }))
        }
        "file" if url.host_str().is_none_or(str::is_empty) => {
            let path = url.to_file_path().ok()?;
            Some(TaskRoute::File {
                path: path.to_string_lossy().into_owned(),
            })
        }
        "http" | "https" => Some(web_route(&url, context)),
        _ => None,
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

/// A task's page on the Notion website, as the old app opened a task link with no connection.
pub fn notion_web_page(page_id: &str) -> String {
    format!("https://app.notion.com/p/{}", page_id.replace('-', ""))
}
