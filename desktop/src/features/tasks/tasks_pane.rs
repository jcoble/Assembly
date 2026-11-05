//! `TasksPane` is the drawer's Tasks tab: the Notion tasks saved on this Mac, a task's page, and the connection card when
//! it's needed. It shows the cached list at once, reads Notion only on Refresh or first connect, and holds its pushed
//! topics only while it's shown. Replaces TasksPanel.svelte.

use gpui_kit::component::WindowExt as _;
use gpui_kit::component::dock::{
    BasePanel, DockPlacement, PaneRef, Panel, PanelEvent, PanelHandle, PanelId, panel_handle,
    register_panel,
};
use gpui_kit::component::input::{InputEvent, InputState};
use gpui_kit::component::notification::Notification;
use gpui_kit::*;

use crate::core::engine::SessionKey;
use crate::core::intents::open_task::{OpenTask, OpenTaskLink};
use crate::core::socket::client::SocketClient;
use crate::core::socket::update::Update;
use crate::core::state::selected_session::SelectedSession;
use crate::core::ui::center_panes::CenterPanes;
use crate::core::ui::filter_pills::{FilterPills, FilterPillsChanged};
use crate::features::shell::default_layout::TASKS;

use super::notion_client::NotionClient;
use super::notion_setup::NotionSetup;
use super::task_detail::TaskDetailView;
use super::task_export::*;
use super::task_navigation::notion_web_page;
use super::tasks_state::*;

#[cfg(test)]
#[path = "tasks_pane_links_tests.rs"]
mod links_tests;
#[path = "tasks_pane_list.rs"]
mod list;
#[path = "tasks_pane_open.rs"]
mod open;
#[cfg(test)]
#[path = "tasks_pane_tests.rs"]
mod tests;

/// The open pane, so a link from anywhere reaches it.
struct OpenPane(WeakEntity<TasksPane>);
impl Global for OpenPane {}

pub struct TasksPane {
    focus: FocusHandle,
    client: NotionClient,
    state: TasksState,
    list: ListState,
    search: Entity<InputState>,
    pills: Entity<FilterPills>,
    setup: Entity<NotionSetup>,
    detail: Option<Entity<TaskDetailView>>,
    /// A Notion page that may not be a task in this source; opened in the browser if it isn't.
    page_fallback: Option<String>,
    /// A link that came before the tab knew whether Notion is connected; followed once it does.
    waiting_link: Option<String>,
    /// The session a task was opened from.
    task_session: Option<SessionKey>,
    refreshing: bool,
    /// Asked Notion once for a brand new connection's list.
    first_refresh_done: bool,
    /// The saved view is in and the topics are held.
    started: bool,
    /// The saved search, for the box to show on the next draw.
    saved_search: Option<String>,
    watching: Vec<Task<()>>,
    reading: Option<Task<()>>,
    reading_detail: Option<Task<()>>,
    refresh_call: Option<Task<()>>,
    saving_view: Option<Task<()>>,
    _heard: Vec<Subscription>,
    _detail_heard: Option<Subscription>,
}

impl TasksPane {
    /// Registers the pane under the drawer's Tasks name, after the placeholders, since the last registration wins.
    pub fn register(cx: &mut App) {
        register_panel(cx, TASKS, |_, window, cx| {
            let pane = cx.new(|cx| TasksPane::new(window, cx));
            cx.set_global(OpenPane(pane.downgrade()));
            panel_handle(pane)
        });
        cx.on_action(|intent: &OpenTask, cx| {
            let intent = intent.clone();
            Self::in_window(cx, move |pane, window, cx| {
                pane.task_session = intent.session.clone();
                pane.open_task(intent.source.clone(), None, window, cx);
            });
        });
        cx.on_action(|intent: &OpenTaskLink, cx| {
            let url = intent.url.clone();
            Self::in_window(cx, move |pane, window, cx| pane.open_link(&url, window, cx));
        });
    }

    /// Runs once the current update is over, on the window's pane, with the tab brought forward.
    fn in_window(
        cx: &mut App,
        act: impl FnOnce(&mut Self, &mut Window, &mut Context<Self>) + 'static,
    ) {
        let Some(window) = cx
            .active_window()
            .or_else(|| cx.windows().into_iter().next())
        else {
            return;
        };
        cx.defer(move |cx| {
            let _ = window.update(cx, |_, window, cx| {
                let Some(pane) = cx
                    .try_global::<OpenPane>()
                    .and_then(|open| open.0.upgrade())
                else {
                    window.push_notification(Notification::warning(NO_TAB), cx);
                    return;
                };
                Self::show(window, cx);
                pane.update(cx, |pane, cx| act(pane, window, cx));
            });
        });
    }

