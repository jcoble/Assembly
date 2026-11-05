//! The Files tab's filter: names in the shown folder that hold what's typed (`files/search`, mode `name`). Typing waits
//! `DEBOUNCE`, then searches. The wait and the call are one task the filter holds, so a newer query, another folder or
//! hiding the tab drops the older one, and its run on the backend is released. Replaces the Search tab's name search.

use std::time::Duration;

use gpui_kit::*;
use serde_json::json;

use super::entry::{FileOwnership, NameMatch, SearchResult};
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::row_source::ExpectedRowSource;

/// How long typing waits before it searches.
pub const DEBOUNCE: Duration = Duration::from_millis(150);
/// Results per page.
const PAGE: u32 = 100;

/// The folder the filter searches, as the Files tab shows it.
#[derive(Clone, Debug, PartialEq)]
pub struct FilterTarget {
    pub session: String,
    pub source: Option<ExpectedRowSource>,
    pub owner: Option<FileOwnership>,
    pub include_excluded: bool,
}

#[derive(Default)]
pub struct FileFilter {
    /// What's typed, trimmed.
    query: String,
    target: Option<FilterTarget>,
    shown: bool,
    matches: Vec<NameMatch>,
    /// The backend's run, while it has more pages.
    next: Option<String>,
    error: Option<String>,
    generation: u64,
    /// The search or page being loaded, with its wait; dropping it cancels its call.
    searching: Option<Task<()>>,
}

impl FileFilter {
    pub fn active(&self) -> bool {
        !self.query.is_empty()
    }
    #[cfg(test)]
    pub fn query(&self) -> &str {
        &self.query
    }
    pub fn matches(&self) -> &[NameMatch] {
        &self.matches
    }
    pub fn error(&self) -> Option<&str> {
        self.error.as_deref()
    }
    pub fn searching(&self) -> bool {
        self.searching.is_some()
    }
    pub fn has_more(&self) -> bool {
        self.next.is_some()
    }
    /// Whether there is a folder to search at all.
    pub fn available(&self) -> bool {
        self.target.is_some()
    }
    #[cfg(test)]
    pub fn target(&self) -> Option<&FilterTarget> {
        self.target.as_ref()
    }
    /// The backend's run for the results shown, while it has more pages.
    #[cfg(test)]
    pub fn run_id(&self) -> Option<&str> {
        self.next.as_deref()
    }

    pub fn set_query(&mut self, query: &str, cx: &mut Context<Self>) {
        let query = query.trim();
        if query != self.query {
            self.query = query.to_string();
            self.run(DEBOUNCE, cx);
        }
    }

    /// Follows the tab's folder; another folder, source, owner or excluded setting searches again at once.
    pub fn set_target(&mut self, target: Option<FilterTarget>, cx: &mut Context<Self>) {
        if target != self.target {
            self.target = target;
            self.run(Duration::ZERO, cx);
        }
    }

    pub fn set_shown(&mut self, shown: bool, cx: &mut Context<Self>) {
        if shown == self.shown {
            return;
        }
        self.shown = shown;
        if shown {
            self.run(Duration::ZERO, cx);
        } else {
            self.generation = self.generation.wrapping_add(1);
            self.release(cx);
        }
    }

    pub fn clear(&mut self, cx: &mut Context<Self>) {
        if self.active() {
            self.query.clear();
            self.run(Duration::ZERO, cx);
        }
    }

    pub fn more(&mut self, cx: &mut Context<Self>) {
        if let (Some(cursor), None) = (self.next.clone(), &self.searching) {
            self.load(Some(cursor), Duration::ZERO, cx);
            cx.notify();
        }
    }

    /// Starts again after `wait`, dropping and releasing the run before.
    fn run(&mut self, wait: Duration, cx: &mut Context<Self>) {
        self.release(cx);
        self.generation = self.generation.wrapping_add(1);
        self.matches.clear();
        self.error = None;
        if self.shown && self.active() && self.target.is_some() {
            self.load(None, wait, cx);
        }
        cx.notify();
    }

    fn load(&mut self, cursor: Option<String>, wait: Duration, cx: &mut Context<Self>) {
        let Some(target) = self.target.clone() else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            self.error = Some("The backend isn't running.".into());
            return;
        };
        let generation = self.generation;
        let args = json!({ "session": target.session, "query": self.query, "mode": "name", "searchId": cursor,
            "pageSize": PAGE, "includeExcluded": target.include_excluded, "expectedSource": target.source,
            "expectedOwner": target.owner });
        self.searching = Some(cx.spawn(async move |filter, cx| {
            if !wait.is_zero() {
                cx.background_executor().timer(wait).await;
            }
            let Ok(call) = filter.update(cx, |_, cx| {
                socket
                    .read(cx)
                    .call::<SearchResult>("files", "search", args, None)
            }) else {
                return;
            };
            let outcome = call.await;
            let _ = filter.update(cx, |filter, cx| {
                if !filter.shown || filter.generation != generation {
                    return;
                }
                filter.searching = None;
                match outcome {
                    Outcome::Ok(page) => {
                        let mut paths: std::collections::HashSet<_> =
                            filter.matches.iter().map(|m| m.path.clone()).collect();
                        filter.matches.extend(
                            page.names
                                .into_iter()
                                .filter(|m| paths.insert(m.path.clone())),
                        );
                        filter.next = page.more.then_some(page.search_id);
                    }
                    Outcome::Failed(failure) if failure.message.contains("cursor_changed") => {
                        filter.next = None;
                        filter.run(Duration::ZERO, cx);
                        return;
                    }
                    Outcome::Failed(failure) => filter.error = Some(failure.message),
                    Outcome::Aborted => {
                        filter.error = Some("The connection dropped; type again to retry.".into())
                    }
                }
                cx.notify();
            });
        }));
    }

    fn release(&mut self, cx: &mut Context<Self>) {
        self.searching = None;
        if let (Some(search_id), Some(socket)) = (self.next.take(), SocketClient::try_global(cx)) {
            let call = socket.read(cx).call::<serde_json::Value>(
                "files",
                "releaseSearch",
                json!({ "searchId": search_id }),
                None,
            );
            call.ignore_reply();
        }
    }
}
