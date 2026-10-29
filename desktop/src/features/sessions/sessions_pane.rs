//! `SessionsPane` is the sessions column: its header (view options and New session), a search field (title or
//! project), provider, location and status pills, the saved grouping and sort popover, and the backend's explicit
//! section, project and session entries as a virtual list, loading
//! the next page as the reader nears its end. Every list and total comes from `sessions/rail`, read again whenever
//! the `sessionRail` topic says a session changed (and at subscribe and each reconnect). A row's menu moves its
//! session between Working and Done or settles it (`sessions/mark`). It registers under the sessions pane's name,
//! after the placeholders.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::input::{Escape, Input, InputEvent, InputState};
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::{Value, json};
use std::cell::Cell;
use std::collections::HashMap;
use std::rc::Rc;

use super::list_entry::ListEntry;
use super::new_session_form::NewSessionForm;
use super::rail_page::{Rail, RailPage, Request};
use super::view_options::{VIEW_KEY, View, saved_view, view_options};
use crate::core::clock::backend_clock::BackendClock;
use crate::core::clock::tick::Tick;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::update::Update;
use crate::core::state::machines::{GlobalMachines, Machines};
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::GlobalSessions;
use crate::core::ui::filter_pills::{FilterGroup, FilterOption, FilterPills, FilterPillsChanged};

const RAIL_TOPIC: &str = "sessionRail";
/// The pills' picks among the backend's preferences.
const FILTERS_KEY: &str = "rail.filters";
/// The pills' groups, in order.
const PROVIDER: usize = 0;
const LOCATION: usize = 1;
const STATUS: usize = 2;

pub struct SessionsPane {
    focus: FocusHandle,
    /// The new-session form while it's open, with the subscription that closes it once it's made a session.
    pub(super) creating: Option<(Entity<NewSessionForm>, Subscription)>,
    /// The marks on their way, each until its answer, by `next_mark`.
    pub(super) marking: HashMap<u64, Task<()>>,
    pub(super) next_mark: u64,
    /// Why the last mark failed, until the next one starts.
    pub(super) mark_error: Option<SharedString>,
    /// What the column asks for and holds.
    pub(super) rail: Rail,
    /// The read that's out; replacing it drops the older one.
    reading: Option<Task<()>>,
    /// The search field's title or project text.
    pub(super) filter: Entity<InputState>,
    /// The provider and location pills; their picks feed the rail's read.
    pills: Entity<FilterPills>,
    /// The saved picks' read, until the `preferences` topic's first state.
    restoring: Option<Task<()>>,
    /// The last picks' save, while it's being written.
    saving: Option<Task<()>>,
    /// The list's lines as last drawn, and the list's own state (heights measured, where it's scrolled).
    pub(super) entries: Rc<Vec<ListEntry>>,
    pub(super) lines: ListState,
    /// The drawn lines start at the rail's real start, not a window further down.
    pub(super) from_start: bool,
    /// The backend clock's last tick, for the rows' ages and running times.
    pub(super) now: Option<i64>,
    /// The last frame drew a running row, so the next tick redraws its running time.
    pub(super) running_drawn: Rc<Cell<bool>>,
    _heard: Vec<Subscription>,
    /// The `sessionRail` feed and the backend clock, once the backend's first ready line started the socket.
    feed: Option<(Task<()>, Entity<BackendClock>, Subscription)>,
    /// The socket's and the machines list's observers.
    _backend: Vec<Subscription>,
}

