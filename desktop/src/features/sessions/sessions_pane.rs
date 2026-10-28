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

use super::group_header::GroupHeader;
use super::list_entries::list_entries;
use super::list_entry::ListEntry;
use super::new_session_form::NewSessionForm;
use super::project_header::ProjectHeader;
use super::session_row::{OnMark, SessionRow};
use super::status::Status;
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
    pub(super) creating: Option<(Entity<NewSessionForm>, Subscription)>,
    /// Settled is folded until its header is clicked.
    pub(super) settled_open: bool,
    /// Each session's mark on its way, by session id: a newer mark of the same session replaces the older.
    pub(super) marking: HashMap<String, Task<()>>,
    /// Why the last mark failed, until the next one starts.
    pub(super) mark_error: Option<SharedString>,
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
            settled_open: false,
            marking: HashMap::new(),
            mark_error: None,
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
        let entries = Rc::new(self.sessions.as_ref().map_or_else(Vec::new, |sessions| {
            list_entries(sessions.read(cx).items(), self.settled_open)
        }));
        let pane = cx.entity().downgrade();
        let on_mark: OnMark = Rc::new(move |session_id, to, cx| {
            pane.update(cx, |pane, cx| pane.mark(session_id, to, cx))
                .ok();
        });
        let lines = entries.len();
        let list = uniform_list(
            "sessions",
            lines,
            cx.processor(move |pane, range: std::ops::Range<usize>, window, cx| {
                let Some(sessions) = pane.sessions.clone() else {
                    return Vec::new();
                };
                // After a failure, only Retry asks again.
                let failed = sessions.read(cx).older_error().is_some();
                if !failed && range.end + LOAD_OLDER_WITHIN >= entries.len() {
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
                let items = pane.sessions.clone();
                range
                    .filter_map(|at| entries.get(at).cloned())
                    .filter_map(|entry| match entry {
                        ListEntry::Group { status, open } => {
                            let header = GroupHeader::new(status, open);
                            Some(if status == Status::Settled {
                                header
                                    .on_toggle(cx.listener(|pane, _, _, cx| {
                                        pane.settled_open = !pane.settled_open;
                                        cx.notify();
                                    }))
                                    .into_any_element()
                            } else {
                                header.into_any_element()
                            })
                        }
                        ListEntry::Project {
                            status,
                            folder,
                            machine,
                        } => {
                            let (at, on) = (folder.clone(), machine.clone());
                            Some(
                                ProjectHeader::new(
                                    status,
                                    folder,
                                    machine,
                                    cx.listener(move |pane, _, window, cx| {
                                        pane.new_in(&at, on.clone(), window, cx)
                                    }),
                                )
                                .into_any_element(),
                            )
                        }
                        ListEntry::Session(at) => {
                            let item = items.as_ref()?.read(cx).items().get(at)?;
                            let picked = selected.as_deref() == Some(item.session_id.as_str());
                            Some(
                                SessionRow::of(item, picked)
                                    .on_mark(on_mark.clone())
                                    .into_any_element(),
                            )
                        }
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
