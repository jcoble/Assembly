//! `Live` owns the shown session's terminal in the window: its screen, which bytes it has, and the calls and the
//! subscription that feed it. The pane drops it on every session switch (terminal#5), which unsubscribes and stops
//! its calls; the backend keeps the terminal running until the session is shown again. Its socket side, as
//! WorkspaceTerminal.svelte and terminalService.ts did it:
//! - `terminal/start` with the session as owner, which answers its running shell or starts one in its folder
//!   (terminal#1, terminal#9), then the size, then the `terminal` topic from the start;
//! - a jump reads the replay with `terminal/read`, a page at a time (`Output`);
//! - one `terminal/write` in flight, with what's typed meanwhile sent together in the next (terminal-shape.md);
//! - the exit stops input and keeps the output readable (terminal#7).

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;

use super::output::{Output, OutputPiece, OutputRow};
use super::screen::{Screen, Size};
use super::terminal_pane::TerminalPane;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::row_update::RowUpdate;

/// The most one `terminal/write` sends.
const WRITE: usize = 1024 * 1024;

/// `terminal/start`'s answer, as much of it as the window uses.
#[derive(Deserialize)]
struct Started {
    id: String,
    cols: u16,
    rows: u16,
}

pub struct Live {
    pub session: String,
    pub screen: Screen,
    pub output: Output,
    /// Why the terminal can't be shown or used, in words for the pane.
    pub error: Option<String>,
    terminal: Option<String>,
    /// The size the backend's terminal has, as last sent or answered.
    sent: Option<Size>,
    writing: bool,
    queued: Vec<u8>,
    _start: Option<Task<()>>,
    _rows: Option<Task<()>>,
    _read: Option<Task<()>>,
    _write: Option<Task<()>>,
    /// Waits for the size to settle, then sends it.
    _settle: Option<Task<()>>,
    _resize: Option<Task<()>>,
}

impl Live {
    fn new(session: String, size: Size) -> Self {
        Self {
            session,
            screen: Screen::new(size),
            output: Output::default(),
            error: None,
            terminal: None,
            sent: None,
            writing: false,
            queued: Vec::new(),
            _start: None,
            _rows: None,
            _read: None,
            _write: None,
            _settle: None,
            _resize: None,
        }
    }
}

impl TerminalPane {
    /// Opens `session`'s terminal at `size`: starts it (or finds it running), then subscribes to its output.
    pub(super) fn open(&mut self, session: String, size: Size, cx: &mut Context<Self>) {
        let mut live = Live::new(session.clone(), size);
        let Some(socket) = SocketClient::try_global(cx) else {
            live.error = Some("The backend isn't running.".into());
            self.live = Some(live);
            return;
        };
        let args = json!({"owner": session, "cols": size.cols, "rows": size.rows});
        let start = socket
            .read(cx)
            .call::<Started>("terminal", "start", args, None);
        live._start = Some(cx.spawn(async move |pane, cx| {
            let outcome = start.await;
            _ = pane.update(cx, |pane, cx| pane.started(outcome, cx));
        }));
        self.live = Some(live);
    }

    fn started(&mut self, outcome: Outcome<Started>, cx: &mut Context<Self>) {
        let Some(live) = &mut self.live else { return };
        let started = match outcome {
            Outcome::Ok(started) => started,
            Outcome::Failed(failure) => return self.fail(failure.message, cx),
            Outcome::Aborted => {
                return self.fail(
                    "The connection dropped. Show the terminal again to retry.".into(),
                    cx,
                );
            }
        };
        live.sent = Some(Size {
            cols: started.cols,
            rows: started.rows,
        });
        live.terminal = Some(started.id.clone());
        // A running terminal answers with its own size: it takes this view's before its output is read.
        self.send_size(cx);
        // What was typed while it started.
        self.flush(cx);
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let mut rows = socket
            .read(cx)
            .subscribe_seq::<OutputRow>("terminal", &started.id, None);
        let Some(live) = &mut self.live else { return };
        // The task owns the subscription, so dropping it unsubscribes.
        live._rows = Some(cx.spawn(async move |pane, cx| {
            while let Ok(update) = rows.updates.recv().await {
                if pane.update(cx, |pane, cx| pane.output(update, cx)).is_err() {
                    return;
                }
            }
        }));
    }

    fn output(&mut self, update: RowUpdate<OutputRow>, cx: &mut Context<Self>) {
        let Some(live) = &mut self.live else { return };
        match update {
            RowUpdate::Row(row) => {
                let bytes = live.output.row(row);
                self.show(bytes, true, cx);
            }
            RowUpdate::Jump { seq } => {
                if let Some(from) = live.output.jump(seq) {
                    self.read(from, cx);
                }
            }
        }
    }