impl SessionsPane {
    /// Registers the column as the layout's Sessions pane. It runs after the placeholders register, since the last
    /// registration for a name wins.
    pub fn register(cx: &mut App) {
        register_panel(cx, "Sessions", |_, window, cx| {
            panel_handle(cx.new(|cx| SessionsPane::new(window, cx)))
        });
    }

    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let filter = cx.new(|cx| InputState::new(window, cx).placeholder("Search sessions…"));
        let locations =
            [("local", "Local"), ("remote", "Remote")].map(|(value, label)| FilterOption {
                value: value.into(),
                label: label.into(),
            });
        let statuses = [
            ("working", "Working"),
            ("done", "Done"),
            ("settled", "Settled"),
        ]
        .map(|(value, label)| FilterOption {
            value: value.into(),
            label: label.into(),
        });
        let pills = cx.new(|cx| {
            let provider = FilterGroup::new("Provider", Vec::new());
            FilterPills::new(
                vec![
                    provider,
                    FilterGroup::new("Location", locations.into()),
                    FilterGroup::new("Status", statuses.into()),
                ],
                cx,
            )
        });
        let heard = vec![
            cx.observe(&SelectedSession::global(cx), |_, _, cx| cx.notify()),
            cx.observe_global::<GlobalSessions>(|pane, cx| pane.follow_backend(cx)),
            cx.observe_global::<GlobalMachines>(|pane, cx| pane.follow_backend(cx)),
            cx.subscribe(&filter, |pane, _, event: &InputEvent, cx| {
                if let InputEvent::Change = event {
                    let text = pane.filter.read(cx).value();
                    let read = pane.rail.set_filter(&text);
                    pane.send(read, cx);
                }
            }),
            cx.subscribe(&pills, |pane, pills, _: &FilterPillsChanged, cx| {
                let groups = &pills.read(cx).state.groups;
                let picks = (
                    groups[PROVIDER].picked.clone(),
                    groups[LOCATION].picked.clone(),
                    groups[STATUS].picked.clone(),
                );
                pane.save_filters(&picks, cx);
                let read = pane.rail.set_picks(picks.0, picks.1, picks.2);
                pane.send(Some(read), cx);
            }),
        ];
        let mut pane = SessionsPane {
            focus: cx.focus_handle(),
            creating: None,
            marking: HashMap::new(),
            next_mark: 0,
            mark_error: None,
            rail: Rail::default(),
            reading: None,
            filter,
            pills,
            restoring: None,
            saving: None,
            entries: Rc::new(Vec::new()),
            lines: ListState::new(0, ListAlignment::Top, px(200.)),
            from_start: true,
            now: None,
            running_drawn: Rc::default(),
            _heard: heard,
            feed: None,
            _backend: Vec::new(),
        };
        pane.follow_backend(cx);
        pane
    }

    /// Once the backend's first ready line has started the socket: subscribes to `sessionRail`, whose state (now and
    /// after each reconnect) and every change read the rail again, holds the backend clock for the ages, and redraws
    /// as the connection and the machines change.
    fn follow_backend(&mut self, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return cx.notify();
        };
        if self.feed.is_none() {
            let mut changes = socket.read(cx).subscribe(RAIL_TOPIC, None);
            let feed = cx.spawn(async move |pane, cx| {
                while changes.updates.recv().await.is_ok() {
                    let heard = pane.update(cx, |pane, cx| {
                        let read = pane.rail.changed();
                        pane.send(read, cx);
                    });
                    if heard.is_err() {
                        return;
                    }
                }
            });
            let clock = cx.new(|cx| {
                let mut clock = BackendClock::new();
                clock.hold(cx);
                clock
            });
            let ticks = cx.subscribe(&clock, |pane, _, tick: &Tick, cx| pane.tick(tick.now, cx));
            self.feed = Some((feed, clock, ticks));
            let mut preferences = socket.read(cx).subscribe("preferences", None);
            self.restoring = Some(cx.spawn(async move |pane, cx| {
                let first = preferences.updates.recv().await.ok();
                if let Some(first) = first.as_ref() {
                    let (providers, locations, statuses) = saved_filters(first).unwrap_or_default();
                    let view = saved_view(first);
                    pane.update(cx, |pane, cx| {
                        pane.restore(providers, locations, statuses, view, cx)
                    })
                    .ok();
                }
            }));
        }
        let mut backend = vec![cx.observe(&socket, |_, _, cx| cx.notify())];
        backend.extend(
            Machines::try_global(cx).map(|machines| cx.observe(&machines, |_, _, cx| cx.notify())),
        );
        self._backend = backend;
        cx.notify();
    }

    /// Whether the column follows the backend's rail topic.
    #[cfg(test)]
    pub(super) fn following(&self) -> bool {
        self.feed.is_some()
    }

    /// Ages are minutes at the finest, so a tick redraws when the minute turns, and each second only while a running
    /// row's time is in view.
    fn tick(&mut self, now: i64, cx: &mut Context<Self>) {
        let turned = self.now.map(|held| held / 60_000) != Some(now / 60_000);
        self.now = Some(now);
        if turned || self.running_drawn.get() {
            cx.notify();
        }
    }

    /// Sends a rail read, with one key, so a newer read cancels an older one on the backend too.
    pub(super) fn send(&mut self, read: Option<Request>, cx: &mut Context<Self>) {
        let Some((id, args)) = read else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            // No backend yet: the topic's first state reads once it starts.
            let next = self.rail.finish(id, Outcome::Aborted);
            return self.send(next, cx);
        };
        let call =
            socket
                .read(cx)
                .call::<RailPage>("sessions", "rail", args, Some("sessions/rail"));
        self.reading = Some(cx.spawn(async move |pane, cx| {
            let outcome = call.await;
            pane.update(cx, |pane, cx| pane.answer(id, outcome, cx))
                .ok();
        }));
        cx.notify();
    }

    pub(super) fn set_view(&mut self, view: View, cx: &mut Context<Self>) {
        self.lines.scroll_to(ListOffset::default());
        self.from_start = true;
        self.save_view(view, cx);
        let read = self.rail.set_view(view);
        self.send(Some(read), cx);
    }

    /// Takes a read's answer, and sends the read it asks for next. The selection is left alone: a session that left
    /// the current view stays open.
    pub(super) fn answer(&mut self, id: u64, outcome: Outcome<RailPage>, cx: &mut Context<Self>) {
        let next = self.rail.finish(id, outcome);
        self.show_providers(cx);
        self.send(next, cx);
        cx.notify();
    }

    /// The saved picks, into the pills and the rail, in one read.
    fn restore(
        &mut self,
        providers: Vec<String>,
        locations: Vec<String>,
        statuses: Vec<String>,
        view: View,
        cx: &mut Context<Self>,
    ) {
        let picks = (providers.clone(), locations.clone(), statuses.clone());
        self.pills.update(cx, |pills, cx| {
            let groups = &mut pills.state.groups;
            (
                groups[PROVIDER].picked,
                groups[LOCATION].picked,
                groups[STATUS].picked,
            ) = picks;
            cx.notify();
        });
        let read = self.rail.restore(providers, locations, statuses, view);
        self.show_providers(cx);
        self.send(Some(read), cx);
    }

    /// The provider pills: the backend's providers, and any picked one it hasn't listed yet.
    fn show_providers(&mut self, cx: &mut Context<Self>) {
        let options = provider_options(&self.rail.agents, &self.rail.providers);
        self.pills.update(cx, |pills, _| {
            pills.state.groups[PROVIDER].options = options
        });
    }

    /// Saves the picks as the `rail.filters` preference; a newer save replaces this one.
    fn save_filters(
        &mut self,
        (providers, locations, statuses): &(Vec<String>, Vec<String>, Vec<String>),
        cx: &mut Context<Self>,
    ) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let value = json!({"provider": providers, "location": locations, "status": statuses});
        let save = (socket.read(cx)).call::<Value>(
            "preferences",
            "set",
            json!({"key": FILTERS_KEY, "value": value}),
            None,
        );
        self.saving = Some(cx.spawn(async move |_, _| {
            if let Outcome::Failed(failure) = save.await {
                eprintln!("sessions: the filters weren't saved: {}", failure.message);
            }
        }));
    }

    /// Saves grouping and row order; folds stay only in this pane.
    fn save_view(&mut self, view: View, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let save = (socket.read(cx)).call::<Value>(
            "preferences",
            "set",
            json!({"key": VIEW_KEY, "value": view}),
            None,
        );
        self.saving = Some(cx.spawn(async move |_, _| {
            if let Outcome::Failed(failure) = save.await {
                eprintln!("sessions: the view wasn't saved: {}", failure.message);
            }
        }));
    }

    /// Opens a row's session, and clears the filter, as picking a result did in the old rail.
    pub(super) fn open(&mut self, session_id: String, window: &mut Window, cx: &mut Context<Self>) {
        SelectedSession::global(cx)
            .update(cx, |selected, cx| selected.select(Some(session_id), cx));
        self.clear_filter(window, cx);
    }

    /// Escape in the filter, or a pick: the filter empties and the rail reads without it.
    pub(super) fn clear_filter(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.filter
            .update(cx, |input, cx| input.set_value("", window, cx));
        let read = self.rail.set_filter("");
        self.send(read, cx);
    }

    /// The search field: the title or project text, which Escape clears.
    fn search(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let icon = Icon::new(IconName::Search)
            .xsmall()
            .text_color(cx.theme().muted_foreground);
        div()
            .px(px(12.))
            .pb(px(10.))
            .capture_action(
                cx.listener(|pane, _: &Escape, window, cx| pane.clear_filter(window, cx)),
            )
            .child(Input::new(&self.filter).small().prefix(icon))
    }
}

