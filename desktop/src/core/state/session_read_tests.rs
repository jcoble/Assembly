//! When a tab's read of the selected session runs (scm#4), and what it shows with no session or a git failure
//! (scm#6), on the socket client against `FakeBackend`. The answers are `worktrees/list`'s shape.

use std::sync::{Arc, Mutex};

use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::TestAppContext;
use gpui_kit::{AppContext as _, Entity};
use serde_json::{Value, json};

use super::{SessionRead, Shown};
use crate::core::socket::checkout_selector::CheckoutSelector;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::state::selected_session::SelectedSession;

const GIT_SAYS: &str = "fatal: not a git repository (or any of the parent directories): .git";

/// What the backend saw: each `worktrees/list` call's session and id, and each cancel's id.
#[derive(Default)]
struct Seen {
    reads: Vec<(String, Value)>,
    picked: Vec<Value>,
    cancels: Vec<Value>,
    session_subscription: Value,
}

impl Seen {
    fn sessions(&self) -> Vec<String> {
        self.reads
            .iter()
            .map(|(session, _)| session.clone())
            .collect()
    }
}

/// Answers a list for any session but `slow`, which it never answers, `bad`, which fails as git does, and `dropped`,
/// whose read drops the connection.
fn backend(seen: Arc<Mutex<Seen>>, heard: UnboundedSender<()>) -> FakeBackend {
    FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let id = frame["id"].clone();
        let mut seen = seen.lock().unwrap();
        let replies = match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("sub"), _) if frame["topic"] == "sessions" => {
                seen.session_subscription = id.clone();
                vec![Reply::Frame(
                    json!({"t":"state","sub":id,"items":[["s1",session_item("s1","idle")]]}),
                )]
            }
            (Some("call"), Some("test_state")) => {
                let item = session_item(
                    frame["args"]["session"].as_str().unwrap(),
                    frame["args"]["state"].as_str().unwrap(),
                );
                vec![
                    Reply::Frame(
                        json!({"t":"change","sub":seen.session_subscription,"key":frame["args"]["session"],"item":item}),
                    ),
                    Reply::Frame(json!({"t":"result","id":id,"result":{}})),
                ]
            }
            (Some("call"), Some("test_barrier")) => {
                vec![Reply::Frame(json!({"t":"result","id":id,"result":{}}))]
            }
            (Some("call"), Some("list")) => {
                let session = frame["args"]["session"]
                    .as_str()
                    .unwrap_or_default()
                    .to_string();
                seen.reads.push((session.clone(), id.clone()));
                seen.picked.push(frame["args"]["checkoutPath"].clone());
                match session.as_str() {
                    "slow" => Vec::new(),
                    "dropped" => vec![Reply::Drop],
                    "bad" => vec![Reply::Frame(
                        json!({"t": "error", "id": id, "code": "failed", "message": GIT_SAYS}),
                    )],
                    _ => vec![Reply::Frame(
                        json!({"t": "result", "id": id, "result": listed(&session)}),
                    )],
                }
            }
            (Some("cancel"), _) => {
                seen.cancels.push(id);
                Vec::new()
            }
            _ => Vec::new(),
        };
        drop(seen);
        _ = heard.unbounded_send(());
        replies
    })
}

fn listed(session: &str) -> Value {
    json!({ "worktrees": [{ "path": format!("/r/{session}"), "branch": "main", "head": "0123456789abcdef",
        "isMain": true, "isBare": false, "isDirty": false, "unpushed": 0, "locked": false, "prunable": false,
        "headSubject": "first", "headCommittedAt": 1 }] })
}

struct Open<'a> {
    read: Entity<SessionRead<Value>>,
    seen: Arc<Mutex<Seen>>,
    changes: UnboundedReceiver<()>,
    changed: UnboundedSender<()>,
    cx: &'a mut TestAppContext,
    _heard: gpui_kit::Subscription,
    _backend: FakeBackend,
}

fn open(cx: &mut TestAppContext) -> Open<'_> {
    let seen = Arc::new(Mutex::new(Seen::default()));
    let (changed, changes) = unbounded();
    let backend = backend(seen.clone(), changed.clone());
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let read = cx.new(|cx| SessionRead::<Value>::new("worktrees", "list", cx));
    let tell = changed.clone();
    let heard = cx.update(|cx| cx.observe(&read, move |_, _| _ = tell.unbounded_send(())));
    Open {
        read,
        seen,
        changes,
        changed,
        cx,
        _heard: heard,
        _backend: backend,
    }
}

