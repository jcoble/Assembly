//! Settled's pages (sessions-list#9): Settled is the archive, so none of its sessions load until it's opened; then
//! `sessions/page {group: "settled"}`, newest first, each page from the last one's cursor. `load_next` is what the
//! list asks for as its end comes into view: the open sessions' next page while there is one, then Settled's.

use gpui_kit::*;
use serde_json::json;

use super::{PAGE, Sessions, TOPIC, add_page};
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::session_change::SessionChange;
use crate::core::state::session_page::SessionPage;

impl Sessions {
    /// Loads the open sessions' next page while there is one; after that, with Settled open, Settled's next.
    pub fn load_next(&mut self, settled_open: bool, cx: &mut Context<Self>) {
        if self.older.is_some() {
            self.load_older(cx);
        } else if settled_open {
            self.load_settled(cx);
        }
    }

    /// Loads Settled's next page, once at a time; nothing once its last has loaded.
    fn load_settled(&mut self, cx: &mut Context<Self>) {
        if self.loading_settled.is_some() || (self.settled_started && self.settled_next.is_none()) {
            return;
        }
        self.older_error = None;
        let mut args = json!({"limit": PAGE, "group": "settled"});
        if let Some((before, before_id)) = &self.settled_next {
            args["before"] = json!(before);
            args["beforeId"] = json!(before_id);
        }
        let page = SocketClient::global(cx)
            .read(cx)
            .call::<SessionPage>(TOPIC, "page", args, None);
        self.loading_settled = Some(cx.spawn(async move |this, cx| {
            let outcome = page.await;
            let _ = this.update(cx, |sessions, cx| {
                sessions.loading_settled = None;
                for change in sessions.finish_settled(outcome) {
                    cx.emit(change);
                }
                cx.notify();
            });
        }));
        cx.notify();
    }

    /// Takes a Settled page's outcome, as `finish_older` does an open one's.
    pub(super) fn finish_settled(&mut self, outcome: Outcome<SessionPage>) -> Vec<SessionChange> {
        match outcome {
            Outcome::Ok(page) => {
                self.older_error = None;
                self.settled_started = true;
                self.settled_next = page.next_before.zip(page.next_before_id);
                add_page(&mut self.items, page.sessions)
            }
            Outcome::Failed(failure) => {
                self.older_error = Some(format!(
                    "Couldn't load settled sessions: {}",
                    failure.message
                ));
                Vec::new()
            }
            Outcome::Aborted => Vec::new(),
        }
    }
}
