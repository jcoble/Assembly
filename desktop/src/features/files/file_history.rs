//! The history card's body: one file's commits, newest first, read a page at a time with `git history` while the card
//! is open and the tab shown. Another file, collapsing or hiding drops the read, and a late page for an earlier file is
//! refused, so the card never shows another file's commits. Replaces the old app's FileHistoryPane.svelte list.
use crate::core::intents::open_commit_diff::OpenCommitDiff;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::spinner::Spinner;
use crate::features::source_control::git_history::GitHistory;
use crate::features::source_control::git_labels::commit_age;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

#[cfg(test)]
#[path = "file_history_tests.rs"]
mod tests;

/// The body's height, as the old card's.
const BODY: Pixels = px(220.);
const ROW: Pixels = px(28.);

#[derive(Clone, Debug, PartialEq)]
pub struct HistoryFile {
    pub session: String,
    pub path: String,
    pub source: Option<ExpectedRowSource>,
}

#[derive(Clone)]
struct HistoryRequest {
    file: HistoryFile,
    cursor: Option<String>,
    generation: u64,
}

/// What the body shows: a line in place of the commits, or the commits.
#[derive(Debug, PartialEq)]
pub enum Body {
    Pick,
    Reading,
    Unavailable,
    Failed(String),
    Empty,
    Commits,
}

#[derive(Default)]
struct FileHistoryState {
    file: Option<HistoryFile>,
    shown: bool,
    generation: u64,
    loading: bool,
    loaded: bool,
    page: GitHistory,
    error: Option<String>,
}

impl FileHistoryState {
    /// Shows `file`; another file drops what was read. Whether it changed.
    fn select(&mut self, file: Option<HistoryFile>) -> bool {
        if self.file == file {
            return false;
        }
        self.file = file;
        self.refresh();
        true
    }

    fn refresh(&mut self) {
        self.generation = self.generation.wrapping_add(1);
        self.loading = false;
        self.loaded = false;
        self.page = GitHistory::default();
        self.error = None;
    }

    /// Hidden, it forgets what it read, so showing it again reads the file's history as it is then.
    fn show(&mut self, shown: bool) {
        self.shown = shown;
        if !shown {
            self.refresh();
        }
    }

    fn request(&mut self, more: bool) -> Option<HistoryRequest> {
        let file = self.file.clone()?;
        let wanted = if more {
            self.loaded && self.page.next_cursor.is_some()
        } else {
            !self.loaded
        };
        if !self.shown || self.loading || !wanted {
            return None;
        }
        self.loading = true;
        Some(HistoryRequest {
            file,
            cursor: if more {
                self.page.next_cursor.clone()
            } else {
                None
            },
            generation: self.generation,
        })
    }

    /// Older commits, once the last row drawn is within two of the end of those read.
    fn near_end(&mut self, last_visible: usize) -> Option<HistoryRequest> {
        if last_visible + 2 < self.page.commits.len() {
            return None;
        }
        self.request(true)
    }

    fn loaded(&mut self, request: &HistoryRequest, result: Result<GitHistory, String>) {
        if !self.shown
            || request.generation != self.generation
            || self.file.as_ref() != Some(&request.file)
        {
            return;
        }
        self.loading = false;
        match result {
            Ok(page) => {
                self.page.append(page);
                self.loaded = true;
                self.error = None;
            }
            Err(error) => self.error = Some(error),
        }
    }

    fn body(&self) -> Body {
        if self.file.is_none() {
            return Body::Pick;
        }
        if let Some(error) = &self.error {
            // Git's own wording only, as the History tab reads it: matching more would hide a real failure.
            let lower = error.to_lowercase();
            return if lower.contains("not a git repository")
                || lower.contains("must be run in a work tree")
            {
                Body::Unavailable
            } else {
                Body::Failed(error.clone())
            };
        }
        match (self.loaded, self.page.commits.is_empty()) {
            (false, _) => Body::Reading,
            (true, true) => Body::Empty,
            (true, false) => Body::Commits,
        }
    }
}

pub struct FileHistoryList {
    focus: FocusHandle,
    state: FileHistoryState,
    /// The page being read; dropping it cancels the call.
    reading: Option<Task<()>>,
    scroll: UniformListScrollHandle,
    /// The commit last opened, or moved to with the arrow keys.
    picked: Option<usize>,
}

