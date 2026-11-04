//! The sessions pane's actions: opening the new-session form (empty, or filled in for a project) and moving a
//! session to another group with `sessions/mark`.

use gpui_kit::*;
use serde_json::{Value, json};

use super::new_session_form::{Created, NewSessionForm};
use super::sessions_pane::SessionsPane;
use super::status::Status;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;

impl SessionsPane {
    /// Escape in the filter, or a pick: the filter empties and the rail reads without it.
    pub(super) fn clear_filter(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.filter
            .update(cx, |input, cx| input.set_value("", window, cx));
        let read = self.rail.set_filter("");
        self.send(read, cx);
    }

    /// Escape edits the query once; a second Escape returns keyboard control to the list.
    pub(super) fn escape_filter(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.filter.read(cx).value().is_empty() {
            let focus = self.focus_handle(cx);
            window.focus(&focus, cx);
        } else {
            self.clear_filter(window, cx);
        }
    }

    /// The header's button: opens the form, or closes it while it's open.
    pub(super) fn toggle_form(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.creating.take().is_none() {
            self.open_form(window, cx);
        }
        cx.notify();
    }

    /// A project header's New session: the form, open with the project's folder and machine.
    pub(super) fn new_in(
        &mut self,
        folder: &str,
        machine: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let form = match &self.creating {
            Some((form, _)) => form.clone(),
            None => self.open_form(window, cx),
        };
        form.update(cx, |form, cx| form.fill(folder, machine, window, cx));
        cx.notify();
    }

    pub(super) fn open_form(
        &mut self,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Entity<NewSessionForm> {
        let form = cx.new(|cx| NewSessionForm::new(window, cx));
        let made = cx.subscribe(&form, |pane, _, Created, cx| {
            pane.creating = None;
            cx.notify();
        });
        self.creating = Some((form.clone(), made));
        form
    }

    /// Moves a session to a group. The backend stamps it and sends the changed item on the sessions topic. A mark
    /// is a save, so it's never keyed and a later one doesn't cancel it: each runs to its answer.
    pub(super) fn mark(&mut self, session_id: String, to: Status, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let args = json!({ "session": session_id, "state": to.wire() });
        let call = socket
            .read(cx)
            .call::<Value>("sessions", "mark", args, None);
        self.row_error = None;
        self.next_mark += 1;
        let id = self.next_mark;
        let task = cx.spawn(async move |pane, cx| {
            let outcome = call.await;
            pane.update(cx, |pane, cx| {
                pane.marking.remove(&id);
                if let Outcome::Failed(failure) = outcome {
                    pane.row_error =
                        Some(format!("Couldn't move the session: {}", failure.message).into());
                }
                cx.notify();
            })
            .ok();
        });
        self.marking.insert(id, task);
        cx.notify();
    }
}