/// What an empty column says, from the connection's state (`None` before the backend's first ready line).
pub fn empty_line(state: Option<&ConnectionState>) -> &'static str {
    match state {
        Some(ConnectionState::Connected { .. }) => "No sessions yet.",
        Some(ConnectionState::Connecting) => "Connecting to the backend…",
        None | Some(ConnectionState::Disconnected | ConnectionState::Refused { .. }) => {
            "The backend isn't running."
        }
    }
}

/// The saved provider and location picks in the `preferences` topic's first update, its state.
pub fn saved_filters(update: &Update) -> Option<(Vec<String>, Vec<String>, Vec<String>)> {
    let Update::State(items) = update else {
        return None;
    };
    let (_, item) = items.iter().find(|(key, _)| key == FILTERS_KEY)?;
    let picks =
        |group: &str| serde_json::from_value(item["value"][group].clone()).unwrap_or_default();
    Some((picks("provider"), picks("location"), picks("status")))
}

/// One pill per provider the backend listed, then any picked one it hasn't listed yet; "claude" reads "Claude".
pub fn provider_options(agents: &[String], picked: &[String]) -> Vec<FilterOption> {
    let extra = picked.iter().filter(|pick| !agents.contains(pick));
    (agents.iter().chain(extra))
        .map(|agent| {
            let mut letters = agent.chars();
            let first = letters.next().map(|first| first.to_uppercase());
            FilterOption {
                value: agent.clone(),
                label: first.into_iter().flatten().chain(letters).collect(),
            }
        })
        .collect()
}

