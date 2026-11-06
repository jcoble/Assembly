//! The Tasks tab's way into a task: opening its page, reading all of it, and following the links on it.

use gpui_kit::component::WindowExt as _;
use gpui_kit::component::notification::Notification;
use gpui_kit::*;

use crate::core::intents::open_file::OpenFile;
use crate::core::intents::open_pull_requests::OpenPullRequests;
use crate::core::intents::open_url::OpenUrl;
use crate::core::state::selected_session::SelectedSession;
use crate::features::tasks::notion_client::NotionClient;
use crate::features::tasks::task_detail::{TaskDetailEvent, TaskDetailView, TaskSeed};
use crate::features::tasks::task_export::*;
use crate::features::tasks::task_navigation::{RouteContext, RouteRefused, TaskRoute, route_url};
use crate::features::tasks::tasks_state::*;

use super::{NO_SESSION, NOT_CONNECTED, NOT_OPENABLE, TasksPane};

impl TasksPane {
    pub(super) fn open_row(&mut self, index: usize, window: &mut Window, cx: &mut Context<Self>) {
        let Some(row) = self.state.rows.get(index) else {
            return;
        };
        let source = NotionTaskRef {
            data_source_id: row.source_id.clone(),
            page_id: row.page_id.clone(),
        };
        self.task_session = None;
        self.open_task(source, None, window, cx);
    }

    /// Opens a task's page, from the list if it's known there. `fallback` is where to go if it isn't a task after all.
    pub(super) fn open_task(
        &mut self,
        source: NotionTaskRef,
        fallback: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let seed = self.seed(&source);
        self.set_aside(window, cx);
        let token = self.state.select(source.clone());
        self.page_fallback = fallback;
        let detail = cx.new(|cx| TaskDetailView::new(source.clone(), seed, window, cx));
        self._detail_heard =
            Some(
                cx.subscribe_in(&detail, window, |pane, _, event, window, cx| match event {
                    TaskDetailEvent::Back => pane.go_back(window, cx),
                    TaskDetailEvent::ListStale => pane.refresh(cx),
                    TaskDetailEvent::Reread => {
                        if let Some((token, source)) = pane.state.reread() {
                            pane.read_detail(token, source, window, cx);
                        }
                    }
                    TaskDetailEvent::PicturesSettled => {}
                }),
            );
        detail.read(cx).focus_handle(cx).focus(window, cx);
        self.detail = Some(detail);
        self.read_detail(token, source, window, cx);
        cx.notify();
    }

    pub(super) fn seed(&self, source: &NotionTaskRef) -> TaskSeed {
        let mut seed = TaskSeed {
            statuses: self.state.statuses.clone(),
            priorities: self.state.priorities.clone(),
            ..TaskSeed::default()
        };
        if let Some(row) = self.state.row(&source.page_id) {
            seed.title = row.title.clone();
            seed.key = row.task_key.clone();
            seed.project = row.project_title.clone();
            seed.status = row.status.clone();
            seed.priority = row.priority.clone();
            seed.url = row.url.clone();
        }
        seed
    }

    pub(super) fn go_back(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.state.back();
        self.set_aside(window, cx);
        self.reading_detail = None;
        self.page_fallback = None;
        self.focus.focus(window, cx);
        cx.notify();
    }

    /// Closes the open page. One still sending pictures is kept until they're on, then says if any didn't go.
    pub(super) fn set_aside(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self._detail_heard = None;
        let Some(view) = self.detail.take() else {
            return;
        };
        if !view.read(cx).pictures_pending() {
            return;
        }
        let title = self
            .state
            .row(&view.read(cx).source.page_id)
            .map(|row| row.title.clone())
            .filter(|title| !title.is_empty());
        let heard = cx.subscribe_in(&view, window, move |pane, view, event, window, cx| {
            if matches!(event, TaskDetailEvent::Reread) {
                // The same page opened again shows the picture that just went on.
                let page = &view.read(cx).source.page_id;
                let open = pane
                    .detail
                    .as_ref()
                    .map(|open| &open.read(cx).source.page_id);
                if open == Some(page)
                    && let Some((token, source)) = pane.state.reread()
                {
                    pane.read_detail(token, source, window, cx);
                }
            }
            if !matches!(event, TaskDetailEvent::PicturesSettled) {
                return;
            }
            pane.finishing.retain(|(kept, _)| kept != view);
            if let Some(failure) = view.read(cx).pictures_failure() {
                let note = match &title {
                    Some(title) => format!("{title}: {failure}"),
                    None => failure,
                };
                window.push_notification(Notification::warning(note), cx);
            }
        });
        self.finishing.push((view, heard));
    }