    /// Reads one replay page from `from`, and the next until the replay is done. A failed read ends the replay with
    /// what the screen has.
    fn read(&mut self, from: i64, cx: &mut Context<Self>) {
        let (Some(live), Some(socket)) = (&mut self.live, SocketClient::try_global(cx)) else {
            return;
        };
        let Some(terminal) = live.terminal.clone() else {
            return;
        };
        let read = socket.read(cx).call::<OutputPiece>(
            "terminal",
            "read",
            json!({"terminal": terminal, "offset": from}),
            None,
        );
        live._read = Some(cx.spawn(async move |pane, cx| {
            let piece = match read.await {
                Outcome::Ok(piece) => piece,
                // The subscription carries on past the replay after a reconnect, so the rest would never come.
                Outcome::Aborted => {
                    let message = "The connection dropped. Show the terminal again to retry.";
                    _ = pane.update(cx, |pane, cx| pane.fail(message.into(), cx));
                    return;
                }
                Outcome::Failed(_) => OutputPiece {
                    offset: from,
                    data: String::new(),
                },
            };
            _ = pane.update(cx, |pane, cx| {
                let Some(live) = &mut pane.live else { return };
                let (replay, held, next) = live.output.page(piece);
                // Old output's questions were answered when they were asked; the rows held meanwhile are new.
                pane.show(replay, false, cx);
                pane.show(held, true, cx);
                if let Some(next) = next {
                    pane.read(next, cx);
                }
            });
        }));
    }

    /// Draws new output, and with `answer` writes back what it asked the terminal to answer.
    fn show(&mut self, bytes: Vec<u8>, answer: bool, cx: &mut Context<Self>) {
        let Some(live) = &mut self.live else { return };
        if !bytes.is_empty() {
            live.screen.feed(&bytes);
            let replies = live.screen.replies();
            if answer && !replies.is_empty() {
                self.write(replies, cx);
            }
        }
        cx.notify();
    }

    /// Sends what's typed, unless the terminal has ended: one write at a time, the rest queued for the next (or
    /// until the terminal has started).
    pub(super) fn write(&mut self, bytes: Vec<u8>, cx: &mut Context<Self>) {
        let Some(live) = &mut self.live else { return };
        if live.output.exit().is_some() {
            return;
        }
        live.queued.extend(bytes);
        if !live.writing {
            self.flush(cx);
        }
    }

    fn flush(&mut self, cx: &mut Context<Self>) {
        let (Some(live), Some(socket)) = (&mut self.live, SocketClient::try_global(cx)) else {
            return;
        };
        let Some(terminal) = live.terminal.clone() else {
            return;
        };
        if live.queued.is_empty() {
            return;
        }
        // A big paste goes a part at a time, under the backend's 4 MB frame.
        let part = live.queued.len().min(WRITE);
        let data = STANDARD.encode(live.queued.drain(..part).as_slice());
        let write = socket.read(cx).call::<serde_json::Value>(
            "terminal",
            "write",
            json!({"terminal": terminal, "data": data}),
            None,
        );
        live.writing = true;
        live._write = Some(cx.spawn(async move |pane, cx| {
            let outcome = write.await;
            _ = pane.update(cx, |pane, cx| {
                let Some(live) = &mut pane.live else { return };
                live.writing = false;
                // Ended meanwhile: the exit row says so, and input stops then.
                if let Outcome::Failed(failure) = outcome {
                    eprintln!("terminal: a write failed: {}", failure.message);
                }
                pane.flush(cx);
            });
        }));
    }

    /// The pane's size changed: the screen follows at once, and the backend's terminal once the size settles (a dock
    /// drag resizes on every move).
    pub(super) fn resized(&mut self, size: Size, cx: &mut Context<Self>) {
        let Some(live) = &mut self.live else { return };
        if live.screen.size() != size {
            live.screen.resize(size);
            cx.notify();
        }
        live._settle = Some(cx.spawn(async move |pane, cx| {
            cx.background_executor()
                .timer(std::time::Duration::from_millis(120))
                .await;
            _ = pane.update(cx, |pane, cx| pane.send_size(cx));
        }));
    }

    /// Sends the screen's size, unless the backend's terminal already has it.
    fn send_size(&mut self, cx: &mut Context<Self>) {
        let (Some(live), Some(socket)) = (&mut self.live, SocketClient::try_global(cx)) else {
            return;
        };
        let (Some(terminal), size) = (live.terminal.clone(), live.screen.size()) else {
            return;
        };
        if live.sent == Some(size) || live.output.exit().is_some() {
            return;
        }
        live.sent = Some(size);
        let args = json!({"terminal": terminal, "cols": size.cols, "rows": size.rows});
        let resize = socket
            .read(cx)
            .call::<serde_json::Value>("terminal", "resize", args, None);
        live._resize = Some(cx.spawn(async move |_, _| _ = resize.await));
    }

    fn fail(&mut self, message: String, cx: &mut Context<Self>) {
        if let Some(live) = &mut self.live {
            live.error = Some(message);
        }
        cx.notify();
    }
}
