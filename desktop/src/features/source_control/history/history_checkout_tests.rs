//! The History tab reading another checkout, which moves nothing in the session or the panel.
use super::HistoryPane;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    row_source::ExpectedRowSource,
    test_wait::until,
};
use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::{Entity, Subscription};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

const OWN: &str = "/home/me/owned";
const LINKED: &str = "/home/me/owned-linked";
const SHA: &str = "aaaaaaaa";

fn selects(args: &Value) -> bool {
    args.get("checkoutPath").is_some() || args.get("expectedCheckoutRoot").is_some()
}
fn origin() -> ExpectedRowSource {
    ExpectedRowSource { machine: "owner".into(), generation: 7, route_machine: None }
}
#[derive(Default)]
struct Seen {
    statuses: Vec<Value>,
    checkouts: Vec<Value>,
    histories: Vec<Value>,
    files: Vec<Value>,
    /// What `git/checkouts` answers. An owner too old to have it says `not_found`.
    offered: bool,
    refused: Option<&'static str>,
}
struct Open<'a> {
    pane: Entity<HistoryPane>,
    cx: &'a mut VisualTestContext,
    seen: Arc<Mutex<Seen>>,
    changed: UnboundedSender<()>,
    changes: UnboundedReceiver<()>,
    _backend: FakeBackend,
    _heard: Vec<Subscription>,
}
async fn open(cx: &mut TestAppContext, offered: bool, source: Option<ExpectedRowSource>) -> Open<'_> {
    let (changed, changes) = unbounded();
    let heard = changed.clone();
    let seen = Arc::new(Mutex::new(Seen { offered, ..Seen::default() }));
    let answered = seen.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        let id = frame["id"].clone();
        let result = |value: Value| Reply::Frame(json!({"t":"result","id":id,"result":value}));
        let refuse = |code: &str| Reply::Frame(json!({"t":"error","id":id,"code":code,"message":"refused"}));
        let mut seen = answered.lock().unwrap();
        let args = frame["args"].clone();
        let replies = match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("sub"), _) => vec![Reply::Frame(json!({"t":"state","sub":id,"items":[]}))],
            (Some("call"), Some("status")) => {
                seen.statuses.push(args.clone());
                let (branch, root) = if selects(&args) { ("feature", LINKED) } else { ("main", OWN) };
                match (selects(&args), seen.refused) {
                    (true, Some(code)) => vec![refuse(code)],
                    _ => vec![result(json!({"branch":branch,"head":"a".repeat(40),"ahead":0,"behind":0,"hasUpstream":true,
                        "files":[],"totalFiles":0,"fingerprint":"f","checkoutRoot":root}))],
                }
            }
            (Some("call"), Some("checkouts")) => {
                seen.checkouts.push(args);
                if seen.offered {
                    vec![result(json!({"sessionRoot":OWN,"worktrees":[{"path":LINKED,"branch":"feature"}]}))]
                } else {
                    vec![refuse("not_found")]
                }
            }
            (Some("call"), Some("history")) => {
                seen.histories.push(args);
                vec![result(json!({"commits":[{"shortSha":"aaaaaaa","sha":SHA,"subject":"Commit","body":"","author":"Jesse",
                    "committedAt":"2026-11-03T12:00:00Z","refs":[],"parents":[]}],"nextCursor":null}))]
            }
            (Some("call"), Some("commitFiles")) => {
                seen.files.push(args);
                vec![result(json!([{"path":"a.rs","originalPath":null,"letter":"M"}]))]
            }
            (Some("call"), _) => vec![result(json!({}))],
            _ => vec![],
        };
        let _ = heard.unbounded_send(());
        replies
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(Endpoint { port: backend.port, token: "secret".into() }, cx);
    });
    let (pane, visual) = cx.add_window_view(|window, cx| HistoryPane::new("s1".into(), None, source, window, cx));
    let (tell, also) = (changed.clone(), changed.clone());
    let heard = visual.update(|_, cx| {
        let timeline = pane.read(cx).timeline.clone();
        vec![
            cx.observe(&pane, move |_, _| {
                let _ = tell.unbounded_send(());
            }),
            cx.observe(&timeline, move |_, _| {
                let _ = also.unbounded_send(());
            }),
        ]
    });
    let mut open = Open { pane, cx: visual, seen, changed, changes, _backend: backend, _heard: heard };
    open.until("the first reads", |pane, cx, seen| {
        let answered = cx.update(|_, cx| {
            let pane = pane.read(cx);
            pane.status_read.is_none() && pane.checkouts_read.is_none() && pane.status.is_some()
        });
        answered && !seen.histories.is_empty()
    })
    .await;
    open
}
impl Open<'_> {
    async fn until(&mut self, what: &str, mut done: impl FnMut(&Entity<HistoryPane>, &mut VisualTestContext, &Seen) -> bool) {
        let (pane, seen, cx) = (self.pane.clone(), self.seen.clone(), &mut *self.cx);
        until(what, &mut self.changes, &self.changed, || {
            cx.run_until_parked();
            done(&pane, cx, &seen.lock().unwrap())
        })
        .await;
    }
    fn pick(&mut self, path: Option<&str>) {
        let path = path.map(String::from);
        let pane = self.pane.clone();
        pane.update(self.cx, |pane, cx| pane.pick_checkout(path, cx));
    }
    async fn showing_linked(&mut self) {
        self.pick(Some(LINKED));
        self.until("the other checkout's commits", |pane, cx, seen| {
            let shown = cx.update(|_, cx| {
                let pane = pane.read(cx);
                pane.status.as_ref().is_some_and(|status| status.branch.as_deref() == Some("feature"))
                    && pane.timeline.read(cx).history.commits.len() == 1
            });
            shown && seen.histories.last().is_some_and(selects)
        })
        .await;
    }
}
#[gpui::test]
async fn an_older_owner_keeps_the_folder_label_and_never_gets_a_selector(cx: &mut TestAppContext) {
    let mut open = open(cx, false, Some(origin())).await;
    open.cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(open.cx.debug_bounds("history-repository").is_none());
    open.pick(Some(LINKED));
    open.cx.run_until_parked();
    let seen = open.seen.lock().unwrap();
    assert!(!seen.statuses.iter().chain(seen.histories.iter()).any(selects));
}
#[gpui::test]
async fn a_session_without_a_source_is_not_asked_for_its_checkouts(cx: &mut TestAppContext) {
    let open = open(cx, true, None).await;
    assert!(open.seen.lock().unwrap().checkouts.is_empty());
}
#[gpui::test]
async fn a_picked_repository_is_read_for_status_history_and_commit_files(cx: &mut TestAppContext) {
    let mut open = open(cx, true, Some(origin())).await;
    open.cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(open.cx.debug_bounds("history-repository").is_some());
    assert!(!open.seen.lock().unwrap().statuses.iter().any(selects), "its own folder sends none");
    open.showing_linked().await;
    let pane = open.pane.clone();
    pane.update(open.cx, |pane, cx| {
        pane.timeline.update(cx, |timeline, cx| timeline.toggle_commit(SHA.into(), cx))
    });
    open.until("the commit's files", |_, _, seen| !seen.files.is_empty()).await;
    let seen = open.seen.lock().unwrap();
    for asked in [seen.statuses.last().unwrap(), seen.histories.last().unwrap(), &seen.files[0]] {
        assert_eq!(asked["checkoutPath"], LINKED);
        assert_eq!(asked["expectedCheckoutRoot"], OWN);
        assert_eq!(asked["expectedSource"], serde_json::to_value(origin()).unwrap());
    }
}
#[gpui::test]
async fn a_refused_repository_goes_back_to_the_session_folder_and_says_so(cx: &mut TestAppContext) {
    let mut open = open(cx, true, Some(origin())).await;
    open.seen.lock().unwrap().refused = Some("checkout_unavailable");
    open.pick(Some(LINKED));
    open.until("back on its own folder", |pane, cx, seen| {
        let back = cx.update(|_, cx| {
            let pane = pane.read(cx);
            pane.checkout.is_none()
                && pane.status.as_ref().is_some_and(|status| status.branch.as_deref() == Some("main"))
        });
        back && seen.statuses.iter().any(selects)
    })
    .await;
    assert!(!open.seen.lock().unwrap().statuses.last().is_some_and(selects));
    open.cx.update(|_, cx| {
        assert!(open.pane.read(cx).notice.as_deref().is_some_and(|notice| notice.contains("went back to the session folder")));
    });
}
#[gpui::test]
async fn a_file_opens_only_from_the_session_folder_and_a_commit_diff_keeps_its_checkout(cx: &mut TestAppContext) {
    let mut open = open(cx, true, Some(origin())).await;
    open.cx.update(|_, cx| {
        let timeline = open.pane.read(cx).timeline.read(cx);
        assert!(timeline.current_file("a.rs").is_some());
        assert_eq!(timeline.commit_diff(SHA, "a.rs", "a.rs".into()).checkout, None);
    });
    open.showing_linked().await;
    open.cx.update(|_, cx| {
        let timeline = open.pane.read(cx).timeline.read(cx);
        assert!(timeline.current_file("a.rs").is_none(), "there's no way to open it in that checkout yet");
        let diff = timeline.commit_diff(SHA, "a.rs", "a.rs".into());
        assert_eq!(diff.checkout.expect("the checkout it came from").path, LINKED);
    });
    open.pick(None);
    open.until("its own commits", |pane, cx, seen| {
        let own = cx.update(|_, cx| pane.read(cx).timeline.read(cx).current_file("a.rs").is_some());
        own && !seen.histories.last().is_some_and(selects)
    })
    .await;
}
// The trigger names where it's looking while that folder's status is on its way.
#[gpui::test]
async fn the_label_names_the_picked_folder_before_its_status_answers(cx: &mut TestAppContext) {
    let mut open = open(cx, true, Some(origin())).await;
    open.pick(Some(LINKED));
    open.cx.update(|_, cx| assert_eq!(open.pane.read(cx).folder_label(), "owned-linked"));
    open.until("the other checkout's status", |pane, cx, _| cx.update(|_, cx| pane.read(cx).status.is_some())).await;
    open.cx.update(|_, cx| assert_eq!(open.pane.read(cx).folder_label(), "owned-linked (feature)"));
    open.pick(None);
    open.cx.update(|_, cx| assert_eq!(open.pane.read(cx).folder_label(), "owned (main)"));
}
