//! `SessionsPane` draws the sessions list as a virtual list, newest first, loading the next page (`sessions/page`)
//! as the reader nears its end, with the new-session form above it while it's open. It registers under the
//! sessions pane's name, after the placeholders.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::new_session_form::{Created, NewSessionForm};
use super::session_row::SessionRow;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::{GlobalSessions, Sessions};

/// Load the next older page when the last row drawn is this close to the end.
const LOAD_OLDER_WITHIN: usize = 20;

pub struct SessionsPane {
    focus: FocusHandle,
    /// `None` before the backend's first ready line.
    sessions: Option<Entity<Sessions>>,
    /// The new-session form while it's open, with the subscription that closes it once it's made a session.
    creating: Option<(Entity<NewSessionForm>, Subscription)>,
    list: UniformListScrollHandle,
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
        ];
        let mut pane = SessionsPane {
            focus: cx.focus_handle(),
            sessions: None,
            creating: None,
            list: UniformListScrollHandle::new(),
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
        self._backend = backend;
        cx.notify();
    }

    fn toggle_form(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.creating.take().is_none() {
            let form = cx.new(|cx| NewSessionForm::new(window, cx));
            let made = cx.subscribe(&form, |pane, _, Created, cx| {
                pane.creating = None;
                cx.notify();
            });
            self.creating = Some((form, made));
        }
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
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
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
        let header = h_flex()
            .px_3()
            .py_2()
            .justify_between()
            .child(
                div()
                    .text_sm()
                    .font_weight(FontWeight::MEDIUM)
                    .child("Sessions"),
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
        let list = uniform_list(
            "sessions",
            count,
            cx.processor(|pane, range: std::ops::Range<usize>, window, cx| {
                let Some(sessions) = pane.sessions.clone() else {
                    return Vec::new();
                };
                // After a failure, only Retry asks again.
                let failed = sessions.read(cx).older_error().is_some();
                if !failed && range.end + LOAD_OLDER_WITHIN >= sessions.read(cx).items().len() {
                    // Checked again when it runs: a page that failed since this was queued waits for Retry.
                    cx.on_next_frame(window, move |_, _, cx| {
                        sessions.update(cx, |sessions, cx| {
                            if sessions.older_error().is_none() {
                                sessions.load_older(cx);
                            }
                        })
                    });
                }
                let selected = SelectedSession::global(cx)
                    .read(cx)
                    .session_id()
                    .map(String::from);
                let sessions = pane.sessions.as_ref().map(|sessions| sessions.read(cx));
                range
                    .filter_map(|at| sessions.and_then(|sessions| sessions.items().get(at)))
                    .map(|item| {
                        let picked = selected.as_deref() == Some(item.session_id.as_str());
                        SessionRow::of(item, picked)
                    })
                    .collect()
            }),
        )
        .track_scroll(&self.list)
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
                pane.child(
                    div()
                        .px_3()
                        .text_sm()
                        .text_color(muted)
                        .child(empty_line(state.as_ref())),
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
                                        sessions.update(cx, |sessions, cx| sessions.load_older(cx));
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