impl Open<'_> {
    fn select(&mut self, session: Option<&str>) {
        self.cx.update(|cx| {
            SelectedSession::global(cx).update(cx, |selected, cx| {
                selected.select(session.map(Into::into), cx)
            })
        });
        self.cx.run_until_parked();
    }

    fn show(&mut self, active: bool) {
        self.read
            .update(self.cx, |read, cx| read.set_active(active, cx));
        self.cx.run_until_parked();
    }

    fn shown(&mut self) -> Shown<Value> {
        self.cx.update(|cx| self.read.read(cx).shown().clone())
    }

    async fn until(&mut self, what: &str, mut done: impl FnMut(&Seen, &Shown<Value>) -> bool) {
        let (read, seen) = (self.read.clone(), self.seen.clone());
        let cx = &mut *self.cx;
        until(what, &mut self.changes, &self.changed, || {
            cx.run_until_parked();
            let shown = cx.update(|cx| read.read(cx).shown().clone());
            done(&seen.lock().unwrap(), &shown)
        })
        .await;
    }
}

// Pins: scm#4
#[gpui::test]
async fn it_reads_only_while_shown_and_follows_the_picked_session(cx: &mut TestAppContext) {
    let mut open = open(cx);
    open.select(Some("s0"));
    open.select(Some("s1"));
    open.show(true);
    open.until("s1 read", |seen, shown| {
        !seen.reads.is_empty() && matches!(shown, Shown::Read(_))
    })
    .await;
    assert_eq!(open.seen.lock().unwrap().sessions(), vec!["s1"]);

    open.select(Some("s2"));
    open.until("s2 read", |seen, _| seen.reads.len() == 2).await;
    assert_eq!(open.seen.lock().unwrap().sessions(), vec!["s1", "s2"]);

    open.show(false);
    open.select(Some("s3"));
    open.show(true);
    open.until("s3 read", |seen, _| seen.reads.len() >= 3).await;
    assert_eq!(open.seen.lock().unwrap().sessions(), vec!["s1", "s2", "s3"]);

    // Shown<Value> again for the same session, it reads again, and so does Refresh.
    open.until(
        "s3 shown",
        |_, shown| matches!(shown, Shown::Read(list) if list["worktrees"][0]["path"] == "/r/s3"),
    )
    .await;
    open.show(false);
    open.show(true);
    open.until("shown again", |seen, _| seen.reads.len() >= 4)
        .await;
    open.read.update(open.cx, |read, cx| read.refresh(cx));
    open.until("refreshed", |seen, _| seen.reads.len() >= 5)
        .await;
    assert_eq!(
        open.seen.lock().unwrap().sessions(),
        vec!["s1", "s2", "s3", "s3", "s3"]
    );
}

// Pins: scm#4
#[gpui::test]
async fn hiding_the_tab_stops_the_read_on_the_backend(cx: &mut TestAppContext) {
    let mut open = open(cx);
    open.select(Some("slow"));
    open.show(true);
    open.until("slow read sent", |seen, _| !seen.reads.is_empty())
        .await;
    open.show(false);
    open.until("slow read cancelled", |seen, _| !seen.cancels.is_empty())
        .await;
    open.read.update(open.cx, |read, cx| read.refresh(cx));
    let barrier = open.cx.update(|cx| {
        SocketClient::global(cx)
            .read(cx)
            .call::<Value>("test", "test_barrier", json!({}), None)
    });
    assert!(matches!(
        barrier.await,
        crate::core::socket::outcome::Outcome::Ok(_)
    ));
    let seen = open.seen.lock().unwrap();
    assert_eq!(
        seen.reads.len(),
        1,
        "a hidden refresh must not dispatch a new read"
    );
    assert_eq!(seen.cancels, vec![seen.reads[0].1.clone()]);
}

// Pins: scm#6
#[gpui::test]
async fn a_git_failure_shows_git_s_own_message(cx: &mut TestAppContext) {
    let mut open = open(cx);
    open.select(Some("bad"));
    open.show(true);
    open.until("failure shown", |_, shown| {
        matches!(shown, Shown::Failed(_))
    })
    .await;
    assert_eq!(open.shown(), Shown::Failed(GIT_SAYS.into()));
}