impl FileHistoryList {
    pub fn new(cx: &mut Context<Self>) -> Self {
        Self {
            focus: cx.focus_handle(),
            state: FileHistoryState::default(),
            reading: None,
            scroll: UniformListScrollHandle::new(),
            picked: None,
        }
    }

    #[cfg(test)]
    pub fn file(&self) -> Option<&HistoryFile> {
        self.state.file.as_ref()
    }

    /// Shows `file`, reading it if the card is open and shown.
    pub fn show(&mut self, file: Option<HistoryFile>, cx: &mut Context<Self>) {
        if self.state.select(file) {
            self.reading = None;
            self.picked = None;
            self.scroll.scroll_to_item(0, ScrollStrategy::Top);
            self.read(false, cx);
            cx.notify();
        }
    }

    /// Open and shown, it reads; otherwise it reads nothing and drops any read running.
    pub fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        if active == self.state.shown {
            return;
        }
        self.state.show(active);
        self.reading = None;
        self.picked = None;
        self.read(false, cx);
        cx.notify();
    }

    pub fn refresh(&mut self, cx: &mut Context<Self>) {
        self.reading = None;
        self.picked = None;
        self.state.refresh();
        self.read(false, cx);
        cx.notify();
    }

    #[cfg(test)]
    pub fn body(&self) -> Body {
        self.state.body()
    }

    #[cfg(test)]
    pub fn commits(&self) -> &[crate::features::source_control::git_history::GitCommit] {
        &self.state.page.commits
    }

    #[cfg(test)]
    pub fn is_reading(&self) -> bool {
        self.reading.is_some()
    }

    /// The list drew down to `last_visible`: near the end, it reads older commits.
    pub(super) fn reached(&mut self, last_visible: usize, cx: &mut Context<Self>) {
        if let Some(request) = self.state.near_end(last_visible) {
            self.send(request, cx);
        }
    }

    fn read(&mut self, more: bool, cx: &mut Context<Self>) {
        if let Some(request) = self.state.request(more) {
            self.send(request, cx);
        }
    }

    fn send(&mut self, request: HistoryRequest, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            self.state
                .loaded(&request, Err("The backend isn't running.".into()));
            return;
        };
        let call = socket.read(cx).call::<GitHistory>(
            "git",
            "history",
            json!({ "session": request.file.session, "cursor": request.cursor, "path": request.file.path, "expectedSource": request.file.source }),
            None,
        );
        self.reading = Some(cx.spawn(async move |list, cx| {
            let answer = match call.await {
                Outcome::Ok(page) => Ok(page),
                Outcome::Failed(error) => Err(error.message),
                Outcome::Aborted => Err("History disconnected; refresh to retry.".into()),
            };
            let _ = list.update(cx, |list, cx| {
                list.reading = None;
                list.state.loaded(&request, answer);
                cx.notify();
            });
        }));
    }

    /// Opens the commit's diff of this file, as the old card did.
    pub(super) fn open(&mut self, ix: usize, window: &mut Window, cx: &mut Context<Self>) {
        let (Some(file), Some(commit)) = (&self.state.file, self.state.page.commits.get(ix)) else {
            return;
        };
        let intent = OpenCommitDiff {
            session_id: file.session.clone(),
            sha: commit.sha.clone(),
            path: file.path.clone(),
            expected_source: file.source.clone(),
            title: format!("{} · {}", commit.short_sha, file.path),
            checkout: None,
        };
        self.picked = Some(ix);
        window.dispatch_action(Box::new(intent), cx);
        cx.notify();
    }

    fn key_down(&mut self, event: &KeyDownEvent, window: &mut Window, cx: &mut Context<Self>) {
        let count = self.state.page.commits.len();
        match event.keystroke.key.as_str() {
            "up" | "down" if count > 0 => {
                let ix = match (self.picked, event.keystroke.key.as_str()) {
                    (None, _) => 0,
                    (Some(ix), "up") => ix.saturating_sub(1),
                    (Some(ix), _) => (ix + 1).min(count - 1),
                };
                self.picked = Some(ix);
                self.scroll.scroll_to_item(ix, ScrollStrategy::Center);
                cx.notify();
            }
            "enter" | "space" => {
                if let Some(ix) = self.picked {
                    self.open(ix, window, cx);
                }
            }
            _ => return,
        }
        cx.stop_propagation();
    }

    fn row(&self, ix: usize, focused: bool, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme();
        let muted = theme.muted_foreground;
        let Some(commit) = self.state.page.commits.get(ix) else {
            return h_flex()
                .h(ROW)
                .pl(px(25.))
                .gap_2()
                .text_role(TextRole::Body, cx)
                .text_color(muted)
                .child(Spinner::new().xsmall())
                .child("Reading older commits…")
                .into_any_element();
        };
        let picked = self.picked == Some(ix);
        let (line, accent, secondary, ring) =
            (theme.border, theme.accent, theme.secondary, theme.ring);
        let tip = format!(
            "{}\n{} · {}",
            commit.subject, commit.author, commit.short_sha
        );
        let mark = v_flex()
            .w(px(14.))
            .h_full()
            .flex_none()
            .items_center()
            .child(
                div()
                    .w(px(2.))
                    .flex_1()
                    .when(ix > 0, |above| above.bg(line)),
            )
            .child(
                div()
                    .size(px(8.))
                    .flex_none()
                    .rounded_full()
                    .border_2()
                    .border_color(muted),
            )
            .child(div().w(px(2.)).flex_1().bg(line));
        h_flex()
            .id(("files-history-commit", ix))
            .h(ROW)
            .px(px(6.))
            .gap(px(5.))
            .rounded(theme.radius)
            .cursor_pointer()
            .text_role(TextRole::Body, cx)
            .when(picked, |row| row.bg(secondary))
            .when(!picked, |row| row.hover(|row| row.bg(accent)))
            .when(picked && focused, |row| row.border_1().border_color(ring))
            .child(mark)
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .overflow_hidden()
                    .text_ellipsis()
                    .whitespace_nowrap()
                    .child(commit.subject.clone()),
            )
            .child(
                div()
                    .flex_none()
                    .text_color(muted)
                    .child(commit_age(&commit.committed_at)),
            )
            .tooltip(move |window, cx| Tooltip::new(tip.clone()).build(window, cx))
            .on_click(cx.listener(move |list, _, window, cx| {
                window.focus(&list.focus, cx);
                list.open(ix, window, cx)
            }))
            .into_any_element()
    }
}

