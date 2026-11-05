//! `SessionsPane` is the sessions column: a search field (title or project) with view options and New session beside
//! it, provider, status and location pills, the saved grouping and sort popover, and the backend's explicit
//! section, project and session entries as a virtual list, loading
//! the next page as the reader nears its end. Every list and total comes from `sessions/rail`, read again whenever
//! the `sessionRail` topic says a session changed (and at subscribe and each reconnect). A row's menu moves its
//! session between Working and Done or settles it (`sessions/mark`). It registers under the sessions pane's name,
//! after the placeholders.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
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
use super::view_options::{OnView, VIEW_KEY, View, saved_view, view_options};
use crate::core::clock::backend_clock::BackendClock;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::update::Update;
use crate::core::state::machines::GlobalMachines;
use crate::core::state::preferences::Preferences;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::GlobalSessions;
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::sessions as ids;
use crate::core::ui::filter_pills::{FilterGroup, FilterOption, FilterPills, FilterPillsChanged};

const RAIL_TOPIC: &str = "sessionRail";
/// The pills' picks among the backend's preferences.
const FILTERS_KEY: &str = "rail.filters";
/// The pills' groups, in order.
const PROVIDER: usize = 0;
const STATUS: usize = 1;
const LOCATION: usize = 2;

pub struct SessionsPane {
    focus: FocusHandle,
    pub(super) drops: Entity<crate::core::ui::row_drop_down::RowDropDowns>,
    /// The new-session form while it's open, with the subscription that closes it once it's made a session.
    pub(super) creating: Option<(Entity<NewSessionForm>, [Subscription; 2])>,
    /// The marks on their way, each until its answer, by `next_mark`.
    pub(super) marking: HashMap<u64, Task<()>>,
    pub(super) next_mark: u64,
    /// Why the last mark or open failed, until the next one starts.
    pub(super) row_error: Option<SharedString>,
    /// Waits for the answer to the last row opened, so a refusal says why instead of looking like a dead click.
    opening: Option<Subscription>,
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
    pub(super) source_pass: Option<Task<()>>,
    pub(super) source_serial: u64,
    pub(super) source_cut: Option<(Entity<SocketClient>, u64, Vec<Option<String>>)>,
    /// The connection the engine watches were opened on: a new one opens them again.
    pub(super) source_connection: Option<(Entity<SocketClient>, u64)>,
    pub(super) engine_watches:
        HashMap<crate::core::engine::EngineScope, crate::core::engine::EngineWatch>,
    /// The `sessionRail` feed and the backend clock, once the backend's first ready line started the socket.
    feed: Option<(Task<()>, Entity<BackendClock>, Subscription)>,
    /// The socket's and the machines list's observers.
    _backend: Vec<Subscription>,
}

