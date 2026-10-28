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

    /// Moves a session to a group. The backend stamps it and sends the changed item on the sessions topic.
    pub(super) fn mark(&mut self, session_id: String, to: Status, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let key = format!("sessions/mark:{session_id}");
        let args = json!({ "session": session_id, "state": to.wire() });
        let call = socket
            .read(cx)
            .call::<Value>("sessions", "mark", args, Some(&key));
        self.mark_error = None;
        let id = session_id.clone();
        let task = cx.spawn(async move |pane, cx| {
            let outcome = call.await;
            pane.update(cx, |pane, cx| {
                pane.marking.remove(&id);
                if let Outcome::Failed(failure) = outcome {
                    pane.mark_error =
                        Some(format!("Couldn't move the session: {}", failure.message).into());
                }
                cx.notify();
            })
            .ok();
        });
        self.marking.insert(session_id, task);
        cx.notify();
    }
}
