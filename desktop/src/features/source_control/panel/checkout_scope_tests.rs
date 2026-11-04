//! Looking at another checkout of the session, against a backend that records what each read asked for.
use super::{PaneResult, SourceControlPane};
use crate::core::engine::SessionKey;
use crate::core::intents::open_git_history::OpenGitHistory;
use crate::core::socket::{
    checkout_selector::CheckoutSelector,
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply},
    row_source::ExpectedRowSource,
    test_wait::until,
};
use crate::core::state::{
    inspected_checkout::InspectedCheckout, selected_session::SelectedSession, sessions::Sessions,
    workspace_panes::WorkspaceKey,
};
use crate::features::source_control::actions_menu::{MenuAction, READ_ONLY_SCOPE};
use crate::features::source_control::git_checkouts::CheckoutHint;
use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{Entity, Modifiers, Subscription, px, size};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

const OWN: &str = "/owned";
const LINKED: &str = "/owned-linked";

fn session_row(version: i64, checkout: &str, operation: &str) -> Value {
    json!({"entityKind":"session","id":"s1","machine":"owner","version":version,"provider":"fixture","model":null,
        "folder":OWN,"title":null,"parentId":null,"parentMachine":null,"runId":null,"state":"idle","activityState":"idle",
        "epoch":1,"holder":"local","projectId":"project","checkoutState":checkout,"checkoutOperationId":operation})
}
fn offering(worktrees: Value) -> Option<Value> {
    Some(json!({"sessionRoot":OWN,"worktrees":worktrees}))
}
fn linked() -> Option<Value> {
    offering(json!([{"path":LINKED,"branch":"feature"}]))
}
fn status(branch: &str) -> Value {
    json!({"branch":branch,"head":"a".repeat(40),"ahead":0,"behind":0,"hasUpstream":false,
        "files":[{"path":"a.rs","originalPath":null,"index":"M","worktree":" "}],
        "totalFiles":1,"totalStaged":1,"fingerprint":branch})
}
fn selects(args: &Value) -> bool {
    args.get("checkoutPath").is_some() || args.get("expectedCheckoutRoot").is_some()
}
#[derive(Default)]
struct Seen {
    statuses: Vec<Value>,
    checkouts: Vec<Value>,
    branches: Vec<Value>,
    histories: Vec<Value>,
    commits: Vec<Value>,
    commands: Vec<Value>,
    subscriptions: Vec<Value>,
    /// What `git/checkouts` answers. An owner too old to have it says `not_found`.
    offered: Option<Value>,
    /// What a read of the other checkout is refused with.
    refused: Option<&'static str>,
}
struct Open<'a> {
    pane: Entity<SourceControlPane>,
    cx: &'a mut VisualTestContext,
    seen: Arc<Mutex<Seen>>,
    changed: UnboundedSender<()>,
    changes: UnboundedReceiver<()>,
    _backend: FakeBackend,
    _heard: Subscription,
}
/// The panel on s1 once its status and `git/checkouts` have both answered.
async fn open(cx: &mut TestAppContext, offered: Option<Value>) -> Open<'_> {
    let (changed, changes) = unbounded();
    let heard = changed.clone();
    let seen = Arc::new(Mutex::new(Seen {
        offered,
        ..Seen::default()
    }));
    let answered = seen.clone();
    let backend = FakeBackend::start(move |frame| {
        let id = frame["id"].clone();
        let result = |value: Value| Reply::Frame(json!({"t":"result","id":id,"result":value}));
        let refuse =
            |code: &str| Reply::Frame(json!({"t":"error","id":id,"code":code,"message":"refused"}));
        let mut seen = answered.lock().unwrap();
        let args = frame["args"].clone();
        let replies = match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("hello"), _) => vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"box","engine":"owner"}),
            )],
            (Some("sub"), _) if frame["stream"] == "engine" => {
                seen.subscriptions.push(id.clone());
                vec![Reply::Frame(
                    json!({"t":"engine_snapshot","id":id,"machine":"owner","through":1,
                    "items":if frame["topic"] == "sessions" { json!([session_row(1,"ready","first")]) } else { json!([]) },"more":false}),
                )]
            }
            (Some("sub"), _) => vec![Reply::Frame(json!({"t":"state","sub":id,"items":[]}))],
            (Some("call"), Some("status")) => {
                seen.statuses.push(args.clone());
                match (selects(&args), seen.refused) {
                    (true, Some(code)) => vec![refuse(code)],
                    (true, None) => vec![result(status("feature"))],
                    (false, _) => vec![result(status("main"))],
                }
            }
            (Some("call"), Some("checkouts")) => {
                seen.checkouts.push(args);
                match seen.offered.clone() {
                    Some(list) => vec![result(list)],
                    None => vec![refuse("not_found")],
                }
            }
            (Some("call"), Some("owner")) => {
                vec![result(
                    json!({"machine":"elsewhere","checkoutRoot":OWN,"remoteSessionId":"s1"}),
                )]
            }
            (Some("call"), Some("branches")) => {
                seen.branches.push(args);
                vec![result(json!({"remotes":[]}))]
            }
            (Some("call"), Some("history")) => {
                seen.histories.push(args);
                vec![result(json!({"commits":[],"nextCursor":null}))]
            }
            (Some("call"), Some("commit")) => {
                seen.commits.push(args);
                vec![result(json!({}))]
            }
            (Some("call"), Some("command")) => {
                seen.commands.push(args);
                let mut replies = vec![result(
                    json!({"applied":true,"late":false,"error":null,"version":2,"seq":2,"epoch":1}),
                )];
                replies.extend(seen.subscriptions.iter().map(|id| Reply::Frame(json!({"t":"engine_events","id":id,"machine":"owner","after":1,"through":2,
                    "events":[{"machine":"owner","seq":2,"entityKind":"session","entityId":"s1","version":2}]}))));
                replies
            }
            (Some("call"), Some("entities")) => {
                let operation = seen.commands.last().map_or("first".to_string(), |command| {
                    command["commandId"].as_str().unwrap().to_string()
                });
                let version = if seen.commands.is_empty() { 1 } else { 2 };
                vec![result(
                    json!({"items":[session_row(version,"ready",&operation)],"next":null}),
                )]
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
        SelectedSession::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint.clone(), cx);
        SocketClient::start_bulk(endpoint, cx);
        Sessions::start(cx);
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.commit_open(
                SessionKey {
                    machine: "owner".into(),
                    id: "s1".into(),
                },
                ExpectedRowSource {
                    machine: "owner".into(),
                    generation: 0,
                    route_machine: None,
                },
                false,
                cx,
            )
        });
    });
    let (pane, visual) = cx.add_window_view(SourceControlPane::new);
    visual.simulate_resize(size(px(500.), px(900.)));
    let tell = changed.clone();
    let heard = visual.update(|_, cx| {
        cx.observe(&pane, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    pane.update_in(visual, |pane, window, cx| pane.set_active(true, window, cx));
    let mut open = Open {
        pane,
        cx: visual,
        seen,
        changed,
        changes,
        _backend: backend,
        _heard: heard,
    };
    open.settled().await;
    let bulk = open.cx.update(|_, cx| {
        SocketClient::try_bulk(cx)
            .expect("the bulk socket")
            .read(cx)
            .call::<Value>("test", "test_barrier", json!({}), None)
    });
    let _ = bulk.await;
    open
}
impl Open<'_> {
    async fn until(
        &mut self,
        what: &str,
        mut done: impl FnMut(&Entity<SourceControlPane>, &mut VisualTestContext, &Seen) -> bool,
    ) {
        let (pane, seen, cx) = (self.pane.clone(), self.seen.clone(), &mut *self.cx);
        until(what, &mut self.changes, &self.changed, || {
            cx.run_until_parked();
            done(&pane, cx, &seen.lock().unwrap())
        })
        .await;
    }
    /// The status is shown and the list read beside it has answered.
    async fn settled(&mut self) {
        self.until("the status and the list", |pane, cx, seen| {
            let shown = pane.update(cx, |pane, cx| pane.shown_status(cx)).is_some();
            let answered = cx.update(|_, cx| pane.read(cx).checkouts_read.is_none());
            shown && answered && !seen.checkouts.is_empty()
        })
        .await;
    }
    async fn barrier(&mut self) {
        let call = self.cx.update(|_, cx| {
            SocketClient::global(cx)
                .read(cx)
                .call::<Value>("test", "test_barrier", json!({}), None)
        });
        let _ = call.await;
        self.cx.run_until_parked();
    }
    fn pick(&mut self, path: Option<&str>) {
        let path = path.map(String::from);
        let pane = self.pane.clone();
        pane.update(self.cx, |pane, cx| pane.pick_checkout(path, cx));
    }
    async fn showing_linked(&mut self) {
        self.pick(Some(LINKED));
        self.until("the other checkout is shown", |pane, cx, _| {
            let status = pane.update(cx, |pane, cx| pane.shown_status(cx));
            status.is_some_and(|status| status.branch.as_deref() == Some("feature"))
        })
        .await;
        self.draw();
    }
    fn selection(&mut self) -> Option<CheckoutSelector> {
        let pane = self.pane.clone();
        self.cx.update(|_, cx| pane.read(cx).selection(cx))
    }
    fn draw(&mut self) {
        self.cx.update(|window, cx| window.draw(cx).clear(cx));
    }
}
fn owner(generation: u64) -> ExpectedRowSource {
    ExpectedRowSource {
        machine: "owner".into(),
        generation,
        route_machine: None,
    }
}
fn saved(generation: u64) -> CheckoutHint {
    CheckoutHint {
        session: "s1".into(),
        source: owner(generation),
        path: LINKED.into(),
        expected_root: OWN.into(),
    }
}
impl Open<'_> {
    /// The panel as a saved layout builds it, up to the next list.
    fn restore(&mut self, hint: CheckoutHint) {
        let pane = self.pane.clone();
        pane.update(self.cx, |pane, cx| {
            pane.checkout_hint = Some(hint);
            pane.read.update(cx, |read, cx| read.refresh(cx));
        });
        assert_eq!(self.selection(), None, "a hint alone reads nothing");
    }
    fn hint(&mut self) -> Option<CheckoutHint> {
        let pane = self.pane.clone();
        self.cx.update(|_, cx| pane.read(cx).checkout_hint.clone())
    }
    async fn dropped_and_said(&mut self) {
        self.until("the hint is dropped", |pane, cx, _| {
            cx.update(|_, cx| pane.read(cx).checkout_hint.is_none())
        })
        .await;
        self.barrier().await;
        assert_eq!(self.selection(), None);
        assert!(!self.seen.lock().unwrap().statuses.iter().any(selects));
        let pane = self.pane.clone();
        self.cx.update(|_, cx| {
            assert!(matches!(&pane.read(cx).result, Some(PaneResult::Failed(failure)) if failure.sentence.contains("went back to the session folder")));
        });
    }
}
#[gpui::test]
async fn the_pull_requests_say_they_stay_with_the_session_folder(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    open.draw();
    assert!(open.cx.debug_bounds("git-pr-scope").is_none());
    open.showing_linked().await;
    assert!(open.cx.debug_bounds("git-pr-scope").is_some());
}
#[gpui::test]
async fn an_older_owner_gets_no_picker_and_never_a_selector(cx: &mut TestAppContext) {
    let mut open = open(cx, None).await;
    open.draw();
    assert!(open.cx.debug_bounds("git-scope").is_none());
    open.pick(Some(LINKED));
    open.barrier().await;
    assert_eq!(open.selection(), None);
    assert!(!open.seen.lock().unwrap().statuses.iter().any(selects));
}
#[gpui::test]
async fn a_session_with_nowhere_else_to_look_gets_no_picker(cx: &mut TestAppContext) {
    let mut open = open(cx, offering(json!([]))).await;
    open.draw();
    assert!(open.cx.debug_bounds("git-scope").is_none());
}
#[gpui::test]
async fn the_picker_appears_once_the_session_is_offered_a_checkout(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    open.draw();
    assert!(open.cx.debug_bounds("git-scope").is_some());
    let seen = open.seen.lock().unwrap();
    assert_eq!(seen.checkouts[0]["session"], "s1");
    assert_eq!(seen.checkouts[0]["expectedSource"]["machine"], "owner");
    assert!(
        !seen.statuses.iter().any(selects),
        "its own folder sends no selector"
    );
    assert!(!seen.histories.iter().any(selects));
}
// The reads that show a checkout name it; the read of the branches a write would use never does.
#[gpui::test]
async fn picking_a_checkout_reads_that_checkout(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    open.showing_linked().await;
    open.until("the commits of that checkout", |_, _, seen| {
        seen.histories.iter().any(selects)
    })
    .await;
    let seen = open.seen.lock().unwrap();
    let asked = seen.statuses.last().unwrap();
    assert_eq!(asked["checkoutPath"], LINKED);
    assert_eq!(asked["expectedCheckoutRoot"], OWN);
    assert_eq!(asked["expectedSource"]["machine"], "owner");
    let history = seen.histories.last().unwrap();
    assert_eq!(history["checkoutPath"], LINKED);
    assert_eq!(history["expectedCheckoutRoot"], OWN);
    assert!(!seen.branches.iter().any(selects));
    assert!(!seen.checkouts.iter().any(selects));
}
#[gpui::test]
async fn nothing_can_be_changed_while_another_checkout_is_shown(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    open.showing_linked().await;
    assert!(open.cx.debug_bounds("git-use-session-checkout").is_some());
    let pane = open.pane.clone();
    pane.update_in(open.cx, |pane, window, cx| {
        pane.message
            .update(cx, |message, cx| message.set_value("Fix it", window, cx));
        assert!(pane.target(cx).is_none(), "nothing writable to aim at");
        assert!(
            pane.shown_target(cx).is_some(),
            "what's shown still has its session"
        );
        let (rows, _) = pane.menu_rows(cx);
        let push = rows
            .iter()
            .find(|row| row.action == MenuAction::Push)
            .unwrap();
        assert_eq!(push.reason.as_deref(), Some(READ_ONLY_SCOPE));
        pane.commit(window, cx);
    });
    open.barrier().await;
    assert!(open.seen.lock().unwrap().commits.is_empty());
    open.pick(None);
    open.until("its own folder again", |pane, cx, _| {
        let status = pane.update(cx, |pane, cx| pane.shown_status(cx));
        status.is_some_and(|status| status.branch.as_deref() == Some("main"))
    })
    .await;
    assert!(
        !open
            .seen
            .lock()
            .unwrap()
            .statuses
            .last()
            .is_some_and(selects)
    );
    pane.update(open.cx, |pane, cx| assert!(pane.target(cx).is_some()));
}
#[gpui::test]
async fn another_session_leaves_the_checkout_behind(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    open.showing_linked().await;
    open.cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.commit_open(
                SessionKey {
                    machine: "owner".into(),
                    id: "s2".into(),
                },
                ExpectedRowSource {
                    machine: "owner".into(),
                    generation: 0,
                    route_machine: None,
                },
                false,
                cx,
            )
        })
    });
    open.until("the next session's list", |_, _, seen| {
        seen.checkouts
            .last()
            .is_some_and(|args| args["session"] == "s2")
    })
    .await;
    assert_eq!(open.selection(), None);
    let seen = open.seen.lock().unwrap();
    let asked = seen.statuses.last().unwrap();
    assert_eq!(asked["session"], "s2");
    assert!(!selects(asked));
}
// Pins the Engine as the authority: the read selector moves nothing, the shared checkout command does.
#[gpui::test]
async fn using_it_as_the_session_checkout_moves_the_session_through_the_engine(
    cx: &mut TestAppContext,
) {
    let mut open = open(cx, linked()).await;
    open.showing_linked().await;
    let button = open
        .cx
        .debug_bounds("git-use-session-checkout")
        .expect("the button");
    open.cx.simulate_click(button.center(), Modifiers::none());
    open.until("the checkout is submitted", |_, _, seen| {
        !seen.commands.is_empty()
    })
    .await;
    assert_eq!(
        open.seen.lock().unwrap().commands[0]["command"],
        json!({"kind":"change_checkout","checkoutKind":"folder","branch":null,"base":null,"folder":LINKED})
    );
    open.until(
        "it landed and this reads the session's own again",
        |pane, cx, seen| {
            let landed = cx.update(|_, cx| {
                pane.read(cx).checkout.is_none() && pane.read(cx).selection(cx).is_none()
            });
            landed && !seen.statuses.last().is_some_and(selects)
        },
    )
    .await;
}
// Pins: the Changes tab follows what this panel read, never a pick still on its way.
#[gpui::test]
async fn it_publishes_only_the_checkout_it_has_read(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    let published = |open: &mut Open<'_>| {
        open.cx
            .update(|_, cx| InspectedCheckout::global(cx).read(cx).current.clone())
            .expect("a published status")
    };
    let own = published(&mut open);
    assert_eq!(own.session, "s1");
    assert_eq!(own.checkout, None);
    open.showing_linked().await;
    let linked = published(&mut open);
    assert_eq!(
        linked
            .checkout
            .as_ref()
            .map(|checkout| checkout.path.as_str()),
        Some(LINKED)
    );
    assert_eq!(
        linked.checkout.map(|checkout| checkout.expected_root),
        Some(OWN.to_string())
    );
}
#[gpui::test]
async fn reveal_says_so_when_another_machine_owns_the_file(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    let pane = open.pane.clone();
    pane.update(open.cx, |pane, cx| pane.reveal_file("a.rs".into(), cx));
    open.until("the refusal", |pane, cx, _| {
        cx.update(|_, cx| {
            matches!(&pane.read(cx).result, Some(PaneResult::Failed(failure))
                if failure.sentence == "Reveal is available only for files owned by this desktop.")
        })
    })
    .await;
}

#[path = "checkout_scope_saved_tests.rs"]
mod saved_tests;