    pub(super) fn read_detail(
        &mut self,
        token: DetailToken,
        source: NotionTaskRef,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let client = self.client.clone();
        self.reading_detail = Some(cx.spawn_in(window, async move |pane, cx| {
            let result = read_whole(&client, &source, cx).await;
            let _ = pane.update_in(cx, |pane, window, cx| {
                pane.detail_read(token, result, window, cx)
            });
        }));
    }

    pub(super) fn detail_read(
        &mut self,
        token: DetailToken,
        result: Result<TaskDetail, NotionError>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        match result {
            Ok(detail) => {
                if !self.state.detail_result(token, detail.clone()) {
                    return;
                }
                self.page_fallback = None;
                if let Some(view) = &self.detail {
                    view.update(cx, |view, cx| view.detail_arrived(&detail, window, cx));
                }
            }
            Err(error) => {
                if !self.state.detail_failed(token, &error) {
                    return;
                }
                let elsewhere = matches!(
                    error.code.as_str(),
                    "notion_source_mismatch" | "notion_http_403" | "notion_http_404"
                );
                if let Some(url) = self.page_fallback.take().filter(|_| elsewhere) {
                    window.dispatch_action(Box::new(OpenUrl { url }), cx);
                    self.go_back(window, cx);
                    return;
                }
                if let Some(view) = &self.detail {
                    view.update(cx, |view, cx| {
                        view.detail_failed(error_text(&error.code), cx)
                    });
                }
                self.setup_follows(cx);
            }
        }
        cx.notify();
    }

    /// A link on a task page, or from outside.
    pub(super) fn open_link(&mut self, url: &str, window: &mut Window, cx: &mut Context<Self>) {
        let context = RouteContext {
            source: self.state.source(),
        };
        match route_url(url, &context) {
            Ok(TaskRoute::Task(source)) => self.open_task(source, None, window, cx),
            Ok(TaskRoute::NotionPage { source, url }) => {
                self.open_task(source, Some(url), window, cx)
            }
            Ok(TaskRoute::PullRequest { repo, number }) => window.dispatch_action(
                Box::new(OpenPullRequests {
                    session_id: None,
                    repo: Some(repo),
                    number: Some(number),
                }),
                cx,
            ),
            Ok(TaskRoute::File { path }) => self.open_file(path, window, cx),
            Ok(TaskRoute::Web(url)) => window.dispatch_action(Box::new(OpenUrl { url }), cx),
            Err(RouteRefused::NotConnected) => {
                window.push_notification(Notification::warning(NOT_CONNECTED), cx)
            }
            Err(RouteRefused::Unsupported) => {
                window.push_notification(Notification::warning(NOT_OPENABLE), cx)
            }
        }
    }

    /// Against the session the task was opened from, or the selected one. No session, no guess.
    pub(super) fn open_file(&mut self, path: String, window: &mut Window, cx: &mut Context<Self>) {
        let selected = SelectedSession::try_global(cx);
        let selected = selected.as_ref().map(|selected| selected.read(cx));
        let session = self
            .task_session
            .as_ref()
            .map(|key| key.id.clone())
            .or_else(|| {
                selected
                    .as_ref()
                    .and_then(|selected| selected.session_id().map(String::from))
            });
        let Some(session_id) = session else {
            window.push_notification(Notification::warning(NO_SESSION), cx);
            return;
        };
        let expected_source = selected
            .filter(|selected| selected.session_id() == Some(session_id.as_str()))
            .and_then(|selected| selected.source().cloned());
        window.dispatch_action(
            Box::new(OpenFile {
                session_id,
                path,
                line: None,
                column: None,
                pinned: true,
                expected_source,
            }),
            cx,
        );
    }
}

async fn read_whole(
    client: &NotionClient,
    source: &NotionTaskRef,
    cx: &mut AsyncWindowContext,
) -> Result<TaskDetail, NotionError> {
    let mut after = None;
    let mut detail: Option<TaskDetail> = None;
    loop {
        let call = cx
            .update(|_, cx| client.detail(source.clone(), after.clone(), cx))
            .map_err(|_| NotionError {
                code: "cancelled".into(),
                message: String::new(),
            })?;
        let page = call.await?;
        let whole = detail.get_or_insert_with(|| TaskDetail {
            source: source.clone(),
            revision: page.revision.clone(),
            properties: page.properties.clone().unwrap_or_default(),
            blocks: Vec::new(),
            comments: Vec::new(),
            comments_unavailable: page.comments_unavailable,
        });
        whole.blocks.extend(page.blocks);
        whole.comments.extend(page.comments);
        match page.next {
            Some(next) => after = Some(next),
            None => return Ok(detail.expect("filled above")),
        }
    }
}
