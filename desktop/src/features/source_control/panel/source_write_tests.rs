//! Writes the panel sends from a keystroke or a branch pick, against the source-derived wire shapes.
use super::SourceControlPane;
use crate::core::engine::SessionKey;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply},
    row_source::ExpectedRowSource,
    test_wait::until,
};
use crate::core::state::{
    selected_session::SelectedSession, session_item::SessionItem, sessions::Sessions,
};
use crate::features::source_control::branch_menu::BranchPicked;
use crate::features::source_control::result_line::PaneResult;
use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{Entity, Focusable as _, Subscription};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

const HEAD: &str = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

fn session_row(version: i64, checkout: &str, operation: &str) -> Value {
    json!({"entityKind":"session","id":"s1","machine":"owner","version":version,"provider":"fixture","model":null,
        "folder":"/owned","title":null,"parentId":null,"parentMachine":null,"runId":null,"state":"idle","activityState":"idle",
        "epoch":1,"holder":"local","projectId":"project","checkoutState":checkout,"checkoutOperationId":operation})
}
#[derive(Default)]
struct Seen {
    commits: Vec<Value>,
    switches: Vec<Value>,
    branch_reads: Vec<Value>,
    commands: Vec<Value>,
    statuses: usize,
    subscriptions: Vec<Value>,
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
/// The panel on s1, owned by this machine's Engine, with one staged file.
async fn open(cx: &mut TestAppContext) -> Open<'_> {
    let (changed, changes) = unbounded();
    let heard = changed.clone();
    let seen = Arc::new(Mutex::new(Seen::default()));
    let answered = seen.clone();
    let backend = FakeBackend::start(move |frame| {
        let id = frame["id"].clone();
        let result = |value: Value| Reply::Frame(json!({"t":"result","id":id,"result":value}));
        let mut seen = answered.lock().unwrap();
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
                seen.statuses += 1;
                vec![result(
                    json!({"branch":"main","head":HEAD,"ahead":0,"behind":0,"hasUpstream":false,
                    "files":[{"path":"a.rs","originalPath":null,"index":"M","worktree":" "}],
                    "totalFiles":1,"totalStaged":1,"fingerprint":"f"}),
                )]
            }
            (Some("call"), Some("branches")) => {
                seen.branch_reads.push(frame["args"].clone());
                vec![result(json!({"remotes":[]}))]
            }
            (Some("call"), Some("switchBranch")) => {
                seen.switches.push(frame["args"].clone());
                vec![result(json!({}))]
            }
            (Some("call"), Some("commit")) => {
                seen.commits.push(frame["args"].clone());
                vec![result(json!({}))]
            }
            (Some("call"), Some("command")) => {
                seen.commands.push(frame["args"].clone());
                let folder = frame["args"]["command"]["folder"].as_str();
                let moved = folder == Some("/always-moving")
                    || folder == Some("/moved-once") && seen.commands.len() == 1;
                if moved {
                    return vec![result(
                        json!({"applied":false,"late":false,"error":"version_conflict","version":5,"seq":0,"epoch":1}),
                    )];
                }
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
    open.until("the status is shown", |pane, cx| {
        pane.update(cx, |pane, cx| pane.shown_status(cx)).is_some()
    })
    .await;
    // An Engine row read refuses until the bulk socket has shaken hands, as it does in the app.
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
        mut done: impl FnMut(&Entity<SourceControlPane>, &mut VisualTestContext) -> bool,
    ) {
        let (pane, cx) = (self.pane.clone(), &mut *self.cx);
        until(what, &mut self.changes, &self.changed, || {
            cx.run_until_parked();
            done(&pane, cx)
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
}
// Pins: g9#2
#[gpui::test]
async fn cmd_enter_commits(cx: &mut TestAppContext) {
    let mut open = open(cx).await;
    open.cx.update(|window, cx| window.draw(cx).clear(cx));
    let pane = open.pane.clone();
    pane.update_in(open.cx, |pane, window, cx| {
        let focus = pane.message.read(cx).focus_handle(cx);
        window.focus(&focus, cx);
    });
    open.cx.update(|window, cx| window.draw(cx).clear(cx));
    open.cx.simulate_input("Fix it");
    open.cx.simulate_keystrokes("enter");
    open.barrier().await;
    assert!(
        open.seen.lock().unwrap().commits.is_empty(),
        "enter is a new line"
    );
    open.cx.simulate_keystrokes("secondary-enter");
    let seen = open.seen.clone();
    open.until("the commit", |_, _| {
        !seen.lock().unwrap().commits.is_empty()
    })
    .await;
    let commit = seen.lock().unwrap().commits[0].clone();
    assert_eq!(commit["session"], "s1");
    assert_eq!(commit["message"], "Fix it");
    assert_eq!(commit["amend"], false);
    assert_eq!(commit["expectedHead"], HEAD);
}
// Pins: g9#6
#[gpui::test]
async fn a_held_branch_submits_the_shared_checkout_and_retains_session_identity(
    cx: &mut TestAppContext,
) {
    let mut open = open(cx).await;
    let before = open.seen.lock().unwrap().statuses;
    let pane = open.pane.clone();
    pane.update_in(open.cx, |pane, window, cx| {
        pane.branch_picked(
            BranchPicked::MoveToFolder("/owned/other".into()),
            window,
            cx,
        )
    });
    let seen = open.seen.clone();
    open.until("the shared checkout is submitted", |_, _| {
        !seen.lock().unwrap().commands.is_empty()
    })
    .await;
    let command = seen.lock().unwrap().commands[0].clone();
    assert_eq!(command["entityId"], "s1");
    assert_eq!(command["machine"], "owner");
    assert_eq!(command["expectedVersion"], 1);
    assert_eq!(
        command["command"],
        json!({"kind":"change_checkout","checkoutKind":"folder","branch":null,"base":null,"folder":"/owned/other"})
    );
    open.until(
        "the checkout landed and the status was read again",
        |pane, cx| {
            let done = cx.update(|_, cx| pane.read(cx).checkout.is_none());
            done && seen.lock().unwrap().statuses > before
        },
    )
    .await;
    open.cx.update(|_, cx| {
        let selected = SelectedSession::global(cx);
        let selected = selected.read(cx);
        assert_eq!(
            selected.session_id(),
            Some("s1"),
            "the session stays the session"
        );
        assert_eq!(selected.key().map(|key| key.id.as_str()), Some("s1"));
        assert_eq!(
            pane.read(cx)
                .target(cx)
                .map(|target| target.session)
                .as_deref(),
            Some("s1")
        );
    });
}
#[gpui::test]
async fn a_row_that_moved_on_the_way_is_asked_once_more_at_its_new_version(
    cx: &mut TestAppContext,
) {
    let mut open = open(cx).await;
    let pane = open.pane.clone();
    pane.update_in(open.cx, |pane, window, cx| {
        pane.branch_picked(BranchPicked::MoveToFolder("/moved-once".into()), window, cx)
    });
    let seen = open.seen.clone();
    open.until("the checkout landed", |pane, cx| {
        cx.update(|_, cx| pane.read(cx).checkout.is_none())
    })
    .await;
    let commands = seen.lock().unwrap().commands.clone();
    assert_eq!(commands.len(), 2);
    assert_eq!(commands[0]["expectedVersion"], 1);
    assert_eq!(commands[1]["expectedVersion"], 5);
    assert_ne!(commands[0]["commandId"], commands[1]["commandId"]);
    assert_eq!(commands[0]["command"], commands[1]["command"]);
    open.cx.update(|_, cx| {
        assert!(!matches!(pane.read(cx).result, Some(PaneResult::Failed(_))));
    });
}
#[gpui::test]
async fn a_row_still_moving_after_that_says_so_in_words(cx: &mut TestAppContext) {
    let mut open = open(cx).await;
    let pane = open.pane.clone();
    pane.update_in(open.cx, |pane, window, cx| {
        pane.branch_picked(
            BranchPicked::MoveToFolder("/always-moving".into()),
            window,
            cx,
        )
    });
    open.until("the refusal", |pane, cx| {
        cx.update(|_, cx| pane.read(cx).checkout.is_none())
    })
    .await;
    assert_eq!(open.seen.lock().unwrap().commands.len(), 2);
    open.cx.update(|_, cx| match &pane.read(cx).result {
        Some(PaneResult::Failed(failure)) => {
            assert_eq!(failure.sentence, "The session just changed. Try again.")
        }
        _ => panic!("the refusal is shown"),
    });
}

/// A viewed folder has no session behind it, so its branch never goes through the Engine.
#[gpui::test]
async fn a_viewed_folders_branch_is_switched_there_and_no_session_moves(cx: &mut TestAppContext) {
    use crate::core::state::viewed_project::{Viewed, ViewedProject};

    let mut open = open(cx).await;
    open.cx.update(|_, cx| {
        let viewed = Viewed {
            project_id: "p".into(),
            machine: "local".into(),
            folder: "/other".into(),
            title: "other".into(),
        };
        ViewedProject::set(Some("s1".into()), Some(viewed), cx);
    });
    let id = "view:70:2f6f74686572";
    open.until("the viewed status", |pane, cx| {
        pane.read_with(cx, |pane, cx| {
            pane.target(cx).is_some_and(|target| target.session == id)
                && pane.shown_status(cx).is_some()
        })
    })
    .await;
    let pane = open.pane.clone();
    pane.update_in(open.cx, |pane, window, cx| {
        pane.branch_picked(BranchPicked::Create("made".into()), window, cx)
    });
    let seen = open.seen.clone();
    open.until("the switch", |_, _| {
        !seen.lock().unwrap().switches.is_empty()
    })
    .await;
    let seen = seen.lock().unwrap();
    assert_eq!(seen.switches[0]["session"], id);
    assert_eq!(seen.switches[0]["name"], "made");
    assert_eq!(seen.switches[0]["create"], true);
    assert_eq!(seen.switches[0]["expectedHead"], HEAD);
    assert!(seen.commands.is_empty(), "the session's checkout stays put");
}

/// Read-only stops writes, not reads: the branch menu still lists the folder it is looking at.
#[gpui::test]
async fn a_read_only_viewed_folder_still_reads_its_own_branches(cx: &mut TestAppContext) {
    use crate::core::state::viewed_project::{Viewed, ViewedProject};

    let mut open = open(cx).await;
    open.cx.update(|_, cx| {
        let working: SessionItem = serde_json::from_value(json!({
            "sessionId":"s2","agent":"fixture","repoUrl":"/other","updatedAt":0,
            "state":"waiting","title":"Busy","root":"/other","machine":"local"
        }))
        .unwrap();
        Sessions::start_holding(vec![working], false, false, cx);
        let viewed = Viewed {
            project_id: "p".into(),
            machine: "local".into(),
            folder: "/other".into(),
            title: "other".into(),
        };
        ViewedProject::set(Some("s1".into()), Some(viewed), cx);
    });
    let id = "view:70:2f6f74686572";
    open.until("the read-only viewed status", |pane, cx| {
        pane.read_with(cx, |pane, cx| {
            pane.read_only(cx)
                && pane
                    .shown_target(cx)
                    .is_some_and(|target| target.session == id)
        })
    })
    .await;
    // The commit box stays, turned off, as in the old app.
    open.until("the commit box", |_, cx| {
        cx.update(|window, cx| window.draw(cx).clear(cx));
        cx.debug_bounds("git-commit-box").is_some()
    })
    .await;
    let pane = open.pane.clone();
    pane.update_in(open.cx, |pane, _, cx| pane.set_branch_open(true, cx));
    let seen = open.seen.clone();
    open.until("the branch list of the viewed folder", |_, _| {
        seen.lock()
            .unwrap()
            .branch_reads
            .iter()
            .any(|args| args["session"] == id)
    })
    .await;
}
