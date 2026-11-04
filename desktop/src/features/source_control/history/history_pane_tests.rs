use super::HistoryPane;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use crate::core::state::{selected_session::SelectedSession, sessions::Sessions};
use crate::features::source_control::git_status::GitStatus;
use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{Entity, MouseButton, Modifiers, MouseDownEvent, MouseUpEvent, Subscription, px, size};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

fn status(branch: serde_json::Value, root: serde_json::Value) -> GitStatus {
    serde_json::from_value(json!({
        "branch":branch,"head":"abc","ahead":0,"behind":0,"hasUpstream":true,"files":[],"totalFiles":0,"fingerprint":"f","checkoutRoot":root
    }))
    .unwrap()
}
// Pins: g9#18
#[gpui::test]
fn the_file_chip_clears_the_path_filter(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    let (pane, visual) = cx.add_window_view(|window, cx| {
        HistoryPane::new("s1".into(), Some("src/a.rs".into()), None, window, cx)
    });
    visual.simulate_resize(size(px(1200.), px(400.)));
    visual.run_until_parked();
    visual.update(|window, cx| window.draw(cx).clear(cx));
    let clear = visual.debug_bounds("history-path-clear").expect("the chip's clear button").center();
    let modifiers = Modifiers::default();
    visual.simulate_mouse_move(clear, None, modifiers);
    visual.simulate_event(MouseDownEvent { position: clear, button: MouseButton::Left, modifiers, click_count: 1, first_mouse: false });
    visual.simulate_event(MouseUpEvent { position: clear, button: MouseButton::Left, modifiers, click_count: 1 });
    visual.run_until_parked();
    visual.update(|window, cx| {
        window.draw(cx).clear(cx);
        assert_eq!(pane.read(cx).path, None);
    });
    assert!(visual.debug_bounds("history-path-clear").is_none());
}
#[gpui::test]
fn the_folder_label_names_the_checkout_it_read_and_nothing_else(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    let (pane, visual) = cx.add_window_view(|window, cx| HistoryPane::new("s1".into(), None, None, window, cx));
    visual.update(|_, cx| {
        pane.update(cx, |pane, _| {
            assert_eq!(pane.folder_label(), "No repository");
            pane.status = Some(status(json!("main"), json!("/home/me/Assembly/")));
            assert_eq!(pane.folder_label(), "Assembly (main)");
            pane.status = Some(status(json!(null), json!("/home/me/Assembly")));
            assert_eq!(pane.folder_label(), "Assembly");
            pane.status = Some(status(json!("main"), json!(null)));
            assert_eq!(pane.folder_label(), "This folder (main)");
        })
    });
}
fn session_row(session: &str, state: &str) -> Value {
    json!({"sessionId":session,"agent":"fixture","repoUrl":"/owned","title":null,"machine":null,"parentSessionId":null,"acpSessionId":null,"updatedAt":1,"activeRunId":null,"state":state})
}
#[derive(Default)]
struct Seen {
    subscription: Value,
    status: usize,
    history: usize,
}
struct Turning<'a> {
    pane: Entity<HistoryPane>,
    cx: &'a mut VisualTestContext,
    seen: Arc<Mutex<Seen>>,
    changed: UnboundedSender<()>,
    changes: UnboundedReceiver<()>,
    _backend: FakeBackend,
    _heard: Vec<Subscription>,
}
/// A History tab on s1, which is running; s2 is another session running beside it. Each status read moves the head.
async fn turning(cx: &mut TestAppContext) -> Turning<'_> {
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let seen = Arc::new(Mutex::new(Seen::default()));
    let answered = seen.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        let id = frame["id"].clone();
        let mut seen = answered.lock().unwrap();
        let replies = match (frame["t"].as_str(), frame["topic"].as_str(), frame["method"].as_str()) {
            (Some("sub"), Some("sessions"), _) => {
                seen.subscription = id.clone();
                vec![Reply::Frame(json!({"t":"state","sub":id,"items":[
                    ["s1",session_row("s1","running")],["s2",session_row("s2","running")]
                ]}))]
            }
            (Some("sub"), ..) => vec![Reply::Frame(json!({"t":"state","sub":id,"items":[]}))],
            (Some("call"), _, Some("test_state")) => vec![
                Reply::Frame(json!({"t":"change","sub":seen.subscription,"key":frame["args"]["session"],
                    "item":session_row(frame["args"]["session"].as_str().unwrap(),frame["args"]["state"].as_str().unwrap())})),
                Reply::Frame(json!({"t":"result","id":id,"result":{}})),
            ],
            (Some("call"), _, Some("status")) => {
                seen.status += 1;
                let head = seen.status.to_string().repeat(40);
                vec![Reply::Frame(json!({"t":"result","id":id,"result":
                    {"branch":"main","head":head,"ahead":0,"behind":0,"hasUpstream":true,"files":[],"totalFiles":0,"fingerprint":"f"}}))]
            }
            (Some("call"), _, Some("history")) => {
                seen.history += 1;
                vec![Reply::Frame(json!({"t":"result","id":id,"result":{"commits":[],"nextCursor":null}}))]
            }
            (Some("call"), ..) => vec![Reply::Frame(json!({"t":"result","id":id,"result":{}}))],
            _ => vec![],
        };
        let _ = heard.unbounded_send(());
        replies
    });
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
        Sessions::start(cx);
    });
    let sessions = cx.update(|cx| Sessions::try_global(cx).unwrap());
    let tell = changed.clone();
    let listed = cx.update(|cx| {
        cx.observe(&sessions, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    until("the sessions are listed", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| sessions.read(cx).items().len() == 2)
    })
    .await;
    let (pane, visual) =
        cx.add_window_view(|window, cx| HistoryPane::new("s1".into(), None, None, window, cx));
    let tell = changed.clone();
    let watched = visual.update(|_, cx| {
        cx.observe(&pane, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    let mut turning = Turning {
        pane,
        cx: visual,
        seen,
        changed,
        changes,
        _backend: backend,
        _heard: vec![listed, watched],
    };
    turning.reads_reach(1, 1).await;
    turning
}
impl Turning<'_> {
    async fn reads_reach(&mut self, status: usize, history: usize) {
        let (seen, pane, cx) = (self.seen.clone(), self.pane.clone(), &mut *self.cx);
        until("the reads", &mut self.changes, &self.changed, || {
            cx.run_until_parked();
            let seen = seen.lock().unwrap();
            let answered = cx.update(|_, cx| pane.read(cx).status_read.is_none());
            answered && seen.status == status && seen.history == history
        })
        .await;
    }
    /// Answers once the backend has applied the state, and everything sent before it has been seen.
    async fn session_becomes(&mut self, session: &str, state: &str) {
        let call = self.cx.update(|_, cx| {
            SocketClient::global(cx).read(cx).call::<Value>(
                "test",
                "test_state",
                json!({"session":session,"state":state}),
                None,
            )
        });
        let _ = call.await;
        let (session, cx) = (session.to_string(), &mut *self.cx);
        until("the state applied", &mut self.changes, &self.changed, || {
            cx.run_until_parked();
            cx.update(|_, cx| {
                Sessions::try_global(cx).unwrap().read(cx).items().iter()
                    .any(|item| item.session_id == session && item.state == state)
            })
        })
        .await;
        let barrier = self.cx.update(|_, cx| {
            SocketClient::global(cx).read(cx).call::<Value>("test", "test_barrier", json!({}), None)
        });
        let _ = barrier.await;
        self.cx.run_until_parked();
    }
    fn reads(&self) -> (usize, usize) {
        let seen = self.seen.lock().unwrap();
        (seen.status, seen.history)
    }
}
// Pins: g9#18; a commit made in the turn moves the head, so the list has to read it too.
#[gpui::test]
async fn a_turn_ending_rereads_the_shown_status(cx: &mut TestAppContext) {
    let mut open = turning(cx).await;
    open.session_becomes("s2", "idle").await;
    assert_eq!(open.reads(), (1, 1), "another session's turn says nothing about this folder");
    open.session_becomes("s1", "idle").await;
    open.reads_reach(2, 2).await;
    open.session_becomes("s1", "failed").await;
    assert_eq!(open.reads(), (2, 2), "only a turn that was running ends");
}
// Pins: g9#18
#[gpui::test]
async fn a_turn_ending_while_hidden_reads_nothing(cx: &mut TestAppContext) {
    let mut open = turning(cx).await;
    let pane = open.pane.clone();
    pane.update_in(open.cx, |pane, window, cx| pane.set_active(false, window, cx));
    open.session_becomes("s1", "idle").await;
    assert_eq!(open.reads(), (1, 1));
}
