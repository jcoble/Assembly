//! `Sessions` owns the sessions list: the `sessions` topic's newest 200, newest first (`updatedAt`, then id, as the
//! backend orders them), each change upserted in place, and older pages from `sessions/page`. The page cursor is kept
//! apart from the list: it starts at the oldest of the topic's first state and moves only with each page's
//! `nextBefore`, so an item that changes without moving (a rename) can't skip the sessions past it. A reconnect's
//! state that doesn't reach the newest held session (more than 200 changed meanwhile) replaces the list and starts
//! the cursor again from its oldest. The backend keeps helper sessions off the topic and the pages. Each item that arrives or
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
    /// Where the next older page starts, (`before`, `beforeId`); `None` when there's nothing older.
    older: Option<(i64, String)>,
    /// The topic's first state has set `older`; a reconnect's state moves it only past a gap.
    seeded: bool,
    /// Owns the `sessions` subscription; dropping it unsubscribes.
    _feed: Task<()>,
    loading_older: Option<Task<()>>,
    /// Why the last older page failed, until a page loads or a retry starts.
    older_error: Option<String>,
}

impl EventEmitter<SessionChange> for Sessions {}

/// Set once the socket client has started; observing it tells a view the list is there.
pub(crate) struct GlobalSessions(Entity<Sessions>);

impl Global for GlobalSessions {}

impl Sessions {
    /// Starts the window's sessions list, once the socket client has started.
    pub fn start(cx: &mut App) {
        let state = cx.new(Sessions::new);
        cx.set_global(GlobalSessions(state));
    }

    /// The sessions list, once the socket client has started.
    pub fn try_global(cx: &App) -> Option<Entity<Self>> {
        cx.try_global::<GlobalSessions>()
            .map(|state| state.0.clone())
    }

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
            older: None,
            seeded: false,
            _feed: feed,
            loading_older: None,
            older_error: None,
        }
    }

    pub fn items(&self) -> &[SessionItem] {
        &self.items
    }

    /// Why the last older page failed, until a page loads or a retry starts.
    pub fn older_error(&self) -> Option<&str> {
        self.older_error.as_deref()
    }

    /// Loads the next older page, once at a time; nothing when there are no more.
    pub fn load_older(&mut self, cx: &mut Context<Self>) {
        let Some((before, before_id)) = &self.older else {
            return;
        };
        if self.loading_older.is_some() {
            return;
        }
        self.older_error = None;
        let args = json!({"before": before, "beforeId": before_id, "limit": PAGE});
        let page = SocketClient::global(cx)
            .read(cx)
            .call::<SessionPage>(TOPIC, "page", args, None);
        self.loading_older = Some(cx.spawn(async move |this, cx| {
            let outcome = page.await;
            let _ = this.update(cx, |sessions, cx| {
                sessions.loading_older = None;
                for change in sessions.finish_older(outcome) {
                    cx.emit(change);
                }
                cx.notify();
            });
        }));
        cx.notify();
    }

    /// Takes an older page's outcome: a page moves the cursor and is added, a failure keeps its message, and an
    /// aborted call changes nothing.
    fn finish_older(&mut self, outcome: Outcome<SessionPage>) -> Vec<SessionChange> {
        match outcome {
            Outcome::Ok(page) => {
                self.older_error = None;
                self.older = page.next_before.zip(page.next_before_id);
                add_page(&mut self.items, page.sessions)
            }
            Outcome::Failed(failure) => {
                self.older_error =
                    Some(format!("Couldn't load older sessions: {}", failure.message));
                Vec::new()
            }
            Outcome::Aborted => Vec::new(),
        }
    }

    fn apply(&mut self, update: Update, cx: &mut Context<Self>) {
        match update {
            Update::State(items) => {
                let first = !self.seeded;
                self.seeded = true;
                let full = items.len() >= NEWEST;
                let state = items
                    .into_iter()
                    .filter_map(|(_, item)| read(item))
                    .collect();
                for change in add_state(
                    &mut self.items,
                    &mut self.older,
                    &mut self.older_error,
                    first,
                    state,
                    full,
                ) {
                    cx.emit(change);
                }
            }
            Update::Change { item, .. } => {
                if let Some(item) = read(item) {
                    cx.emit(upsert(&mut self.items, item));
                }
            }
        }
        cx.notify();
    }
}

