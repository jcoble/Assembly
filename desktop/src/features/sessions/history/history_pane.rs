//! `HistoryPane` owns the drawer's History tab: the loaded sessions by project (`ProjectGroup`), each project folded
//! at first with its count, and open, its sessions newest first, 25 at a time, each with its title, agent and age. A
//! click on a session opens it, as the sessions column does. The tab groups what the sessions list has loaded; a line
//! at its end loads the list's next older page. The ages count by the backend clock, held only while the tab is shown.

use std::collections::{HashMap, HashSet};

use gpui_kit::assets::IconName;
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::age::age;
use super::project_group::ProjectGroup;
use crate::core::clock::backend_clock::BackendClock;
use crate::core::clock::tick::Tick;
use crate::core::socket::client::SocketClient;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::{GlobalSessions, Sessions};

/// The layout's name for the tab (default_layout.rs).
const HISTORY: &str = "History";
/// How many of a project's sessions show at first, and how many more each "Show more" adds, as the old tab did.
const WINDOW: usize = 25;
const MINUTE: i64 = 60_000;

type Key = (Option<String>, String);

pub struct HistoryPane {
    focus: FocusHandle,
    /// `None` before the backend's first ready line.
    sessions: Option<Entity<Sessions>>,
    /// The projects opened, by `ProjectGroup::key`; the rest are folded.
    open: HashSet<Key>,
    /// How many of a project's sessions show, past the first `WINDOW`.
    shown: HashMap<Key, usize>,
    /// The backend clock's last tick (Unix ms), once one has come.
    now: Option<i64>,
    /// The clock and its subscription, while the tab is shown.
    clock: Option<(Entity<BackendClock>, Subscription)>,
    _heard: Vec<Subscription>,
    _sessions: Option<Subscription>,
}

impl HistoryPane {
    /// Registers the tab under the layout's History name, after the placeholders, since the last registration wins.
    pub fn register(cx: &mut App) {
        register_panel(cx, HISTORY, |_, _, cx| {
            panel_handle(cx.new(HistoryPane::new))
        });
    }

    pub fn new(cx: &mut Context<Self>) -> Self {
        let heard = vec![
            cx.observe(&SelectedSession::global(cx), |_, _, cx| cx.notify()),
            cx.observe_global::<GlobalSessions>(|pane, cx| pane.follow_sessions(cx)),
        ];
        let mut pane = HistoryPane {
            focus: cx.focus_handle(),
            sessions: None,
            open: HashSet::new(),
            shown: HashMap::new(),
            now: None,
            clock: None,
            _heard: heard,
            _sessions: None,
        };
        pane.follow_sessions(cx);
        pane
    }

    /// Takes the sessions list once the backend's first ready line has started it.
    fn follow_sessions(&mut self, cx: &mut Context<Self>) {
        self.sessions = Sessions::try_global(cx);
        self._sessions = self
            .sessions
            .as_ref()
            .map(|sessions| cx.observe(sessions, |_, _, cx| cx.notify()));
        cx.notify();
    }

    /// A clock tick: the ages redraw only when the minute changes, since none says less.
    pub(super) fn tick(&mut self, now: i64, cx: &mut Context<Self>) {
        let minute = |now: Option<i64>| now.map(|now| now / MINUTE);
        if minute(self.now) != minute(Some(now)) {
            cx.notify();
        }
        self.now = Some(now);
    }

    fn toggle(&mut self, key: Key, cx: &mut Context<Self>) {
        if !self.open.remove(&key) {
            self.open.insert(key);
        }
        cx.notify();
    }

    /// Shows the next `WINDOW` of a project's sessions.
    fn show_more(&mut self, key: Key, cx: &mut Context<Self>) {
        *self.shown.entry(key).or_insert(WINDOW) += WINDOW;
        cx.notify();
    }

    fn load_older(&mut self, cx: &mut Context<Self>) {
        if let Some(sessions) = &self.sessions {
            sessions.update(cx, |sessions, cx| sessions.load_older(cx));
        }
    }
}

impl EventEmitter<PanelEvent> for HistoryPane {}

impl Focusable for HistoryPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for HistoryPane {
    fn panel_name(&self) -> &'static str {
        HISTORY
    }

    /// A default layout pane: a permanent tab, with no close button.
    fn closable(&self, _: &App) -> bool {
        false
    }

    /// Holds the backend clock while the tab is shown, so nothing ticks while it's hidden.
    fn set_active(&mut self, active: bool, _: &mut Window, cx: &mut Context<Self>) {
        self.clock = None;
        if !active || SocketClient::try_global(cx).is_none() {
            return;
        }
        let clock = cx.new(|cx| {
            let mut clock = BackendClock::new();
            clock.hold(cx);
            clock
        });
        let heard = cx.subscribe(&clock, |pane, _, tick: &Tick, cx| pane.tick(tick.now, cx));
        self.clock = Some((clock, heard));
    }
}

impl Panel for HistoryPane {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        HISTORY
    }
}

