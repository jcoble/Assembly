//! While a session is open, the pane tells the backend the newest row it has shown (`sessions/read`), which clears the
//! session's unread dot in the sessions list: once its first page lands and again as rows arrive while it stays open.
//! A `Jump` (a reconnect) tells it again, in case a read was lost with the connection.

use serde_json::json;

use gpui_kit::*;

use super::SessionPane;

impl SessionPane {
    pub(super) fn tell_read(&mut self, cx: &mut Context<Self>) {
        let Some(session) = self.session.clone() else {
            return;
        };
        if self.view.newest <= self.read_to {
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