impl SessionsPane {
    /// Registers the column as the layout's Sessions pane. It runs after the placeholders register, since the last
    /// registration for a name wins.
    pub fn register(cx: &mut App) {
        super::search_keys::register_search_keys(cx);
        NewSessionForm::register_projects(cx);
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
                "sessions",
                vec![
                    provider,
                    FilterGroup::new("Status", statuses.into()),
                    FilterGroup::new("Location", locations.into()),
                ],
                cx,
            )
        });
        let engine = crate::core::engine::EngineFeed::global(cx);
        let engine_changed = cx.subscribe(
            &engine,
            |pane, _, changed: &crate::core::engine::EngineChanged, cx| {
                if changed
                    .scopes
                    .iter()
                    .any(|scope| matches!(scope.topic.as_str(), "sessions" | "needs_me"))
                    || changed
                        .keys
                        .iter()
                        .any(|key| matches!(key.kind.as_str(), "session" | "needs_me"))
                {
                    let read = pane.rail.changed();
                    pane.send(read, cx);
                }
                cx.notify();
            },
        );
        let (drops, dropdowns) = Self::dropdowns(cx);
        let mut heard = vec![
            dropdowns,
            engine_changed,
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
        if let Some(preferences) = Preferences::try_global(cx) {
            let mut metrics = Preferences::read(cx, |appearance| {
                (appearance.ui_font(), appearance.ui_text(TextRole::Title))
            });
            heard.push(cx.observe(&preferences, move |pane, _, cx| {
                let next = Preferences::read(cx, |appearance| {
                    (appearance.ui_font(), appearance.ui_text(TextRole::Title))
                });
                if next != metrics {
                    metrics = next;
                    let top = pane.lines.logical_scroll_top();
                    let count = pane.entries.len();
                    pane.lines.remeasure_items(0..count);
                    pane.lines.scroll_to(top);
                    cx.notify();
                }
            }));
        }
        let mut pane = SessionsPane {
            focus: cx.focus_handle(),
            drops,
            creating: None,
            marking: HashMap::new(),
            next_mark: 0,
            row_error: None,
            opening: None,
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
            source_connection: None,
            engine_watches: HashMap::new(),
            source_pass: None,
            source_serial: 0,
            source_cut: None,
            feed: None,
            _backend: Vec::new(),
        };
        pane.follow_backend(cx);
        pane
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
    pub(super) fn open(
        &mut self,
        session_id: String,
        route_machine: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let request_id = uuid::Uuid::new_v4().to_string();
        let expected = request_id.clone();
        let route = route_machine.clone();
        let session = session_id.clone();
        self.row_error = None;
        self.opening = Some(cx.subscribe(
            &crate::core::state::selected_session::SelectedSession::global(cx),
            move |pane, _, event: &crate::core::intents::session_open::SessionOpenResult, cx| {
                if event.request_id != expected {
                    return;
                }
                pane.opening = None;
                if let Err(error) = &event.result {
                    eprintln!(
                        "sessions: {session} on {route:?} didn't open: {}: {}",
                        error.code, error.message
                    );
                    pane.row_error = Some(refusal_words(route.as_deref(), error).into());
                    cx.notify();
                }
            },
        ));
        window.dispatch_action(
            Box::new(crate::core::intents::open_session::OpenSession {
                id: session_id,
                route_machine,
                request_id,
                historical: false,
            }),
            cx,
        );
        self.clear_filter(window, cx);
    }

    /// The top line: the search field (the title or project text, which Escape clears), then view options and New
    /// session. The dock's own tab already names the column.
    fn search(
        &self,
        on_view: OnView,
        new_session: Button,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let theme = cx.theme();
        // The mock's search: sunk below the card, on the ground's colour with a faint edge, a 13 px faint glass.
        let field = h_flex()
            .flex_1()
            .min_w_0()
            .h(px(30.))
            .pl(px(10.))
            .gap(px(5.))
            .rounded(px(9.))
            .bg(theme.background)
            .border_1()
            .border_color(theme.border.opacity(0.6))
            .child(
                Icon::new(IconName::Search)
                    .size(px(13.))
                    .text_color(theme.muted_foreground.opacity(0.65)),
            )
            .child(
                Input::new(&self.filter)
                    .small()
                    .appearance(false)
                    .control(ids::SEARCH, "Search sessions"),
            );
        h_flex()
            .pl(px(12.))
            .pr(px(8.))
            .pt(px(10.))
            .pb(px(10.))
            .gap(px(2.))
            .capture_action(
                cx.listener(|pane, _: &Escape, window, cx| pane.escape_filter(window, cx)),
            )
            .child(field)
            .child(view_options(self.rail.view_options, on_view, cx))
            .child(new_session)
    }
}

/// The column's header buttons, the mock's `.ib`: 26 px, 8 px corners, a 15 px muted icon.
pub fn header_button(button: Button, cx: &App) -> Button {
    button
        .ghost()
        .with_size(px(20.))
        .size(px(26.))
        .rounded(px(8.))
        .text_color(cx.theme().muted_foreground)
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

/// The rail's filtered "need you" total in words, or nothing at zero.
fn needs_words(needs: usize) -> Option<String> {
    match needs {
        0 => None,
        1 => Some("1 needs you".into()),
        n => Some(format!("{n} need you")),
    }
}

/// Says why a row didn't open. A row on another machine is refused as `source_changed` when that machine can't be
/// reached, which reads as nonsense to someone who only clicked a row, so it names the machine instead.
pub(crate) fn refusal_words(
    route: Option<&str>,
    error: &crate::core::engine::EngineRefusal,
) -> String {
    match route {
        _ if error.code == "unknown_engine_session" => NOT_FOUND.into(),
        Some(machine) if error.code == "source_changed" => not_connected(machine),
        _ => format!("That session didn't open: {}", error.message),
    }
}

const NOT_FOUND: &str = "That session wasn't found. It may have been removed, or the link may be wrong; check the Sessions list.";

/// A session on `machine`, which this app can't reach or doesn't have.
pub(crate) fn not_connected(machine: &str) -> String {
    format!("That session is on {machine}, which this app isn't connected to.")
}

#[cfg(test)]
#[path = "sessions_pane_refusal_tests.rs"]
mod refusal_tests;
#[path = "sessions_pane_render.rs"]
mod render;
#[cfg(test)]
#[path = "sessions_pane_tests.rs"]
mod tests;

#[path = "drop_down/rail_engine.rs"]
pub(super) mod engine_rows;
