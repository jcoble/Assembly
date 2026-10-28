//! `HistoryPane` owns the drawer's History tab: the loaded sessions by project (`ProjectGroup`), each project folded
//! at first with its count, and open, its sessions newest first, 25 at a time, each with its title, agent and age. A
//! click on a session opens it, as the sessions column does. The tab groups what the sessions list has loaded; a line
//! at its end loads the list's next page, Settled's too, as the sessions column's Settled does once it's opened. The ages count by the backend clock, held only while the tab is shown.

use std::collections::{HashMap, HashSet};

use gpui_kit::assets::IconName;
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::history_row::history_row;
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

type Key = (Option<String>, String);

pub struct HistoryPane {
    focus: FocusHandle,
    /// `None` before the backend's first ready line.
    sessions: Option<Entity<Sessions>>,
    /// The loaded sessions by project, regrouped only when the list changes, not on each clock tick.
    groups: Vec<ProjectGroup>,
    /// The projects opened, by `ProjectGroup::key`; the rest are folded.
    open: HashSet<Key>,
    /// How many of a project's sessions show, past the first `WINDOW`.
    shown: HashMap<Key, usize>,
    /// The kit shows this tab; only then is the clock held.
    active: bool,
    /// The backend clock's last tick (Unix ms), once one has come while the tab is shown.
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
            groups: Vec::new(),
            open: HashSet::new(),
            shown: HashMap::new(),
            active: false,
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
            .map(|sessions| cx.observe(sessions, |pane, _, cx| pane.regroup(cx)));
        self.hold_clock(cx);
        self.regroup(cx);
    }

    fn regroup(&mut self, cx: &mut Context<Self>) {
        self.groups = self.sessions.as_ref().map_or(Vec::new(), |sessions| {
            ProjectGroup::all(sessions.read(cx).items())
        });
        cx.notify();
    }

    /// Holds the backend clock while the tab is shown and the socket client has started, whichever comes last.
    fn hold_clock(&mut self, cx: &mut Context<Self>) {
        if !self.active || self.clock.is_some() || SocketClient::try_global(cx).is_none() {
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

    /// A clock tick redraws the ages: each steps at its own session's minute, so any second can change one.
    pub(super) fn tick(&mut self, now: i64, cx: &mut Context<Self>) {
        self.now = Some(now);
        cx.notify();
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

    /// History lists every session, so Settled's pages load after the open ones'.
    fn load_next(&mut self, cx: &mut Context<Self>) {
        if let Some(sessions) = &self.sessions {
            sessions.update(cx, |sessions, cx| sessions.load_next(true, cx));
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

    /// Holds the backend clock while the tab is shown, so nothing ticks while it's hidden. Hidden, it drops the last
    /// tick too, so a tab shown again never draws ages from when it was hidden.
    fn set_active(&mut self, active: bool, _: &mut Window, cx: &mut Context<Self>) {
        self.active = active;
        if !active {
            (self.clock, self.now) = (None, None);
            return;
        }
        self.hold_clock(cx);
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
        let (muted, hover) = (theme.muted_foreground, theme.muted);
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
        if self.groups.is_empty() {
            return pane.child(note(
                "history-note-empty",
                "No past sessions yet. Sessions you have run appear here, grouped by project.",
            ));
        }
        let older = sessions.has_next();
        let mut lines: Vec<AnyElement> = Vec::new();
        for (index, group) in self.groups.iter().enumerate() {
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
            for item in group.sessions.iter().take(shown) {
                let picked = selected.as_deref() == Some(item.session_id.as_str());
                lines.push(history_row(item, self.now, picked, cx).into_any_element());
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
                    .on_click(cx.listener(|pane, _, _, cx| pane.load_next(cx)))
                    .child(match failed {
                        Some(why) => format!("{why}. Try again"),
                        None => "Load older sessions".into(),
                    }),
            )
        })
    }
}

#[cfg(test)]
#[path = "history_pane_tests.rs"]
mod tests;
