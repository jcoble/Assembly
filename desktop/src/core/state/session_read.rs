//! `SessionRead` owns one call's answer about the selected session, for a tab that shows it: the Worktrees tab's
//! `worktrees/list` and Source control's `git/status`. It reads only while its tab is shown: when the tab comes into
//! view, when the selected session changes while it's shown, and on `refresh`. Hiding the tab or picking another
//! session stops the read on its way, in the window and on the backend (scm#4, base#10).

use gpui_kit::*;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};

use super::selected_session::SelectedSession;
use crate::core::socket::client::SocketClient;
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
    shown: Shown<T>,
    reading: bool,
    /// The read on its way; dropping it cancels the call on the backend.
    _read: Option<Task<()>>,
    _selected: Subscription,
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
            shown: Shown::NoSession,
            reading: false,
            _read: None,
            _selected: selected,
        }
    }

    pub fn shown(&self) -> &Shown<T> {
        &self.shown
    }

    pub fn reading(&self) -> bool {
        self.reading
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
        self.read(true, cx);
    }

    /// Reads the selected session, unless it's already shown or on its way and `again` isn't set.
    fn read(&mut self, again: bool, cx: &mut Context<Self>) {
        let selected = SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from);
        let Some(session) = selected else {
            self.read_for = None;
            self.reading = false;
            self._read = None;
            self.shown = Shown::NoSession;
            cx.notify();
            return;
        };
        let same = self.read_for.as_deref() == Some(session.as_str());
        if same && !again && !matches!(self.shown, Shown::Failed(_)) {
            return;
        }
        if !same {
            self.shown = Shown::Reading;
        }
        self.read_for = Some(session.clone());
        let Some(socket) = SocketClient::try_global(cx) else {
            self.reading = false;
            self._read = None;
            self.shown = Shown::Failed("The backend isn't running.".into());
            cx.notify();
            return;
        };
        // Keyed, so a newer read answers this one Aborted and cancels it on the backend.
        let key = format!("{}/{}", self.feature, self.method);
        let call = socket.read(cx).call::<Value>(
            self.feature,
            self.method,
            json!({ "session": session }),
            Some(&key),
        );
        self.reading = true;
        self._read = Some(cx.spawn(async move |read, cx| {
            let outcome = call.await;
            _ = read.update(cx, |read, cx| {
                read.reading = false;
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