    fn located(cx: &App) -> Option<(PanelId, DockPlacement)> {
        let dock = CenterPanes::dock(cx)?;
        let dock = dock.read(cx);
        let mut found = None;
        for placement in [
            DockPlacement::Right,
            DockPlacement::Left,
            DockPlacement::Center,
            DockPlacement::Bottom,
        ] {
            if let Some(tree) = dock.layout(placement) {
                tree.root().walk(&mut |node| {
                    if let PaneRef::Tabs { panels, .. } = node.kind() {
                        for id in panels {
                            if let Some(panel) = dock.panel(*id)
                                && let Some(handle) = PanelHandle::of(panel)
                                && handle.view().downcast::<Self>().is_ok()
                            {
                                found = Some((*id, placement));
                            }
                        }
                    }
                });
            }
        }
        found
    }

    fn show(window: &mut Window, cx: &mut App) {
        let (Some(dock), Some((id, placement))) = (CenterPanes::dock(cx), Self::located(cx)) else {
            return;
        };
        dock.update(cx, |dock, cx| {
            dock.select_panel(id, window, cx);
            if placement != DockPlacement::Center && !dock.is_dock_open(placement) {
                dock.toggle_dock(placement, window, cx);
            }
        });
    }

    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let search = cx.new(|cx| InputState::new(window, cx).placeholder("Search tasks"));
        let pills = cx.new(|cx| FilterPills::new("tasks", Vec::new(), cx));
        let setup = cx.new(|cx| NotionSetup::new(window, cx));
        let mut heard = vec![
            cx.subscribe_in(
                &search,
                window,
                |pane: &mut Self, search, event: &InputEvent, _, cx| {
                    let text = search.read(cx).value().trim().to_string();
                    let submitted = matches!(event, InputEvent::PressEnter { .. });
                    if submitted || (text.is_empty() && pane.state.query.search.is_some()) {
                        pane.state.query.search = (!text.is_empty()).then_some(text);
                        pane.query_changed(cx);
                    }
                },
            ),
            cx.subscribe(
                &pills,
                |pane: &mut Self, pills, _: &FilterPillsChanged, cx| {
                    let groups = &pills.read(cx).state.groups;
                    pane.state.query.statuses = groups
                        .first()
                        .map(|group| group.picked.clone())
                        .unwrap_or_default();
                    pane.state.query.priorities = groups
                        .get(1)
                        .map(|group| group.picked.clone())
                        .unwrap_or_default();
                    pane.query_changed(cx);
                },
            ),
            cx.observe(&setup, |_, _, cx| cx.notify()),
        ];
        if let Some(selected) = SelectedSession::try_global(cx) {
            heard.push(cx.observe(&selected, |pane, _, cx| pane.follow_selected(cx)));
        }
        Self {
            focus: cx.focus_handle(),
            client: NotionClient,
            state: TasksState::default(),
            list: ListState::new(0, ListAlignment::Top, px(OVERSCAN as f32 * ROW_HEIGHT)),
            search,
            pills,
            setup,
            detail: None,
            page_fallback: None,
            waiting_link: None,
            task_session: None,
            refreshing: false,
            first_refresh_done: false,
            started: false,
            saved_search: None,
            watching: Vec::new(),
            reading: None,
            reading_detail: None,
            refresh_call: None,
            saving_view: None,
            _heard: heard,
            _detail_heard: None,
        }
    }

    /// Reads the saved view, then holds the topics, whose first state starts the list.
    fn start(&mut self, cx: &mut Context<Self>) {
        if self.started || SocketClient::try_global(cx).is_none() {
            return;
        }
        self.started = true;
        let scroll = cx.entity().downgrade();
        self.list.set_scroll_handler(move |event, _, cx| {
            let end = event.visible_range.end;
            if let Some(pane) = scroll.upgrade() {
                pane.update(cx, |pane, cx| pane.maybe_more(end, cx));
            }
        });
        let view = self.client.view(cx);
        self.reading = Some(cx.spawn(async move |pane, cx| {
            let saved = view.await;
            let _ = pane.update(cx, |pane, cx| {
                if let Ok(query) = saved {
                    pane.state.query = query;
                    pane.saved_search = pane.state.query.search.clone();
                }
                pane.watch(cx);
                cx.notify();
            });
        }));
    }

    fn watch(&mut self, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let mut settings = socket.read(cx).subscribe("notion-settings", None);
        let mut tasks = socket.read(cx).subscribe("notion-tasks", None);
        let on_settings = cx.spawn(async move |pane, cx| {
            while let Ok(update) = settings.updates.recv().await {
                let item = match update {
                    Update::State(items) => items.into_iter().next().map(|(_, item)| item),
                    Update::Change { item, .. } => Some(item),
                };
                let view =
                    item.and_then(|item| serde_json::from_value::<NotionSettingsView>(item).ok());
                let Some(view) = view else {
                    continue;
                };
                if pane
                    .update(cx, |pane, cx| pane.settings_pushed(view, cx))
                    .is_err()
                {
                    return;
                }
            }
        });
        let on_tasks = cx.spawn(async move |pane, cx| {
            // A first state means a return to the tab or a reconnect, when anything may have changed meanwhile.
            while tasks.updates.recv().await.is_ok() {
                if pane.update(cx, |pane, cx| pane.tasks_pushed(cx)).is_err() {
                    return;
                }
            }
        });
        self.watching = vec![on_settings, on_tasks];
    }

    fn stop(&mut self) {
        self.started = false;
        self.watching.clear();
        self.reading = None;
        self.reading_detail = None;
    }
}

