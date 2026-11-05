//! What the Terminal tab does as its connection and its shell change, and what it says about them (spec section 4).
//!
//! A connection that's down is `Reconnecting`, quietly: with a link the rows or the socket's next state bring it back;
//! with none, the open is tried again, from pushed state or, when no push can come, on a timer. A shell that's gone and
//! any other failure stop the tab, with the output kept.

use std::time::Duration;

use gpui_kit::*;

use super::{Shown, TerminalPane};
use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::update::Update;
use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::Sessions;
use crate::features::terminal::terminal_link::{
    Availability, Output, connection_down, source_changed,
};

const GONE: &str = "This terminal has ended. Close it and open it again for a new shell.";
/// The note for a `source_changed` row, which carries no message of its own (OutputRow.cs).
const SOURCE_CHANGED: &str = "The terminal's original owning source is unavailable or changed.";

/// The waits between tries to open, when no push can come; the last one repeats.
const RETRY_WAITS: [Duration; 3] = [
    Duration::from_secs(1),
    Duration::from_secs(2),
    Duration::from_secs(5),
];

impl TerminalPane {
    /// Reads the kept output from where the grid got to, then hands on the rows held meanwhile.
    pub(super) fn catch_up(&mut self, cx: &mut Context<Self>) {
        let Some(mut link) = self.link.clone() else {
            return;
        };
        self.held = Some(Vec::new());
        self._replay = Some(cx.spawn(async move |pane, cx| {
            let kept = link.replay(cx).await;
            pane.update(cx, |pane, cx| {
                let held = pane.held.take().unwrap_or_default();
                match kept {
                    Ok(bytes) => {
                        pane.grid.feed(&bytes);
                        pane.link = Some(link);
                        pane.reconnected(cx);
                        for update in held {
                            pane.output(update, cx);
                        }
                    }
                    Err(failure) => {
                        pane.call_failed(failure, cx);
                        pane.hand_on_without_reading(held, cx);
                    }
                }
                cx.notify();
            })
            .ok();
        }));
    }

    /// After a failed read: a row past the gap would only read and fail again, so it waits for the next row or state.
    fn hand_on_without_reading(&mut self, held: Vec<Update>, cx: &mut Context<Self>) {
        if self.stopped() {
            return;
        }
        for update in held {
            let stepped = self.link.as_mut().map(|link| link.step(&update));
            if let Some(Ok(Some(output))) = stepped {
                self.handed_on(output, cx);
            }
        }
    }

    /// One update from the shell's rows: its bytes to the grid, or its exit. A state is a new subscription after a
    /// reconnect, so output may have come meanwhile: the kept output is read again. A row past a gap waits for that
    /// read and is handed on after it.
    pub(super) fn output(&mut self, update: Update, cx: &mut Context<Self>) {
        if let Some(held) = &mut self.held {
            // The read on its way covers what a state stands for.
            if !matches!(update, Update::State(_)) {
                held.push(update);
            }
            return;
        }
        let Some(link) = &mut self.link else {
            return;
        };
        if matches!(update, Update::State(_)) {
            if self.shown == Shown::Opening {
                self.shown = Shown::Open;
                self.focus_wanted = true;
            }
            self.catch_up(cx);
            return;
        }
        match link.step(&update) {
            Ok(Some(output)) => self.handed_on(output, cx),
            Ok(None) => cx.notify(),
            Err(()) => {
                self.catch_up(cx);
                self.held.get_or_insert_default().push(update);
            }
        }
    }

    fn handed_on(&mut self, output: Output, cx: &mut Context<Self>) {
        match output {
            Output::Bytes(bytes) => {
                self.grid.feed(&bytes);
                self.reconnected(cx);
            }
            Output::Caught => self.reconnected(cx),
            Output::Exit(code) => self.shown = Shown::Exited(code),
            Output::Unavailable(Availability::ConnectionLost) => self.reconnecting(),
            Output::Unavailable(Availability::TerminalMissing) => self.stop(Shown::Gone),
            Output::Unavailable(Availability::SourceChanged) => {
                self.stop(Shown::Said(SOURCE_CHANGED.into()))
            }
        }
        cx.notify();
    }

    /// A call on the terminal failed: quiet when the connection is down; otherwise the tab stops and says why.
    pub(super) fn call_failed(&mut self, failure: CallFailure, cx: &mut Context<Self>) {
        if connection_down(&failure) {
            self.reconnecting();
            if self.shown == Shown::Reconnecting && self.link.is_none() {
                self.wait_to_reopen(cx);
            }
        } else if failure.code == ErrorCode::TerminalMissing {
            self.stop(Shown::Gone);
        } else {
            self.stop(Shown::Said(failure.message));
        }
        cx.notify();
    }

    /// As the old app: keys that went nowhere stop the terminal, and it says so, unless another state explains it.
    pub(super) fn write_failed(&mut self, mut failure: CallFailure, cx: &mut Context<Self>) {
        let explained = connection_down(&failure)
            || failure.code == ErrorCode::TerminalMissing
            || source_changed(&failure);
        if !explained {
            let why = failure.message;
            failure.message = format!("The terminal didn't take the keys: {why}");
        }
        self.call_failed(failure, cx);
    }

