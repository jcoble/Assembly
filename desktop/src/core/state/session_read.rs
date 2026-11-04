//! `SessionRead` owns one call's answer about the selected session, for a tab that shows it: the Worktrees tab's
//! `worktrees/list` and Source control's `git/status`. It reads only while its tab is shown: when the tab comes into
//! view, when the selected session changes while it's shown, and on `refresh`. Hiding the tab or picking another
//! session stops the read on its way, in the window and on the backend (scm#4, base#10).

use gpui_kit::*;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};

use super::selected_session::SelectedSession;
use super::sessions::Sessions;
use crate::core::socket::checkout_selector::CheckoutSelector;
use crate::core::socket::client::SocketClient;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::outcome::Outcome;

/// What the tab shows.
#[derive(Clone, Debug, PartialEq)]
pub enum Shown<T> {
    /// No session is selected.
    NoSession,
    /// The first read for this session is on its way.
    Reading,
    Read(T),
    /// The backend's message (git's own, for a git failure).
    Failed(String),
}

pub struct SessionRead<T> {
    feature: &'static str,
    method: &'static str,
    /// The tab is shown.
    active: bool,
    /// The session what's shown, or the read on its way, is for.
    read_for: Option<String>,
    source: Option<ExpectedRowSource>,
    /// A linked checkout to read instead, for the session and source it was picked under and no other.
    checkout: Option<(String, Option<ExpectedRowSource>, CheckoutSelector)>,
    shown: Shown<T>,
    /// Why the shown failure failed, for a next step such as Connect.
    failure_code: Option<ErrorCode>,
    /// Counts answers from the backend. A status a write returned through `show` isn't one.
    answers: u64,
    reading: bool,
    /// The read on its way; dropping it cancels the call on the backend.
    _read: Option<Task<()>>,
    _selected: Subscription,
    _settled: Option<Subscription>,
    settled_state: Option<(String, String)>,
}

