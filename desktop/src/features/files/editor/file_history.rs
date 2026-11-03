//! Selected-file history uses step 9's exact query/model/diff intent and owns only its visible request.
use crate::core::intents::open_commit_diff::OpenCommitDiff;
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::features::source_control::git_history::GitHistory;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

#[cfg(test)]
#[path = "file_history_tests.rs"]
mod tests;

#[derive(Clone)]
struct HistoryRequest {
    session: String,
    path: String,
    cursor: Option<String>,
    generation: u64,
}

struct FileHistoryState {
    session: String,
    path: String,
    shown: bool,
    generation: u64,
    loading: bool,
    loaded: bool,
    page: GitHistory,
    error: Option<String>,
}

impl FileHistoryState {
    fn new(session: String, path: String) -> Self {
        Self {
            session,
            path,
            shown: false,
            generation: 0,
            loading: false,
            loaded: false,
            page: GitHistory::default(),
            error: None,
        }
    }
    fn select(&mut self, session: String, path: String) {
        if self.session == session && self.path == path {
            return;
        }
        self.session = session;
        self.path = path;
        self.refresh();
    }
    fn refresh(&mut self) {
        self.generation = self.generation.wrapping_add(1);
        self.loading = false;
        self.loaded = false;
        self.page = GitHistory::default();
        self.error = None;
    }
    fn hide(&mut self) {
        self.shown = false;
        self.generation = self.generation.wrapping_add(1);
        self.loading = false;
    }
    fn request(&mut self, more: bool) -> Option<HistoryRequest> {
        if !self.shown
            || self.loading
            || (!more && self.loaded)
            || (more && self.page.next_cursor.is_none())
        {
            return None;
        }
        self.loading = true;
        Some(HistoryRequest {
            session: self.session.clone(),
            path: self.path.clone(),
            cursor: if more {
                self.page.next_cursor.clone()
            } else {
                None
            },
            generation: self.generation,
        })
    }
    fn loaded(&mut self, request: &HistoryRequest, result: Result<GitHistory, String>) {
        if !self.shown
            || request.generation != self.generation
            || request.session != self.session
            || request.path != self.path
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
            Err(error) => {
                self.error = Some(error);
            }
        }
    }
}

pub struct FileHistory {
    source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    state: FileHistoryState,
    loading: Option<Task<()>>,
}

impl FileHistory {
    pub fn new(session: String, path: String, source: Option<crate::core::socket::row_source::ExpectedRowSource>) -> Self {
        Self {
            source,
            state: FileHistoryState::new(session, path),
            loading: None,
        }
    }
    pub fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        if active {
            self.state.shown = true;
            self.load(false, cx);
        } else {
            self.loading = None;
            self.state.hide();
        }
        cx.notify();
    }
    pub fn select(&mut self, session: String, path: String, cx: &mut Context<Self>) {
        self.loading = None;
        self.state.select(session, path);
        self.load(false, cx);
        cx.notify();
    }
    fn load(&mut self, more: bool, cx: &mut Context<Self>) {
        let Some(request) = self.state.request(more) else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            self.state
                .loaded(&request, Err("The backend isn't running.".into()));
            return;
        };
        let call = socket.read(cx).call::<GitHistory>(
            "git",
            "history",
            json!({ "session": request.session, "cursor": request.cursor, "path": request.path, "expectedSource": self.source }),
            None,
        );
        self.loading = Some(cx.spawn(async move |view, cx| {
            let answer = match call.await {
                Outcome::Ok(page) => Ok(page),
                Outcome::Failed(error) => Err(error.message),
                Outcome::Aborted => Err("History disconnected; refresh to retry.".into()),
            };
            let _ = view.update(cx, |view, cx| {
                view.loading = None;
                view.state.loaded(&request, answer);
                cx.notify();
            });
        }));
    }
}

impl Render for FileHistory {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let muted = cx.theme().muted_foreground;
        v_flex()
            .id("file-history-timeline")
            .max_h(px(240.))
            .overflow_y_scroll()
            .px_2()
            .py_1()
            .child(
                h_flex().gap_2().child("File history").child(
                    Button::new("file-history-refresh")
                        .ghost()
                        .label("Refresh")
                        .on_click(cx.listener(|view, _, _, cx| {
                            view.loading = None;
                            view.state.refresh();
                            view.load(false, cx);
                        })),
                ),
            )
            .children(
                self.state
                    .error
                    .clone()
                    .map(|error| div().text_sm().text_color(muted).child(error)),
            )
            .when(
                self.state.loaded && self.state.page.commits.is_empty(),
                |view| view.child("No commits for this file."),
            )
            .when(self.state.loading, |view| view.child("Loading history…"))
            .children(
                self.state
                    .page
                    .commits
                    .iter()
                    .enumerate()
                    .map(|(ix, commit)| {
                        let intent = OpenCommitDiff {
                            session_id: self.state.session.clone(),
                            sha: commit.sha.clone(),
                            path: self.state.path.clone(),
                            expected_source: self.source.clone(),
                            title: format!("{} · {}", commit.short_sha, self.state.path),
                        };
                        v_flex()
                            .id(("file-history", ix))
                            .py_1()
                            .cursor_pointer()
                            .child(commit.subject.clone())
                            .child(div().text_sm().text_color(muted).child(format!(
                                "{} · {} · {}",
                                commit.short_sha, commit.author, commit.committed_at
                            )))
                            .on_click(move |_, window, cx| {
                                window.dispatch_action(Box::new(intent.clone()), cx)
                            })
                    }),
            )
            .when(self.state.page.next_cursor.is_some(), |view| {
                view.child(
                    Button::new("file-history-more")
                        .ghost()
                        .label("Load more")
                        .disabled(self.state.loading)
                        .on_click(cx.listener(|view, _, _, cx| view.load(true, cx))),
                )
            })
    }
}