    /// Exited, gone or stopped: only closing the tab changes it.
    fn stopped(&self) -> bool {
        matches!(self.shown, Shown::Exited(_) | Shown::Said(_) | Shown::Gone)
    }

    fn reconnecting(&mut self) {
        if !self.stopped() {
            self.shown = Shown::Reconnecting;
            // Keys caught in the drop are dropped, none queued.
            self.typed.clear();
        }
    }

    /// The connection is back: typing is on again, and the program hears the size it may have missed, once the next
    /// draw has measured the room without the note.
    fn reconnected(&mut self, cx: &mut Context<Self>) {
        if self.shown == Shown::Reconnecting {
            self.shown = Shown::Open;
            self.size_unsent = true;
            cx.notify();
        }
    }

    fn stop(&mut self, shown: Shown) {
        if self.stopped() {
            return;
        }
        self.shown = shown;
        (self._follow, self._replay, self._resize) = (None, None, None);
        (self.held, self.resize_wanted, self.size_unsent) = (None, None, false);
        self.typed.clear();
    }

    /// Tells the program the pane's size, once the call on its way, if any, is answered: only the newest goes.
    pub(super) fn send_size(&mut self, cx: &mut Context<Self>) {
        self.resize_wanted = Some(self.size);
        self.resize(cx);
    }

    fn resize(&mut self, cx: &mut Context<Self>) {
        if self._resize.is_some() {
            return;
        }
        let Some(size) = self.resize_wanted.take() else {
            return;
        };
        // While reconnecting the size waits: it's sent when the connection is back.
        let sendable = matches!(self.shown, Shown::Opening | Shown::Open);
        let Some(link) = self.link.as_ref().filter(|_| sendable) else {
            return;
        };
        // Dropping the answer cancels the call, so the pane holds it.
        let resize = link.resize(size, cx);
        self._resize = Some(cx.spawn(async move |pane, cx| {
            let resized = resize.await;
            pane.update(cx, |pane, cx| {
                pane._resize = None;
                if let Err(failure) = resized {
                    pane.call_failed(failure, cx);
                }
                pane.resize(cx);
            })
            .ok();
        }));
    }

    /// The open failed with the connection down: it's tried again when a push says it can work, or on the timer when
    /// none can come.
    fn wait_to_reopen(&mut self, cx: &mut Context<Self>) {
        let machines = Machines::try_global(cx)
            .map(|machines| cx.observe(&machines, |pane, _, cx| pane.pushed(cx)));
        let socket = SocketClient::try_terminal(cx)
            .map(|client| cx.observe(&client, |pane, _, cx| pane.pushed(cx)));
        self.reopen = machines.into_iter().chain(socket).collect();
        if !self.can_reopen(cx) {
            return;
        }
        let wait = RETRY_WAITS[self.retries.min(RETRY_WAITS.len() - 1)];
        self.retries += 1;
        self._retry = Some(cx.spawn(async move |pane, cx| {
            cx.background_executor().timer(wait).await;
            pane.update(cx, |pane, cx| pane.refresh_open(cx)).ok();
        }));
    }

    /// The machines list or the socket changed while waiting.
    fn pushed(&mut self, cx: &mut Context<Self>) {
        if self.can_reopen(cx) {
            self.refresh_open(cx);
        } else {
            // A push will come, so no timer runs.
            self._retry = None;
        }
    }

    /// Whether the session's machine isn't offline and the terminal socket is connected.
    fn can_reopen(&self, cx: &App) -> bool {
        let connected = SocketClient::try_terminal(cx).is_some_and(|client| {
            matches!(client.read(cx).state(), ConnectionState::Connected { .. })
        });
        connected && !Machines::offline(self.session_machine(cx).as_deref(), cx)
    }

    /// The machine that owns the session: the selection's route, or the sessions list's for a selection without one.
    fn session_machine(&self, cx: &App) -> Option<String> {
        let selected = SelectedSession::global(cx).read(cx);
        (selected
            .source()
            .and_then(|source| source.route_machine.clone()))
        .or_else(|| {
            let session = self.session.as_deref()?;
            let sessions = Sessions::try_global(cx)?;
            let sessions = sessions.read(cx);
            let item = sessions
                .items()
                .iter()
                .find(|item| item.session_id == session)?;
            item.machine.clone()
        })
    }

    /// Tries the open again.
    fn refresh_open(&mut self, cx: &mut Context<Self>) {
        self.reopen.clear();
        (self._retry, self._follow) = (None, None);
        self.shown = Shown::Opening;
        self.open(cx);
        cx.notify();
    }

    /// Whether the note uses the error color.
    pub(super) fn error_note(&self) -> bool {
        matches!(self.shown, Shown::Said(_))
    }

    /// Whether typing and Paste are on.
    pub(super) fn typing(&self) -> bool {
        self.shown == Shown::Open
    }

    /// The line under the grid, or in its place.
    pub(super) fn status(&self) -> Option<String> {
        match &self.shown {
            Shown::Opening => Some("Opening terminal…".into()),
            Shown::Reconnecting => Some("Reconnecting…".into()),
            Shown::Exited(code) => Some(format!("exited with {code}")),
            Shown::Gone => Some(GONE.into()),
            Shown::Said(why) => Some(why.clone()),
            Shown::Nothing | Shown::Open => None,
        }
    }
}
