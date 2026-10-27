//! `Sessions` owns the sessions list: the `sessions` topic's newest 200, newest first (`updatedAt`, then id, as the
//! backend orders them), each change upserted in place, and older pages from `sessions/page` with the oldest item's
//! (`before`, `beforeId`). The backend keeps helper sessions off the topic and the pages. Each item that arrives or
//! changes is emitted as a `SessionChange`, so the list can animate it.

use gpui_kit::*;
use serde_json::{Value, json};

use super::session_change::SessionChange;
use super::session_item::SessionItem;
use super::session_page::SessionPage;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::update::Update;

const TOPIC: &str = "sessions";
/// What the topic holds (`SessionsFeature.ListLimit`): fewer means there's nothing older.
const NEWEST: usize = 200;
const PAGE: usize = 50;

pub struct Sessions {
    /// Newest first.
    items: Vec<SessionItem>,
    /// Older sessions may exist past the last item.
    more: bool,
    /// Owns the `sessions` subscription; dropping it unsubscribes.
    _feed: Task<()>,
    loading_older: Option<Task<()>>,
}

impl EventEmitter<SessionChange> for Sessions {}

impl Sessions {
    /// Subscribes to the `sessions` topic.
    pub fn new(cx: &mut Context<Self>) -> Self {
        let mut subscription = SocketClient::global(cx).read(cx).subscribe(TOPIC, None);
        let feed = cx.spawn(async move |this, cx| {
            while let Ok(update) = subscription.updates.recv().await {
                if this
                    .update(cx, |sessions, cx| sessions.apply(update, cx))
                    .is_err()
                {
                    return;
                }
            }
        });
        Self {
            items: Vec::new(),
            more: false,
            _feed: feed,
            loading_older: None,
        }
    }

    pub fn items(&self) -> &[SessionItem] {
        &self.items
    }

    /// Loads the next older page, once at a time; nothing when there are no more.
    pub fn load_older(&mut self, cx: &mut Context<Self>) {
        let Some(oldest) = self.items.last() else {
            return;
        };
        if !self.more || self.loading_older.is_some() {
            return;
        }
        let args =
            json!({"before": oldest.updated_at, "beforeId": oldest.session_id, "limit": PAGE});
        let page = SocketClient::global(cx)
            .read(cx)
            .call::<SessionPage>(TOPIC, "page", args, None);
        self.loading_older = Some(cx.spawn(async move |this, cx| {
            let outcome = page.await;
            let _ = this.update(cx, |sessions, cx| {
                sessions.loading_older = None;
                if let Outcome::Ok(page) = outcome {
                    sessions.more = page.next_before.is_some();
                    page.sessions
                        .into_iter()
                        .for_each(|item| sessions.add(item, cx));
                    cx.notify();
                }
            });
        }));
    }

    fn apply(&mut self, update: Update, cx: &mut Context<Self>) {
        match update {
            Update::State(items) => {
                self.more = items.len() >= NEWEST;
                items.into_iter().for_each(|(_, item)| self.read(item, cx));
            }
            Update::Change { item, .. } => self.read(item, cx),
        }
        cx.notify();
    }

    fn read(&mut self, item: Value, cx: &mut Context<Self>) {
        match serde_json::from_value(item) {
            Ok(item) => self.add(item, cx),
            Err(error) => eprintln!("sessions: an item that doesn't read: {error}"),
        }
    }

    fn add(&mut self, item: SessionItem, cx: &mut Context<Self>) {
        cx.emit(upsert(&mut self.items, item));
    }
}

/// Puts `item` in its place in the newest-first list, replacing the one with its id.
fn upsert(items: &mut Vec<SessionItem>, item: SessionItem) -> SessionChange {
    let held = items
        .iter()
        .position(|held| held.session_id == item.session_id);
    if let Some(index) = held {
        items.remove(index);
    }
    let id = item.session_id.clone();
    let newer = |held: &SessionItem| {
        (held.updated_at, &held.session_id) > (item.updated_at, &item.session_id)
    };
    let at = items.partition_point(newer);
    items.insert(at, item);
    if held.is_some() {
        SessionChange::Updated(id)
    } else {
        SessionChange::Added(id)
    }
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::{SessionChange, SessionItem, upsert};

    fn session(id: &str, updated_at: i64) -> SessionItem {
        serde_json::from_value(json!({"sessionId": id, "agent": "claude", "repoUrl": "/r", "title": null,
            "machine": null, "parentSessionId": null, "acpSessionId": null, "updatedAt": updated_at, "summary": {}}))
        .expect("a session item")
    }

    fn ids(items: &[SessionItem]) -> Vec<&str> {
        items.iter().map(|item| item.session_id.as_str()).collect()
    }

    // Pins: g0-socket#11
    #[test]
    fn a_new_or_changed_session_goes_to_its_place_newest_first() {
        let mut items = vec![];
        assert_eq!(
            upsert(&mut items, session("a", 10)),
            SessionChange::Added("a".into())
        );
        upsert(&mut items, session("b", 30));
        upsert(&mut items, session("c", 20));
        assert_eq!(ids(&items), ["b", "c", "a"]);
        assert_eq!(
            upsert(&mut items, session("a", 40)),
            SessionChange::Updated("a".into())
        );
        assert_eq!(ids(&items), ["a", "b", "c"]);
        // A tie on time sorts by id, as the backend's pages do.
        upsert(&mut items, session("d", 20));
        assert_eq!(ids(&items), ["a", "b", "d", "c"]);
    }
}