/// A sign-in callback, once the window is up.
pub fn receive_sign_in(state: String, claim_code: String, cx: &mut App) {
    if let Some(pane) = cx
        .try_global::<OpenPane>()
        .and_then(|open| open.0.upgrade())
    {
        let setup = pane.read(cx).setup.clone();
        setup.update(cx, |setup, cx| setup.receive(state, claim_code, cx));
    }
}

/// A task link, once the window is up. With no Tasks tab to open it in, its Notion page opens in the browser.
pub fn open_task_link(page_id: String, cx: &mut App) {
    let page = notion_web_page(&page_id);
    open_here_or_in_browser(format!("assembly://task/{page_id}"), page, cx);
}

/// A Notion page link from anywhere: its task here when Notion is connected, else the page in the browser.
pub fn open_notion_page(url: String, cx: &mut App) {
    open_here_or_in_browser(url.clone(), url, cx);
}

fn open_here_or_in_browser(link: String, page: String, cx: &mut App) {
    if (cx.try_global::<OpenPane>())
        .and_then(|open| open.0.upgrade())
        .is_none()
    {
        return cx.open_url(&page);
    }
    TasksPane::in_window(cx, move |pane, window, cx| {
        pane.open_link(&link, window, cx)
    });
}

/// About a row's height, for what to draw ahead and how many rows fill the tab.
const ROW_HEIGHT: f32 = 64.;
const NO_TAB: &str = "The Tasks tab isn't open in this window.";
pub(crate) const NOT_OPENABLE: &str = "Assembly can't open that link.";
const NO_SESSION: &str = "Select a session to open this file.";

impl EventEmitter<PanelEvent> for TasksPane {}

impl Focusable for TasksPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for TasksPane {
    fn panel_name(&self) -> &'static str {
        TASKS
    }

    fn closable(&self, _: &App) -> bool {
        false
    }

    fn set_active(&mut self, active: bool, window: &mut Window, cx: &mut Context<Self>) {
        if active {
            let returning = !self.started;
            self.start(cx);
            // The read of an open page stopped with the tab.
            if returning
                && self.detail.is_some()
                && let Some((token, source)) = self.state.reread()
            {
                self.read_detail(token, source, window, cx);
            }
        } else {
            self.stop();
        }
        cx.notify();
    }
}

impl Panel for TasksPane {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        TASKS
    }
}

impl Render for TasksPane {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.start(cx);
        if let Some(search) = self.saved_search.take() {
            self.search
                .update(cx, |input, cx| input.set_value(search, window, cx));
        }
        let root = div().size_full().min_h_0().track_focus(&self.focus);
        match &self.detail {
            Some(detail) => root.child(detail.clone()),
            None => root.child(self.draw_list(cx)),
        }
    }
}
