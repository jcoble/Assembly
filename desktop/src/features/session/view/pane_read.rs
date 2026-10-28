//! While a session is open, the pane tells the backend the newest row it has shown (`sessions/read`), which clears the
//! session's unread dot in the sessions list: once its first page lands, and again when a turn ends while it stays
//! open. Not while a turn runs: unread only changes at a turn's end, and a read per streamed row would be a write and a
//! sessions update each time. A `Jump` (a reconnect) tells it again, in case a read was lost with the connection.

use serde_json::json;

use gpui_kit::*;

use super::SessionPane;

impl SessionPane {
    /// `running`: a turn is going in the open session.
    pub(super) fn tell_read(&mut self, running: bool, cx: &mut Context<Self>) {
        let Some(session) = self.session.clone() else {
            return;
        };
        if running || self.view.newest <= self.read_to {
            return;
        }
        self.read_to = self.view.newest;
        let args = json!({ "session": session, "seq": self.read_to });
        let call = self.client.call("sessions", "read", args, None, cx);
        // Each read runs to its answer (dropping one would cancel it) and, like the other writes, goes on when the
        // reader switches away; it lets go of itself once answered.
        let key = format!("read {session} {}", self.read_to);
        let task = cx.spawn({
            let key = key.clone();
            async move |pane, cx| {
                _ = call.await;
                pane.update(cx, |pane, _| pane._writes.remove(&key)).ok();
            }
        });
        self._writes.insert(key, task);
    }
}
