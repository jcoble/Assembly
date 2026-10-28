//! While a session is open, the pane tells the backend the newest row it has shown (`sessions/read`), which clears the
//! session's unread dot in the sessions list: once its first page lands and again as rows arrive while it stays open.

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
        // A newer read replaces the older; like the other writes, it goes on when the reader switches away.
        let task = cx.spawn(async move |_, _| _ = call.await);
        self._writes.insert(format!("read {session}"), task);
    }
}
