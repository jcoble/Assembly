use super::{PaneResult, RunningOperation, SourceControlPane};
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use crate::core::state::{selected_session::SelectedSession, sessions::Sessions};
use crate::features::source_control::git_operation::OperationStatus;
use crate::features::source_control::git_status::GitStatus;
use crate::features::source_control::git_target::GitTarget;
use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{AppContext as _, Entity};
use serde_json::json;
use std::sync::{Arc, Mutex};

/// A session whose read answers `/old` first and `/current` after, as a session moved to another checkout does.
struct Fixture {
    _backend: FakeBackend,
    /// Status reads then branch reads the backend has answered.
    reads: Arc<Mutex<(usize, usize)>>,
    changed: UnboundedSender<()>,
    changes: UnboundedReceiver<()>,
}
fn start(cx: &mut TestAppContext) -> Fixture {
    let (changed, changes) = unbounded();
    let heard = changed.clone();
    let reads = Arc::new(Mutex::new((0usize, 0usize)));
    let answered = reads.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        let mut reads = answered.lock().unwrap();
        let result = if frame["method"] == "status" {
            reads.0 += 1;
            let root = if reads.0 == 1 { "/old" } else { "/current" };
            json!({"branch":"main","head":"a".repeat(40),"ahead":0,"behind":0,"hasUpstream":false,"files":[],"totalFiles":0,"totalStaged":0,"fingerprint":"actual","checkoutRoot":root})
        } else if frame["method"] == "branches" {
            reads.1 += 1;
            json!({"remotes":[format!("remote-{}", reads.1)]})
        } else {
            json!({})
        };
        let _ = heard.unbounded_send(());
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
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
        SelectedSession::global(cx)
            .update(cx, |selected, cx| selected.select(Some("s1".into()), cx));
    });
    Fixture {
        _backend: backend,
        reads,
        changed,
        changes,
    }
}
fn root(pane: &Entity<SourceControlPane>, cx: &mut VisualTestContext) -> Option<String> {
    pane.update(cx, |pane, cx| pane.shown_status(cx))
        .and_then(|status| status.checkout_root)
}
fn status_on(branch: &str, root: &str) -> GitStatus {
    serde_json::from_value(json!({"branch":branch,"head":"b".repeat(40),"ahead":0,"behind":0,"hasUpstream":false,"files":[],"totalFiles":0,"totalStaged":0,"fingerprint":"written","checkoutRoot":root})).unwrap()
}
/// The write authority answering, exactly as `GitAction::run`'s reply does.
fn write_answers(
    pane: &Entity<SourceControlPane>,
    target: GitTarget,
    status: GitStatus,
    cx: &mut VisualTestContext,
) {
    let action = pane.update(cx, |pane, _| pane.writes.action.clone());
    action.update(cx, |action, cx| {
        action.target = Some(target);
        action.message = Some("Staged.".into());
        action.status = Some(status);
        action.succeeded = true;
        action.completed += 1;
        cx.notify();
    });
    cx.run_until_parked();
}
// Pins: g9#10
#[gpui::test]
async fn a_write_reply_for_the_previous_checkout_does_not_paint_over_the_current_read(
    cx: &mut TestAppContext,
) {
    let Fixture {
        _backend,
        reads: _,
        changed,
        mut changes,
    } = start(cx);
    let (pane, cx) = cx.add_window_view(|window, cx| SourceControlPane::new(window, cx));
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    until("the first checkout is read", &mut changes, &changed, || {
        cx.run_until_parked();
        root(&pane, cx).as_deref() == Some("/old")
    })
    .await;
    let previous = pane.update(cx, |pane, cx| pane.target(cx)).unwrap();
    pane.update(cx, |pane, cx| {
        pane.read.update(cx, |read, cx| read.refresh(cx))
    });
    until("the session moved", &mut changes, &changed, || {
        cx.run_until_parked();
        root(&pane, cx).as_deref() == Some("/current")
    })
    .await;

    write_answers(&pane, previous, status_on("stale", "/old"), cx);
    pane.update(cx, |pane, cx| {
        let status = pane.shown_status(cx).unwrap();
        assert_eq!(status.branch.as_deref(), Some("main"));
        assert_eq!(status.checkout_root.as_deref(), Some("/current"));
        assert!(pane.result.is_none());
    });

    let current = pane.update(cx, |pane, cx| pane.target(cx)).unwrap();
    write_answers(&pane, current, status_on("landed", "/current"), cx);
    pane.update(cx, |pane, cx| {
        assert_eq!(
            pane.shown_status(cx).unwrap().branch.as_deref(),
            Some("landed")
        );
        assert_eq!(pane.result, Some(PaneResult::Done("Staged.".into())));
    });
}
// Pins: g9#6
#[gpui::test]
async fn branches_read_from_the_previous_checkout_are_replaced_when_the_root_moves(
    cx: &mut TestAppContext,
) {
    let Fixture {
        _backend,
        reads: _,
        changed,
        mut changes,
    } = start(cx);
    let (pane, cx) = cx.add_window_view(|window, cx| SourceControlPane::new(window, cx));
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    let remotes = |pane: &Entity<SourceControlPane>, cx: &mut VisualTestContext| {
        pane.update(cx, |pane, _| {
            pane.branches.as_ref().map(|list| list.remotes.clone())
        })
    };
    until("the first branch list", &mut changes, &changed, || {
        cx.run_until_parked();
        remotes(&pane, cx) == Some(vec!["remote-1".to_string()])
    })
    .await;
    pane.update(cx, |pane, cx| {
        pane.read.update(cx, |read, cx| read.refresh(cx))
    });
    until(
        "the second checkout's branch list",
        &mut changes,
        &changed,
        || {
            cx.run_until_parked();
            remotes(&pane, cx) == Some(vec!["remote-2".to_string()])
        },
    )
    .await;
}
/// The write authority settling a long operation the pane started, as its watch does when the status arrives.
fn operation_settles(
    pane: &Entity<SourceControlPane>,
    target: GitTarget,
    id: &str,
    state: &str,
    result: serde_json::Value,
    cx: &mut VisualTestContext,
) {
    pane.update(cx, |pane, _| {
        pane.running = Some(RunningOperation {
            action: "fetch",
            target,
            edit_revision: pane.edit_revision,
        });
    });
    let status: OperationStatus = serde_json::from_value(json!({
        "operationId":id,"instanceId":"i1","machine":"local","version":2,"state":state,"result":result
    }))
    .unwrap();
    let operation = pane.update(cx, |pane, _| pane.writes.operation.clone());
    operation.update(cx, |operation, cx| {
        operation.action = Some("fetch");
        operation.busy = false;
        operation.status = Some(status);
        cx.notify();
    });
    cx.run_until_parked();
}
// Pins: g9#10
#[gpui::test]
async fn a_long_operation_finishing_for_the_previous_checkout_leaves_the_current_one_alone(
    cx: &mut TestAppContext,
) {
    let Fixture {
        _backend,
        reads,
        changed,
        mut changes,
    } = start(cx);
    let (pane, cx) = cx.add_window_view(|window, cx| SourceControlPane::new(window, cx));
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    until("the first checkout is read", &mut changes, &changed, || {
        cx.run_until_parked();
        root(&pane, cx).as_deref() == Some("/old")
    })
    .await;
    let previous = pane.update(cx, |pane, cx| pane.target(cx)).unwrap();
    pane.update(cx, |pane, cx| {
        pane.read.update(cx, |read, cx| read.refresh(cx))
    });
    until("the session moved", &mut changes, &changed, || {
        cx.run_until_parked();
        root(&pane, cx).as_deref() == Some("/current")
            && pane.update(cx, |pane, _| {
                pane.branches_read.is_none() && pane.branches.is_some()
            })
    })
    .await;
    let settled_reads = *reads.lock().unwrap();

    let old = json!({"message":"Fetched.","status":status_on("stale", "/old")});
    operation_settles(&pane, previous.clone(), "op-old", "succeeded", old, cx);
    pane.update(cx, |pane, cx| {
        let status = pane.shown_status(cx).unwrap();
        assert_eq!(status.branch.as_deref(), Some("main"));
        assert_eq!(status.checkout_root.as_deref(), Some("/current"));
        assert!(pane.result.is_none());
        assert!(pane.running.is_none());
    });
    assert_eq!(
        *reads.lock().unwrap(),
        settled_reads,
        "nothing read again for the old checkout"
    );

    operation_settles(&pane, previous, "op-old-failed", "failed", json!(null), cx);
    pane.update(cx, |pane, _| assert!(pane.result.is_none()));

    let current = pane.update(cx, |pane, cx| pane.target(cx)).unwrap();
    let landed = json!({"message":"Fetched.","status":status_on("landed", "/current")});
    operation_settles(&pane, current, "op-current", "succeeded", landed, cx);
    pane.update(cx, |pane, cx| {
        assert_eq!(
            pane.shown_status(cx).unwrap().branch.as_deref(),
            Some("landed")
        );
        assert_eq!(pane.result, Some(PaneResult::Done("Fetched.".into())));
    });
}