// Pins: scm#6
#[gpui::test]
async fn with_no_session_it_reads_nothing_and_asks_for_one(cx: &mut TestAppContext) {
    let mut open = open(cx);
    open.show(true);
    assert_eq!(open.shown(), Shown::NoSession);
    assert!(open.seen.lock().unwrap().reads.is_empty());
}

// Pins: scm#6
#[gpui::test]
async fn a_dropped_connection_says_so_instead_of_reading_forever(cx: &mut TestAppContext) {
    let mut open = open(cx);
    open.select(Some("dropped"));
    open.show(true);
    open.until("drop shown", |_, shown| matches!(shown, Shown::Failed(_)))
        .await;
    assert_eq!(
        open.shown(),
        Shown::Failed("The connection dropped.".into())
    );
    assert!(!open.cx.update(|cx| open.read.read(cx).reading()));
}

fn session_item(session: &str, state: &str) -> Value {
    json!({"sessionId":session,"agent":"fixture","repoUrl":"/owned","title":null,"machine":null,"parentSessionId":null,"acpSessionId":null,"updatedAt":1,"activeRunId":null,"state":state})
}
impl Open<'_> {
    async fn session_state(&mut self, session: &str, state: &str) {
        let call = self.cx.update(|cx| {
            SocketClient::global(cx).read(cx).call::<Value>(
                "test",
                "test_state",
                json!({"session":session,"state":state}),
                None,
            )
        });
        let _ = call.await;
        let cx = &mut *self.cx;
        until(
            "session state applied",
            &mut self.changes,
            &self.changed,
            || {
                cx.run_until_parked();
                cx.update(|cx| {
                    crate::core::state::sessions::Sessions::try_global(cx)
                        .unwrap()
                        .read(cx)
                        .items()
                        .iter()
                        .any(|item| item.session_id == session && item.state == state)
                })
            },
        )
        .await;
    }
}
// Pins: g9#10, g9#26
#[gpui::test]
async fn a_running_selected_turn_ending_refreshes_once_only_while_shown(cx: &mut TestAppContext) {
    let mut open = open(cx);
    open.cx
        .update(|cx| crate::core::state::sessions::Sessions::start(cx));
    open.read
        .update(open.cx, |read, cx| read.refresh_on_settle(cx));
    open.select(Some("s1"));
    open.show(true);
    open.until("initial read", |seen, shown| {
        seen.reads.len() == 1 && matches!(shown, Shown::Read(_))
    })
    .await;
    open.session_state("s1", "running").await;
    assert_eq!(open.seen.lock().unwrap().reads.len(), 1);
    open.session_state("s1", "idle").await;
    open.until("turn settled refresh", |seen, _| seen.reads.len() == 2)
        .await;
    open.session_state("s1", "idle").await;
    assert_eq!(open.seen.lock().unwrap().reads.len(), 2);
    open.session_state("another-session", "running").await;
    open.session_state("another-session", "failed").await;
    assert_eq!(open.seen.lock().unwrap().reads.len(), 2);
    open.show(false);
    open.session_state("s1", "running").await;
    open.session_state("s1", "waiting").await;
    assert_eq!(open.seen.lock().unwrap().reads.len(), 2);
    open.show(true);
    open.until("shown fresh", |seen, _| seen.reads.len() == 3)
        .await;
    open.session_state("s1", "running").await;
    open.select(Some("s2"));
    open.until("new selection", |seen, _| seen.reads.len() == 4)
        .await;
    open.session_state("s1", "idle").await;
    assert_eq!(
        open.seen.lock().unwrap().sessions(),
        vec!["s1", "s1", "s1", "s2"]
    );
}
// What was picked belongs to the session it was picked on.
#[gpui::test]
async fn another_checkout_is_read_until_the_session_changes(cx: &mut TestAppContext) {
    let mut open = open(cx);
    open.select(Some("s1"));
    open.show(true);
    open.until("s1 read", |seen, shown| seen.reads.len() == 1 && matches!(shown, Shown::Read(_))).await;
    let linked = CheckoutSelector { path: "/linked".into(), expected_root: "/own".into() };
    open.read.update(open.cx, |read, cx| read.set_checkout(Some(linked), cx));
    open.until("the other checkout read", |seen, _| seen.reads.len() == 2).await;
    open.select(Some("s2"));
    open.until("s2 read", |seen, _| seen.reads.len() == 3).await;
    assert_eq!(open.seen.lock().unwrap().picked, vec![Value::Null, json!("/linked"), Value::Null]);
    open.cx.update(|cx| assert!(open.read.read(cx).checkout().is_none()));
}
