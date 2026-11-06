//! The Tasks tab's way into a task: opening its page, reading all of it, and following the links on it.

use gpui_kit::component::WindowExt as _;
use gpui_kit::component::notification::Notification;
use gpui_kit::*;

use crate::core::intents::open_pull_requests::OpenPullRequests;
use crate::core::intents::open_url::OpenInBrowser;
use crate::core::navigation::deep_links::handle_route;
use crate::core::socket::client::SocketClient;
use crate::features::tasks::notion_client::NotionClient;
use crate::features::tasks::task_detail::{TaskDetailEvent, TaskDetailView, TaskSeed};
use crate::features::tasks::task_export::*;
use crate::features::tasks::task_navigation::{RouteContext, TaskRoute, passes_on, route_url};
use crate::features::tasks::tasks_state::*;

use super::{NOT_OPENABLE, TasksPane};

impl TasksPane {
    pub(super) fn open_row(&mut self, index: usize, window: &mut Window, cx: &mut Context<Self>) {
        let Some(row) = self.state.rows.get(index) else {
            return;
        };
        let source = NotionTaskRef {
            data_source_id: row.source_id.clone(),
            page_id: row.page_id.clone(),
        };
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
        // A page that may not be a task comes forward only once its read shows it is one.
        if fallback.is_none() {
            Self::show(window, cx);
        }
        let seed = self.seed(&source);
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
        self.detail = None;
        self._detail_heard = None;
        self.reading_detail = None;
        self.page_fallback = None;
        self.focus.focus(window, cx);
        cx.notify();
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

    /// Brings forward a tab that has just read, or failed to read, a page it held back. It starts first, so coming
    /// forward doesn't read the page again.
    fn come_forward(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.start(cx);
        Self::show(window, cx);
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
                if self.page_fallback.take().is_some() {
                    self.come_forward(window, cx);
                }
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
                match self.page_fallback.take() {
                    Some(url) if elsewhere => {
                        window.dispatch_action(Box::new(OpenInBrowser { url }), cx);
                        self.go_back(window, cx);
                        return;
                    }
                    // It may be a task Notion couldn't read just now: the tab comes forward to say so.
                    Some(_) => self.come_forward(window, cx),
                    None => {}
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

    /// A link on a task page, or from outside. Where a Notion link goes depends on whether Notion is connected, which
    /// the tab knows once it has the settings; before that it asks the backend, and the link waits for the answer. The
    /// question always ends: a backend that isn't connected fails it at once, and a dropped connection ends it. With no
    /// answer the link goes where it goes with Notion not connected.
    pub(super) fn open_link(&mut self, url: &str, window: &mut Window, cx: &mut Context<Self>) {
        if self.state.settings.is_none() && SocketClient::try_global(cx).is_some() {
            let asking = self.client.settings(cx);
            let url = url.to_string();
            self.waiting_link = Some(cx.spawn_in(window, async move |pane, cx| {
                let answer = asking.await;
                _ = pane.update_in(cx, |pane, window, cx| {
                    pane.waiting_link = None;
                    if let Ok(settings) = answer
                        && pane.state.settings.is_none()
                    {
                        pane.settings_pushed(settings, cx);
                    }
                    pane.route_link(&url, window, cx);
                });
            }));
            return;
        }
        self.route_link(url, window, cx);
    }

    fn route_link(&mut self, url: &str, window: &mut Window, cx: &mut Context<Self>) {
        let context = RouteContext {
            source: self.state.source(),
        };
        match route_url(url, &context) {
            Some(TaskRoute::Task(source)) => self.open_task(source, None, window, cx),
            Some(TaskRoute::NotionPage { source, url }) => {
                self.open_task(source, Some(url), window, cx)
            }
            Some(TaskRoute::PullRequest { repo, number }) => window.dispatch_action(
                Box::new(OpenPullRequests {
                    session_id: None,
                    repo: Some(repo),
                    number: Some(number),
                }),
                cx,
            ),
            // Neither a task nor a pull request, so only the browser is left, and it mustn't come back here.
            Some(TaskRoute::Web(url)) => {
                window.dispatch_action(Box::new(OpenInBrowser { url }), cx)
            }
            None => {
                if let Some(route) = passes_on(url) {
                    // This runs while the window is being updated, and a route updates the window itself.
                    cx.defer(move |cx| handle_route(route, cx));
                    return;
                }
                window.push_notification(Notification::warning(NOT_OPENABLE), cx);
            }
        }
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
