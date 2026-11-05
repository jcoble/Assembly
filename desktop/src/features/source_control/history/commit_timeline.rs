//! A visible history owner owns its page and commit reads; hiding or replacing it cancels them.
use super::{
    git_history::{GitCommitFile, GitHistory},
    graph_lanes::{LaneRow, lanes},
    history_filter::HistoryFilter,
};
use crate::core::intents::{open_commit_diff::OpenCommitDiff, open_file::OpenFile};
use crate::core::socket::{
    checkout_selector::CheckoutSelector, client::SocketClient, outcome::Outcome,
};
use gpui_kit::*;
use serde_json::json;
use std::collections::{HashMap, HashSet};

pub use table::{Uncommitted, uncommitted_of};
/// The list entries around the commits: the uncommitted row and the footer.
const ITEMS_AROUND: usize = 2;
pub(super) enum CommitFiles {
    Reading,
    Read(Vec<GitCommitFile>),
    Failed(String),
}
pub struct CommitTimeline {
    session: String,
    expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    /// A linked checkout to read instead of the session's own.
    checkout: Option<CheckoutSelector>,
    path: Option<String>,
    active: bool,
    pub(super) history: GitHistory,
    filter: HistoryFilter,
    visible: Vec<usize>,
    graph: Vec<LaneRow>,
    expanded: HashSet<String>,
    pub(super) files: HashMap<String, CommitFiles>,
    reading: bool,
    error: Option<String>,
    read: Option<Task<()>>,
    file_reads: HashMap<String, Task<()>>,
    list: ListState,
    /// Drawn in Source control's own scroller: no graph, and more only on Load more.
    inline: bool,
    pages: u32,
    uncommitted: Option<Uncommitted>,
    /// The commit file last picked, marked in its row.
    chosen: Option<(String, String)>,
}
impl CommitTimeline {
    /// The list as Source control draws it inside its one scroller.
    pub fn new_inline(
        session: String,
        expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
        active: bool,
        cx: &mut Context<Self>,
    ) -> Self {
        let mut timeline = Self::build(session, None, expected_source, active, true, cx);
        if active {
            timeline.refresh(cx);
        }
        timeline
    }
    pub fn new(
        session: String,
        path: Option<String>,
        expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
        active: bool,
        cx: &mut Context<Self>,
    ) -> Self {
        let mut timeline = Self::build(session, path, expected_source, active, false, cx);
        if active {
            timeline.refresh(cx);
        }
        timeline
    }
    fn build(
        session: String,
        path: Option<String>,
        expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
        active: bool,
        inline: bool,
        cx: &mut Context<Self>,
    ) -> Self {
        // The table's list: the uncommitted row, each commit, then the footer.
        let list = ListState::new(ITEMS_AROUND, ListAlignment::Top, px(200.));
        if !inline {
            let weak = cx.entity().downgrade();
            // The list holds its own state while it calls this, so ask it nothing and load once it has let go.
            list.set_scroll_handler(move |event, _, cx| {
                if event.visible_range.end >= event.count {
                    let weak = weak.clone();
                    cx.defer(move |cx| {
                        _ = weak.update(cx, |timeline, cx| timeline.load_more(cx));
                    });
                }
            });
        }
        Self {
            session,
            expected_source,
            checkout: None,
            path,
            active,
            history: GitHistory::default(),
            filter: HistoryFilter::default(),
            visible: Vec::new(),
            graph: Vec::new(),
            expanded: HashSet::new(),
            files: HashMap::new(),
            reading: false,
            error: None,
            read: None,
            file_reads: HashMap::new(),
            list,
            inline,
            pages: 0,
            uncommitted: None,
            chosen: None,
        }
    }
    pub fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        self.active = active;
        if active {
            self.refresh(cx);
        } else {
            self.read = None;
            self.reading = false;
            self.file_reads.clear();
            self.files
                .retain(|_, files| !matches!(files, CommitFiles::Reading));
        }
        cx.notify();
    }
    pub fn set_path(&mut self, path: Option<String>, cx: &mut Context<Self>) {
        if self.path != path {
            self.path = path;
            self.refresh(cx);
        }
    }
    pub fn set_target(
        &mut self,
        path: Option<String>,
        source: Option<crate::core::socket::row_source::ExpectedRowSource>,
        cx: &mut Context<Self>,
    ) {
        if self.path != path || self.expected_source != source {
            self.path = path;
            self.expected_source = source;
            self.refresh(cx);
        }
    }
    pub fn set_checkout(&mut self, checkout: Option<CheckoutSelector>, cx: &mut Context<Self>) {
        if self.checkout != checkout {
            self.checkout = checkout;
            self.refresh(cx);
        }
    }
    pub(super) fn commit_diff(&self, sha: &str, path: &str, title: String) -> OpenCommitDiff {
        OpenCommitDiff {
            session_id: self.session.clone(),
            expected_source: self.expected_source.clone(),
            sha: sha.to_string(),
            path: path.to_string(),
            title,
            checkout: self.checkout.clone(),
        }
    }
    /// A file opens in the session's own folder; there's no way yet to open one in another checkout.
    pub(super) fn current_file(&self, path: &str) -> Option<OpenFile> {
        self.checkout.is_none().then(|| OpenFile {
            session_id: self.session.clone(),
            path: path.to_string(),
            expected_source: self.expected_source.clone(),
            line: None,
            column: None,
            pinned: false,
            outside: None,
        })
    }
    /// The history's own commits, for the toolbar's pickers and counts.
    pub fn shown_count(&self) -> usize {
        self.visible.len()
    }
    pub fn is_reading(&self) -> bool {
        self.reading
    }
    pub fn set_uncommitted(&mut self, uncommitted: Option<Uncommitted>, cx: &mut Context<Self>) {
        if self.uncommitted != uncommitted {
            self.uncommitted = uncommitted;
            self.list.splice(0..1, 1);
            cx.notify();
        }
    }
    pub fn set_filter(&mut self, filter: HistoryFilter, cx: &mut Context<Self>) {
        self.filter = filter;
        self.sync_rows();
        cx.notify();
    }
    pub fn refresh(&mut self, cx: &mut Context<Self>) {
        self.read = None;
        self.reading = false;
        self.file_reads.clear();
        self.files.clear();
        self.expanded.clear();
        self.chosen = None;
        self.history = GitHistory::default();
        self.pages = 0;
        self.sync_rows();
        if self.active {
            self.request(None, cx);
        }
        cx.notify();
    }
    pub fn load_more(&mut self, cx: &mut Context<Self>) {
        if self.active
            && !self.reading
            && let Some(cursor) = self.history.next_cursor.clone()
        {
            self.request(Some(cursor), cx);
        }
    }
    fn request(&mut self, cursor: Option<String>, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            self.error = Some("The backend isn't running.".into());
            cx.notify();
            return;
        };
        let key = format!("git/history/{}", cx.entity_id());
        let args = CheckoutSelector::applied(
            self.checkout.as_ref(),
            json!({"session":self.session,"cursor":cursor,"path":self.path,"expectedSource":self.expected_source}),
        );
        let call = socket
            .read(cx)
            .call::<GitHistory>("git", "history", args, Some(&key));
        self.reading = true;
        self.error = None;
        self.read = Some(cx.spawn(async move |timeline, cx| {
            let outcome = call.await;
            _ = timeline.update(cx, |timeline, cx| {
                timeline.reading = false;
                timeline.read = None;
                match outcome {
                    Outcome::Ok(page) => {
                        timeline.pages += 1;
                        timeline.history.append(page);
                        timeline.sync_rows();
                    }
                    Outcome::Failed(failure) => timeline.error = Some(failure.message),
                    Outcome::Aborted => timeline.error = Some("The connection dropped.".into()),
                }
                cx.notify();
            });
        }));
        cx.notify();
    }
    pub(super) fn toggle_commit(&mut self, sha: String, cx: &mut Context<Self>) {
        let opened = sha.clone();
        if !self.expanded.insert(sha.clone()) {
            self.expanded.remove(&sha);
            self.file_reads.remove(&sha);
            self.files
                .retain(|key, files| key != &sha || !matches!(files, CommitFiles::Reading));
            self.remeasure(&sha);
            cx.notify();
            return;
        }
        if !self.files.contains_key(&sha)
            && let Some(socket) = SocketClient::try_global(cx)
        {
            let key = format!("git/commitFiles/{}/{sha}", cx.entity_id());
            let args = CheckoutSelector::applied(
                self.checkout.as_ref(),
                json!({"session":self.session,"sha":sha,"expectedSource":self.expected_source}),
            );
            let call =
                socket
                    .read(cx)
                    .call::<Vec<GitCommitFile>>("git", "commitFiles", args, Some(&key));
            self.files.insert(sha.clone(), CommitFiles::Reading);
            let target = sha.clone();
            let measured = sha.clone();
            let task = cx.spawn(async move |timeline, cx| {
                let outcome = call.await;
                _ = timeline.update(cx, |timeline, cx| {
                    timeline.file_reads.remove(&target);
                    timeline.files.insert(
                        target,
                        match outcome {
                            Outcome::Ok(files) => CommitFiles::Read(files),
                            Outcome::Failed(failure) => CommitFiles::Failed(failure.message),
                            Outcome::Aborted => {
                                CommitFiles::Failed("The connection dropped.".into())
                            }
                        },
                    );
                    timeline.remeasure(&measured);
                    cx.notify();
                });
            });
            self.file_reads.insert(sha, task);
        }
        self.remeasure(&opened);
        cx.notify();
    }
    /// The commit's row changed height. The list keeps its place, which a reset would lose.
    fn remeasure(&self, sha: &str) {
        if let Some(at) = self
            .visible
            .iter()
            .position(|&index| self.history.commits[index].sha == sha)
        {
            self.list.splice(at + 1..at + 2, 1);
        }
    }
    fn sync_rows(&mut self) {
        let next = self.filter.visible(&self.history.commits);
        let appended = next.starts_with(&self.visible);
        // The picture is drawn for what's on screen: filtered-out commits would leave its lines going nowhere.
        self.graph = lanes(next.iter().map(|&index| &self.history.commits[index]));
        if appended {
            let at = self.visible.len() + 1;
            self.list
                .splice(at..at + 1, next.len() - self.visible.len() + 1);
        } else {
            self.list.reset(next.len() + ITEMS_AROUND);
        }
        self.visible = next;
    }
}
impl Render for CommitTimeline {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if self.inline {
            self.render_inline(cx)
        } else {
            self.render_table(cx)
        }
    }
}
#[path = "commit_timeline_inline.rs"]
mod inline;
#[path = "commit_table.rs"]
mod table;
#[cfg(test)]
#[path = "commit_timeline_tests.rs"]
mod tests;