fn read(item: Value) -> Option<SessionItem> {
    serde_json::from_value(item)
        .inspect_err(|error| eprintln!("sessions: an item that doesn't read: {error}"))
        .ok()
}

/// Adds the topic's state (`full` when it holds the topic's 200). The first sets where older pages start. A later
/// one, after a reconnect, whose oldest is newer than the newest held session leaves a gap: the held list is
/// replaced and the pages start again from the state's oldest, without an older page's error. One that reaches
/// the held list is merged.
fn add_state(
    items: &mut Vec<SessionItem>,
    older: &mut Option<(i64, String)>,
    older_error: &mut Option<String>,
    first: bool,
    state: Vec<SessionItem>,
    full: bool,
) -> Vec<SessionChange> {
    let key = |item: &SessionItem| (item.updated_at, item.session_id.clone());
    let oldest = state.iter().map(key).min();
    let gap = !first
        && matches!((&oldest, items.first()), (Some(oldest), Some(newest)) if *oldest > key(newest));
    if gap {
        items.clear();
    }
    if first || gap {
        *older = oldest.filter(|_| full);
        *older_error = None;
    }
    state.into_iter().map(|item| upsert(items, item)).collect()
}

/// Adds a page's sessions. One already held is skipped: the held copy came from the topic, whose changes are sent
/// inside their write, so it's at least as new as the page, which may have been read before that write.
fn add_page(items: &mut Vec<SessionItem>, page: Vec<SessionItem>) -> Vec<SessionChange> {
    let held = |item: &SessionItem| items.iter().any(|held| held.session_id == item.session_id);
    let fresh: Vec<SessionItem> = page.into_iter().filter(|item| !held(item)).collect();
    fresh.into_iter().map(|item| upsert(items, item)).collect()
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
#[path = "sessions_load_tests.rs"]
mod load_tests;

#[cfg(test)]
mod tests {
    use gpui_kit::Task;
    use serde_json::json;

    use super::{SessionChange, SessionItem, SessionPage, Sessions, add_page, add_state, upsert};
    use crate::core::socket::call_failure::CallFailure;
    use crate::core::socket::error_code::ErrorCode;
    use crate::core::socket::outcome::Outcome;

    fn session(id: &str, updated_at: i64) -> SessionItem {
        serde_json::from_value(json!({"sessionId": id, "agent": "claude", "repoUrl": "/r", "title": null,
            "machine": null, "parentSessionId": null, "acpSessionId": null, "updatedAt": updated_at, "summary": {}}))
        .expect("a session item")
    }

    fn ids(items: &[SessionItem]) -> Vec<&str> {
        items.iter().map(|item| item.session_id.as_str()).collect()
    }

    fn held(items: Vec<SessionItem>, older: (i64, &str)) -> Sessions {
        Sessions {
            items,
            older: Some((older.0, older.1.to_owned())),
            seeded: true,
            _feed: Task::ready(()),
            loading_older: None,
            older_error: None,
        }
    }

    fn failed(message: &str) -> Outcome<SessionPage> {
        Outcome::Failed(CallFailure {
            code: ErrorCode::Failed,
            message: message.to_owned(),
        })
    }

    fn page(sessions: Vec<SessionItem>, next: Option<(i64, &str)>) -> Outcome<SessionPage> {
        Outcome::Ok(SessionPage {
            sessions,
            next_before: next.map(|next| next.0),
            next_before_id: next.map(|next| next.1.to_owned()),
        })
    }

    #[test]
    fn a_failed_older_page_keeps_its_message_and_changes_nothing_else() {
        let mut sessions = held(vec![session("a", 10)], (10, "a"));
        let changes = sessions.finish_older(failed("the database is locked"));
        assert!(changes.is_empty());
        assert!(
            sessions
                .older_error()
                .is_some_and(|error| error.contains("the database is locked"))
        );
        assert_eq!(ids(&sessions.items), ["a"]);
        assert_eq!(sessions.older, Some((10, "a".to_owned())));
    }

    #[test]
    fn a_later_older_page_clears_the_error_and_adds_the_page() {
        let mut sessions = held(vec![session("a", 10)], (10, "a"));
        sessions.finish_older(failed("the database is locked"));
        let changes = sessions.finish_older(page(vec![session("b", 5)], Some((5, "b"))));
        assert_eq!(changes, [SessionChange::Added("b".into())]);
        assert_eq!(sessions.older_error(), None);
        assert_eq!(ids(&sessions.items), ["a", "b"]);
        assert_eq!(sessions.older, Some((5, "b".to_owned())));
    }

    #[test]
    fn an_aborted_older_page_changes_nothing() {
        let mut sessions = held(vec![session("a", 10)], (10, "a"));
        let changes = sessions.finish_older(Outcome::Aborted);
        assert!(changes.is_empty());
        assert_eq!(sessions.older_error(), None);
        assert_eq!(ids(&sessions.items), ["a"]);
        assert_eq!(sessions.older, Some((10, "a".to_owned())));
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

    // More than the topic's 200 changed while disconnected: the reconnect's state doesn't reach the newest held
    // session, so the list has a gap. It's replaced, and older pages start again from the state's oldest.
    // Pins: g0-socket#7, g0-socket#10
    #[test]
    fn a_reconnect_state_past_the_held_list_replaces_it_and_restarts_the_pages() {
        let mut items = vec![session("a", 10), session("b", 5)];
        let mut older = Some((5, "b".to_owned()));
        let state = vec![session("x", 30), session("y", 20)];
        add_state(&mut items, &mut older, &mut None, false, state, true);
        assert_eq!(ids(&items), ["x", "y"]);
        assert_eq!(older, Some((20, "y".to_owned())));
    }

    // An older page failed before the reconnect. The gap restarts the pages, so that page's error no longer applies.
    #[test]
    fn a_reconnect_state_past_the_held_list_clears_the_older_page_error() {
        let mut items = vec![session("a", 10), session("b", 5)];
        let mut older = Some((5, "b".to_owned()));
        let mut older_error =
            Some("Couldn't load older sessions: the database is locked".to_owned());
        let state = vec![session("x", 30), session("y", 20)];
        add_state(&mut items, &mut older, &mut older_error, false, state, true);
        assert_eq!(older_error, None);
    }

    // Pins: g0-socket#7, g0-socket#10
    #[test]
    fn a_reconnect_state_that_reaches_the_held_list_is_merged_and_keeps_the_pages() {
        let mut items = vec![session("a", 10), session("b", 5)];
        let mut older = Some((1, "z".to_owned()));
        let state = vec![session("x", 30), session("a", 10)];
        add_state(&mut items, &mut older, &mut None, false, state, true);
        assert_eq!(ids(&items), ["x", "a", "b"]);
        assert_eq!(older, Some((1, "z".to_owned())));
    }

    // Pins: g0-socket#10
    #[test]
    fn a_page_never_replaces_a_session_already_held() {
        let mut items = vec![session("a", 50), session("b", 10)];
        let changes = add_page(&mut items, vec![session("b", 5), session("c", 4)]);
        assert_eq!(changes, [SessionChange::Added("c".into())]);
        assert_eq!(ids(&items), ["a", "b", "c"]);
        assert_eq!(items[1].updated_at, 10);
    }
}