impl EventEmitter<PanelEvent> for SessionsPane {}

impl Focusable for SessionsPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for SessionsPane {
    /// The sessions pane's name in the saved layout (g0-shape's `SESSIONS`).
    fn panel_name(&self) -> &'static str {
        "Sessions"
    }

    /// The sessions column is a permanent pane.
    fn closable(&self, _cx: &App) -> bool {
        false
    }
}

impl Panel for SessionsPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        "Sessions"
    }
}

impl Render for SessionsPane {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let (muted, danger) = (cx.theme().muted_foreground, cx.theme().danger);
        let state = SocketClient::try_global(cx).map(|socket| socket.read(cx).state().clone());
        let creating = self.creating.is_some();
        let pane = cx.entity().downgrade();
        let on_view: super::view_options::OnView =
            Rc::new(move |view, _: &mut Window, cx: &mut App| {
                pane.update(cx, |pane, cx| pane.set_view(view, cx)).ok();
            });
        let header = h_flex()
            .pl(px(16.))
            .pr(px(10.))
            .pt(px(12.))
            .pb(px(8.))
            .gap(px(2.))
            .child(
                div()
                    .flex_1()
                    .text_base()
                    .font_weight(FontWeight::SEMIBOLD)
                    .child(format!("Sessions · {} need you", self.rail.totals.needs)),
            )
            .child(view_options(self.rail.view_options, on_view))
            .child(
                Button::new("new-session")
                    .ghost()
                    .compact()
                    .icon(if creating {
                        IconName::X
                    } else {
                        IconName::Plus
                    })
                    .tooltip(if creating { "Cancel" } else { "New session" })
                    .on_click(cx.listener(|pane, _, window, cx| pane.toggle_form(window, cx))),
            );
        self.follow_entries(window, cx);
        self.running_drawn.set(false);
        let list = list(self.lines.clone(), self.line_renderer(cx)).flex_1();
        let empty = match self.rail.loaded {
            true => "No sessions here.",
            false => empty_line(state.as_ref()),
        };
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            .child(header)
            .child(self.search(cx))
            .child(self.pills.clone())
            .children(self.creating.as_ref().map(|(form, _)| form.clone()))
            .map(|pane| match self.entries.is_empty() {
                false => pane.child(list),
                true => pane.child(div().px_3().py_2().text_sm().text_color(muted).child(empty)),
            })
            .when_some(self.mark_error.clone(), |pane, error| {
                pane.child(
                    div()
                        .px_3()
                        .py_1()
                        .text_sm()
                        .text_color(danger)
                        .child(error),
                )
            })
            .when_some(self.rail.error.clone(), |pane, error| {
                pane.child(
                    h_flex()
                        .px_3()
                        .py_1()
                        .gap_2()
                        .text_sm()
                        .text_color(danger)
                        .child(error)
                        .child(
                            Button::new("retry-rail")
                                .ghost()
                                .compact()
                                .label("Retry")
                                .on_click(cx.listener(|pane, _, _, cx| {
                                    let read = pane.rail.changed();
                                    pane.send(read, cx);
                                })),
                        ),
                )
            })
    }
}

#[cfg(test)]
#[path = "sessions_pane_tests.rs"]
mod tests;