impl Render for HistoryPane {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, secondary, hover) = (theme.muted_foreground, theme.secondary, theme.muted);
        let note = |id: &'static str, text: &'static str| {
            div()
                .debug_selector(move || id.into())
                .px_2()
                .text_color(muted)
                .child(text)
        };
        let selected = SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from);
        let pane = v_flex()
            .id("history-pane")
            .track_focus(&self.focus)
            .size_full()
            .overflow_y_scroll()
            .p_2()
            .gap_px()
            .text_size(px(13.));
        let Some(sessions) = self.sessions.clone() else {
            return pane.child(note(
                "history-note-waiting",
                "Waiting for the backend to start…",
            ));
        };
        let sessions = sessions.read(cx);
        let groups = ProjectGroup::all(sessions.items());
        if groups.is_empty() {
            return pane.child(note(
                "history-note-empty",
                "No past sessions yet. Sessions you have run appear here, grouped by project.",
            ));
        }
        let older = sessions.has_older();
        let item = |id: &str| sessions.items().iter().find(|item| item.session_id == id);
        let mut lines: Vec<AnyElement> = Vec::new();
        for (index, group) in groups.iter().enumerate() {
            let key = group.key();
            let open = self.open.contains(&key);
            let header = format!("history-project-{}", group.folder);
            let toggled = key.clone();
            lines.push(
                h_flex()
                    .id(("history-project", index))
                    .debug_selector(move || header)
                    .h(px(28.))
                    .px_2()
                    .gap_1p5()
                    .rounded(px(6.))
                    .cursor_pointer()
                    .hover(|line| line.bg(hover))
                    .on_click(cx.listener(move |pane, _, _, cx| pane.toggle(toggled.clone(), cx)))
                    .child(
                        Icon::new(if open {
                            IconName::ChevronDown
                        } else {
                            IconName::ChevronRight
                        })
                        .size(px(12.))
                        .text_color(muted),
                    )
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .truncate()
                            .font_weight(FontWeight::MEDIUM)
                            .child(group.label()),
                    )
                    .child(
                        div()
                            .text_color(muted)
                            .child(group.sessions.len().to_string()),
                    )
                    .into_any_element(),
            );
            if !open {
                continue;
            }
            let shown = self.shown.get(&key).copied().unwrap_or(WINDOW);
            for id in group.sessions.iter().take(shown) {
                let Some(item) = item(id) else { continue };
                let (row, age_id, picked) = (
                    format!("history-session-{id}"),
                    format!("history-age-{id}"),
                    selected.as_deref() == Some(id.as_str()),
                );
                let pick = id.clone();
                lines.push(
                    h_flex()
                        .id(SharedString::from(row.clone()))
                        .debug_selector(move || row)
                        .h(px(28.))
                        .pl(px(26.))
                        .pr_2()
                        .gap_2()
                        .rounded(px(6.))
                        .cursor_pointer()
                        .when(picked, |line| line.bg(secondary))
                        .when(!picked, |line| line.hover(|line| line.bg(hover)))
                        .on_click(move |_, _, cx| {
                            let id = pick.clone();
                            SelectedSession::global(cx)
                                .update(cx, |selected, cx| selected.select(Some(id), cx));
                        })
                        .child(div().flex_1().min_w_0().truncate().child(item.title()))
                        .child(
                            div()
                                .flex_none()
                                .text_color(muted)
                                .child(item.agent.clone()),
                        )
                        .when_some(self.now, |line, now| {
                            line.child(
                                div()
                                    .debug_selector(move || age_id)
                                    .flex_none()
                                    .w(px(28.))
                                    .text_right()
                                    .text_color(muted)
                                    .child(age(now, item.updated_at)),
                            )
                        })
                        .into_any_element(),
                );
            }
            if group.sessions.len() > shown {
                let more = format!("history-more-{}", group.folder);
                let key = key.clone();
                lines.push(
                    div()
                        .id(("history-more", index))
                        .debug_selector(move || more)
                        .h(px(28.))
                        .pl(px(26.))
                        .flex()
                        .items_center()
                        .cursor_pointer()
                        .text_color(muted)
                        .hover(|line| line.text_color(theme.foreground))
                        .on_click(
                            cx.listener(move |pane, _, _, cx| pane.show_more(key.clone(), cx)),
                        )
                        .child(format!("Show {WINDOW} more"))
                        .into_any_element(),
                );
            }
        }
        let failed = sessions.older_error().map(String::from);
        pane.children(lines).when(older, |pane| {
            pane.child(
                div()
                    .id("history-older")
                    .debug_selector(|| "history-older".into())
                    .h(px(28.))
                    .px_2()
                    .flex()
                    .items_center()
                    .cursor_pointer()
                    .text_color(muted)
                    .hover(|line| line.text_color(theme.foreground))
                    .on_click(cx.listener(|pane, _, _, cx| pane.load_older(cx)))
                    .child(match failed {
                        Some(why) => format!("Couldn't load older sessions: {why}. Try again"),
                        None => "Load older sessions".into(),
                    }),
            )
        })
    }
}

#[cfg(test)]
#[path = "history_pane_tests.rs"]
mod tests;
