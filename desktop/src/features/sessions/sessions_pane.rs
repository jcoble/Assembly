//! `SessionsPane` draws the sessions list as a virtual list in the user's groups (Working, Done, Settled) and, inside
//! each, by project, loading the next page (`sessions/page`) as the reader nears its end, with the new-session form
//! above it while it's open. A row's menu moves its session to another group (`sessions/mark`). It registers under
//! the sessions pane's name, after the placeholders.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use std::collections::HashMap;
use std::rc::Rc;

use super::list_entry::ListEntry;
use super::new_session_form::NewSessionForm;
use super::presence::Presence;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::state::machines::{GlobalMachines, Machines};
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::{GlobalSessions, Sessions};

pub struct SessionsPane {
    focus: FocusHandle,
    /// `None` before the backend's first ready line.
    pub(super) sessions: Option<Entity<Sessions>>,
    /// The new-session form while it's open, with the subscription that closes it once it's made a session.
    pub(super) creating: Option<(Entity<NewSessionForm>, Subscription)>,
    /// Settled is folded until its header is clicked.
    pub(super) settled_open: bool,
    /// The marks on their way, each until its answer, by `next_mark`.
    pub(super) marking: HashMap<u64, Task<()>>,
    pub(super) next_mark: u64,
    /// Why the last mark failed, until the next one starts.
    pub(super) mark_error: Option<SharedString>,
    /// The list's lines as last drawn, and the list's own state (heights measured, where it's scrolled).
    pub(super) entries: Rc<Vec<ListEntry>>,
    pub(super) lines: ListState,
    _heard: Vec<Subscription>,
    /// The sessions list's and the socket's observers, once the backend's first ready line started them.
    _backend: Vec<Subscription>,
}

impl SessionsPane {
    /// Registers the column as the layout's Sessions pane. It runs after the placeholders register, since the last
    /// registration for a name wins.
    pub fn register(cx: &mut App) {
        register_panel(cx, "Sessions", |_, _, cx| {
            panel_handle(cx.new(SessionsPane::new))
        });
    }

    pub fn new(cx: &mut Context<Self>) -> Self {
        let heard = vec![
            cx.observe(&SelectedSession::global(cx), |_, _, cx| cx.notify()),
            cx.observe_global::<GlobalSessions>(|pane, cx| pane.follow_backend(cx)),
            cx.observe_global::<GlobalMachines>(|pane, cx| pane.follow_backend(cx)),
        ];
        // A scroll redraws the pane, and each draw checks whether the end is in view (`follow_entries`).
        let lines = ListState::new(0, ListAlignment::Top, px(200.));
        let mut pane = SessionsPane {
            focus: cx.focus_handle(),
            sessions: None,
            creating: None,
            settled_open: false,
            marking: HashMap::new(),
            next_mark: 0,
            mark_error: None,
            entries: Rc::new(Vec::new()),
            lines,
            _heard: heard,
            _backend: Vec::new(),
        };
        pane.follow_backend(cx);
        pane
    }

    /// Takes the sessions list and the socket once the backend's first ready line has started them, so the column
    /// shows them however late they come, and redraws as the connection's state changes.
    fn follow_backend(&mut self, cx: &mut Context<Self>) {
        self.sessions = Sessions::try_global(cx);
        let socket = SocketClient::try_global(cx);
        let mut backend: Vec<Subscription> = Vec::new();
        if let Some(sessions) = &self.sessions {
            backend.push(cx.observe(sessions, |_, _, cx| cx.notify()));
        }
        if let Some(socket) = socket {
            backend.push(cx.observe(&socket, |_, _, cx| cx.notify()));
        }
        if let Some(machines) = Machines::try_global(cx) {
            backend.push(cx.observe(&machines, |_, _, cx| cx.notify()));
        }
        self._backend = backend;
        cx.notify();
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
        let muted = cx.theme().muted_foreground;
        let state = SocketClient::try_global(cx).map(|socket| socket.read(cx).state().clone());
        let count = self
            .sessions
            .as_ref()
            .map_or(0, |sessions| sessions.read(cx).items().len());
        let older_error = self
            .sessions
            .as_ref()
            .and_then(|sessions| sessions.read(cx).older_error().map(String::from));
        let needs_you = self.sessions.as_ref().map_or(0, |sessions| {
            let items = sessions.read(cx).items();
            items
                .iter()
                .filter(|item| Presence::now(item, cx) == Presence::NeedsYou)
                .count()
        });
        let header = h_flex()
            .px_3()
            .py_2()
            .justify_between()
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        div()
                            .text_sm()
                            .font_weight(FontWeight::MEDIUM)
                            .child("Sessions"),
                    )
                    .when(needs_you > 0, |title| {
                        title.child(div().text_xs().text_color(cx.theme().warning).child(
                            match needs_you {
                                1 => "1 needs you".to_string(),
                                n => format!("{n} need you"),
                            },
                        ))
                    }),
            )
            .child(
                Button::new("new-session")
                    .ghost()
                    .compact()
                    .label(if self.creating.is_some() {
                        "Cancel"
                    } else {
                        "New session"
                    })
                    .on_click(cx.listener(|pane, _, window, cx| pane.toggle_form(window, cx))),
            );
        self.follow_entries(window, cx);
        let list = list(self.lines.clone(), self.line_renderer(cx))
            .flex_1()
            .px_1();
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            .child(header)
            .children(self.creating.as_ref().map(|(form, _)| form.clone()))
            .map(|pane| {
                if count > 0 {
                    return pane.child(list);
                }
                // Nothing open, but Settled's header stays, to open the archive.
                if !self.entries.is_empty() {
                    let line = match state {
                        Some(ConnectionState::Connected { .. }) => "No open sessions.",
                        _ => empty_line(state.as_ref()),
                    };
                    return pane
                        .child(div().px_3().text_sm().text_color(muted).child(line))
                        .child(list);
                }
                pane.child(
                    div()
                        .px_3()
                        .text_sm()
                        .text_color(muted)
                        .child(empty_line(state.as_ref())),
                )
            })
            .when_some(self.mark_error.clone(), |pane, error| {
                pane.child(
                    div()
                        .px_3()
                        .py_1()
                        .text_sm()
                        .text_color(cx.theme().danger)
                        .child(error),
                )
            })
            .when_some(older_error, |pane, error| {
                pane.child(
                    h_flex()
                        .px_3()
                        .py_1()
                        .gap_2()
                        .text_sm()
                        .text_color(cx.theme().danger)
                        .child(error)
                        .child(
                            Button::new("retry-older")
                                .ghost()
                                .compact()
                                .label("Retry")
                                .on_click(cx.listener(|pane, _, _, cx| {
                                    if let Some(sessions) = pane.sessions.clone() {
                                        let settled_open = pane.settled_open;
                                        sessions.update(cx, |sessions, cx| {
                                            sessions.load_next(settled_open, cx)
                                        });
                                    }
                                })),
                        ),
                )
            })
    }
}

#[cfg(test)]
#[path = "sessions_pane_tests.rs"]
mod tests;