impl<T: DeserializeOwned + 'static> SessionRead<T> {
    /// Reads `feature/method` with `{session}`.
    pub fn new(feature: &'static str, method: &'static str, cx: &mut Context<Self>) -> Self {
        let selected = cx.observe(&SelectedSession::global(cx), |read, _, cx| {
            if read.active {
                read.read(false, cx);
            }
        });
        Self {
            feature,
            method,
            active: false,
            read_for: None,
            source: None,
            checkout: None,
            shown: Shown::NoSession,
            failure_code: None,
            answers: 0,
            reading: false,
            _read: None,
            _selected: selected,
            _settled: None,
            settled_state: None,
        }
    }

    /// Refreshes on the selected session's actual running-to-settled transition; never polls.
    pub fn refresh_on_settle(&mut self, cx: &mut Context<Self>) {
        if self._settled.is_some() {
            return;
        }
        let Some(sessions) = Sessions::try_global(cx) else {
            return;
        };
        self.settled_state = Self::selected_state(cx);
        self._settled = Some(cx.observe(&sessions, |read, _, cx| {
            let current = Self::selected_state(cx);
            let ended = match (&read.settled_state, &current) {
                (Some((prior_id, prior)), Some((id, state))) => {
                    prior_id == id && prior == "running" && state != "running"
                }
                _ => false,
            };
            read.settled_state = current;
            if read.active && ended {
                read.read(true, cx);
            }
        }));
    }
    fn selected_state(cx: &App) -> Option<(String, String)> {
        let selected = SelectedSession::global(cx);
        let id = selected.read(cx).session_id()?;
        let sessions = Sessions::try_global(cx)?;
        sessions
            .read(cx)
            .items()
            .iter()
            .find(|item| item.session_id == id)
            .map(|item| (item.session_id.clone(), item.state.clone()))
    }

    pub fn shown(&self) -> &Shown<T> {
        &self.shown
    }

    /// The session what's shown, or the read on its way, is for.
    pub fn session(&self) -> Option<&str> {
        self.read_for.as_deref()
    }

    pub fn source(&self) -> Option<&ExpectedRowSource> {
        self.source.as_ref()
    }

    /// The linked checkout being read, if it isn't the session's own.
    pub fn checkout(&self) -> Option<&CheckoutSelector> {
        self.checkout.as_ref().map(|(_, _, checkout)| checkout)
    }

    /// Why the shown failure failed.
    pub fn failure_code(&self) -> Option<&ErrorCode> {
        self.failure_code.as_ref()
    }

    /// Reads another checkout of the session it's on, or its own again. What was shown belongs to the last one.
    pub fn set_checkout(&mut self, checkout: Option<CheckoutSelector>, cx: &mut Context<Self>) {
        let bound = match (checkout, &self.read_for) {
            (Some(checkout), Some(session)) => Some((session.clone(), self.source.clone(), checkout)),
            _ => None,
        };
        if bound == self.checkout {
            return;
        }
        self.checkout = bound;
        self.failure_code = None;
        self.shown = Shown::Reading;
        cx.notify();
        if self.active {
            self.read(true, cx);
        }
    }

    pub fn reading(&self) -> bool {
        self.reading
    }

    pub fn answers(&self) -> u64 {
        self.answers
    }

    /// The machine that didn't answer, when that is why the read failed.
    pub fn unreachable_machine(&self) -> Option<&str> {
        match &self.failure_code {
            Some(ErrorCode::Unreachable { machine }) => Some(machine),
            _ => None,
        }
    }

    /// Shows what a write returned with no second read. A read on its way is older, so it stops.
    pub fn show(&mut self, value: T, cx: &mut Context<Self>) {
        self.reading = false;
        self._read = None;
        self.failure_code = None;
        self.shown = Shown::Read(value);
        cx.notify();
    }

    /// Shown, it reads again, since what it shows may have changed while hidden; hidden, a read on its way stops.
    pub fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        self.active = active;
        if active {
            self.read(true, cx);
        } else if self.reading {
            self.reading = false;
            self._read = None;
            self.read_for = None;
            cx.notify();
        }
    }

    /// Reads again (a Refresh or Try again button).
    pub fn refresh(&mut self, cx: &mut Context<Self>) {
        if self.active {
            self.read(true, cx);
        }
    }

    /// Reads the selected session, unless it's already shown or on its way and `again` isn't set.
    fn read(&mut self, again: bool, cx: &mut Context<Self>) {
        let selection = SelectedSession::global(cx);
        let selection = selection.read(cx);
        let selected = selection.session_id().map(String::from);
        let source = selection.source().cloned();
        let Some(session) = selected else {
            self.read_for = None;
            self.reading = false;
            self._read = None;
            self.source = None;
            self.checkout = None;
            self.shown = Shown::NoSession;
            cx.notify();
            return;
        };
        self.checkout = self
            .checkout
            .take()
            .filter(|(bound, from, _)| *bound == session && *from == source);
        let same = self.read_for.as_deref() == Some(session.as_str()) && self.source == source;
        if same && !again && !matches!(self.shown, Shown::Failed(_)) {
            return;
        }
        if !same {
            self.shown = Shown::Reading;
        }
        self.settled_state = Self::selected_state(cx);
        self.read_for = Some(session.clone());
        self.source = source.clone();
        let Some(socket) = SocketClient::try_global(cx) else {
            self.reading = false;
            self._read = None;
            self.failure_code = None;
            self.shown = Shown::Failed("The backend isn't running.".into());
            cx.notify();
            return;
        };
        // Keyed, so a newer read answers this one Aborted and cancels it on the backend.
        let key = format!("{}/{}", self.feature, self.method);
        let args = CheckoutSelector::applied(
            self.checkout(),
            json!({ "session": session, "expectedSource": source }),
        );
        let call = socket.read(cx).call::<Value>(self.feature, self.method, args, Some(&key));
        self.reading = true;
        self._read = Some(cx.spawn(async move |read, cx| {
            let outcome = call.await;
            _ = read.update(cx, |read, cx| {
                if read.read_for.as_deref() != Some(session.as_str()) || read.source != source { return; }
                read.reading = false;
                read.answers += 1;
                read.failure_code = match &outcome {
                    Outcome::Failed(failure) => Some(failure.code.clone()),
                    _ => None,
                };
                read.shown = match outcome {
                    Outcome::Ok(result) => match serde_json::from_value(result) {
                        Ok(answer) => Shown::Read(answer),
                        Err(error) => Shown::Failed(format!("The answer didn't read: {error}")),
                    },
                    // A newer read drops this task before its answer comes, so an abort here is the socket closing.
                    Outcome::Aborted => Shown::Failed("The connection dropped.".into()),
                    Outcome::Failed(failure) => Shown::Failed(failure.message),
                };
                cx.notify();
            });
        }));
        cx.notify();
    }
}

#[cfg(test)]
#[path = "session_read_tests.rs"]
mod tests;