impl Render for FileHistoryList {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, danger, border) = (theme.muted_foreground, theme.danger, theme.border);
        let body = v_flex()
            .id("files-history-body")
            .track_focus(&self.focus)
            .on_key_down(cx.listener(Self::key_down))
            .h(BODY)
            .w_full()
            .border_t_1()
            .border_color(border);
        let line = |text: SharedString, color: Hsla, spinning: bool, cx: &App| {
            h_flex()
                .gap_2()
                .px_3()
                .py(px(10.))
                .text_role(TextRole::Body, cx)
                .text_color(color)
                .when(spinning, |line| line.child(Spinner::new().xsmall()))
                .child(text)
        };
        match self.state.body() {
            Body::Pick => body.child(line(
                "Select a file to see its history.".into(),
                muted,
                false,
                cx,
            )),
            Body::Reading => body.child(line("Reading the file history…".into(), muted, true, cx)),
            Body::Unavailable => body.child(line(
                "This folder’s Git history is unavailable.".into(),
                muted,
                false,
                cx,
            )),
            Body::Failed(error) => body.child(line(error.into(), danger, false, cx)),
            Body::Empty => body.child(line(
                "No commits have touched this file.".into(),
                muted,
                false,
                cx,
            )),
            Body::Commits => {
                let count = self.state.page.commits.len() + usize::from(self.state.loading);
                body.child(
                    uniform_list(
                        "files-history-commits",
                        count,
                        cx.processor(|list, range: std::ops::Range<usize>, window, cx| {
                            list.reached(range.end.saturating_sub(1), cx);
                            let focused = list.focus.is_focused(window);
                            range.map(|ix| list.row(ix, focused, cx)).collect()
                        }),
                    )
                    .track_scroll(&self.scroll)
                    .flex_1()
                    .p_1(),
                )
            }
        }
    }
}
