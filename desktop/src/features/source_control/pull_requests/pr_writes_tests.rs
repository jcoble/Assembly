use super::super::{
    merge_dialog::MergeRequest, post_confirm::PostRequest, review_draft::ReviewIdentity,
};
use super::{PrOwner, PrWrites, WriteDone};
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    row_source::ExpectedRowSource,
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::{Entity, TestAppContext};
use gpui_kit::AppContext as _;
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};
fn identity() -> ReviewIdentity {
    ReviewIdentity {
        repository: "o/r".into(),
        number: 7,
        head: "a".repeat(40),
        base: "b".repeat(40),
    }
}
fn merge() -> MergeRequest {
    MergeRequest {
        number: 7,
        head: "a".repeat(40),
        base: "b".repeat(40),
        head_branch: "feature".into(),
        base_branch: "main".into(),
        method: "squash".into(),
    }
}
/// A backend that ends every started write in this state, with this result.
fn start_backend(
    state: &'static str,
    outcome: Value,
    cx: &mut TestAppContext,
) -> (
    FakeBackend,
    Arc<Mutex<Vec<Value>>>,
    futures_channel::mpsc::UnboundedSender<()>,
    futures_channel::mpsc::UnboundedReceiver<()>,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        observed.lock().unwrap().push(frame.clone());
        let _ = heard.unbounded_send(());
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        let result = if frame["method"] == "operation_host" {
            json!({"instanceId":"owned","machine":"machine"})
        } else if frame["method"] == "start" {
            json!({"operationId":frame["args"]["operationId"],"instanceId":"owned","machine":"machine","version":2,"state":state,"error":outcome["error"],"result":outcome["result"]})
        } else {
            return vec![];
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    (backend, seen, changed, changes)
}
fn starts(seen: &Mutex<Vec<Value>>) -> Vec<Value> {
    seen.lock()
        .unwrap()
        .iter()
        .filter(|frame| frame["method"] == "start")
        .cloned()
        .collect()
}
fn writes(
    changed: &futures_channel::mpsc::UnboundedSender<()>,
    cx: &mut TestAppContext,
) -> (Entity<PrWrites>, gpui::Subscription) {
    owned_writes(
        PrOwner::Session {
            id: "session".into(),
            source: None,
        },
        changed,
        cx,
    )
}
fn owned_writes(
    owner: PrOwner,
    changed: &futures_channel::mpsc::UnboundedSender<()>,
    cx: &mut TestAppContext,
) -> (Entity<PrWrites>, gpui::Subscription) {
    let writes = cx.new(|cx| PrWrites::new(owner, cx));
    let sent = changed.clone();
    let heard = cx.update(|cx| {
        cx.observe(&writes, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    (writes, heard)
}
// Pins: g9#19
#[gpui::test]
async fn nothing_posts_before_confirm_and_an_uncertain_post_cannot_be_retried(
    cx: &mut TestAppContext,
) {
    let (backend, seen, changed, mut changes) = start_backend(
        "uncertain",
        json!({"error":"This may have posted. Refresh before trying again.","result":null}),
        cx,
    );
    let request = PostRequest::comment(identity(), "Exact comment".into()).unwrap();
    let (writes, _heard) = writes(&changed, cx);
    cx.run_until_parked();
    assert!(
        !seen
            .lock()
            .unwrap()
            .iter()
            .any(|frame| frame["t"] == "call")
    );
    writes
        .update(cx, |writes, cx| writes.post(&request, &identity(), cx))
        .unwrap();
    until("uncertain post", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| writes.read(cx).uncertain(cx))
    })
    .await;
    assert!(
        writes
            .update(cx, |writes, cx| writes.post(&request, &identity(), cx))
            .is_err()
    );
    assert!(
        writes
            .update(cx, |writes, cx| writes.merge(&merge(), cx))
            .is_err()
    );
    cx.run_until_parked();
    let starts = starts(&seen);
    assert_eq!(starts.len(), 1);
    assert_eq!(starts[0]["args"]["action"], "comment");
    assert_eq!(starts[0]["args"]["session"], "session");
    assert_eq!(starts[0]["args"]["args"]["body"], "Exact comment");
    assert_eq!(starts[0]["args"]["args"]["expectedHead"], "a".repeat(40));
    drop(backend);
}
// Pins: g9#19
#[gpui::test]
async fn reading_the_page_after_an_uncertain_write_is_what_allows_another(cx: &mut TestAppContext) {
    let (_backend, seen, changed, mut changes) = start_backend(
        "uncertain",
        json!({"error":"This may have merged.","result":null}),
        cx,
    );
    let (writes, _heard) = writes(&changed, cx);
    writes
        .update(cx, |writes, cx| writes.merge(&merge(), cx))
        .unwrap();
    until("uncertain merge", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| writes.read(cx).merge_uncertain(cx))
    })
    .await;
    assert_eq!(
        cx.update(|cx| writes.read(cx).merge_error(cx)).as_deref(),
        Some("This may have merged.")
    );
    assert_eq!(cx.update(|cx| writes.read(cx).post_error(cx)), None);
    writes.update(cx, |writes, cx| writes.page_read(cx));
    assert!(cx.update(|cx| writes.read(cx).free(cx)));
    assert_eq!(cx.update(|cx| writes.read(cx).merge_error(cx)), None);
    let request = PostRequest::comment(identity(), "Next".into()).unwrap();
    writes
        .update(cx, |writes, cx| writes.post(&request, &identity(), cx))
        .unwrap();
    until("second write", &mut changes, &changed, || {
        cx.run_until_parked();
        starts(&seen).len() == 2
    })
    .await;
}
// Pins: g9#19
#[gpui::test]
async fn a_merge_is_only_done_with_a_github_link_back(cx: &mut TestAppContext) {
    let url = "https://github.com/o/r/pull/7";
    let (_backend, _seen, changed, mut changes) =
        start_backend("succeeded", json!({"error":null,"result":url}), cx);
    let (writes, _heard) = writes(&changed, cx);
    let done = Arc::new(Mutex::new(Vec::<WriteDone>::new()));
    let kept = done.clone();
    let _done = cx.update(|cx| {
        cx.subscribe(&writes, move |_, event: &WriteDone, _| {
            kept.lock().unwrap().push(event.clone())
        })
    });
    writes
        .update(cx, |writes, cx| writes.merge(&merge(), cx))
        .unwrap();
    until("merge finished", &mut changes, &changed, || {
        cx.run_until_parked();
        !done.lock().unwrap().is_empty()
    })
    .await;
    assert_eq!(
        done.lock().unwrap().as_slice(),
        &[WriteDone::Merged { url: url.into() }]
    );
    assert!(cx.update(|cx| writes.read(cx).free(cx)));
}
// Pins: g9#19
#[gpui::test]
async fn success_without_a_github_link_is_not_confirmed_and_blocks_the_next_write(
    cx: &mut TestAppContext,
) {
    let (_backend, _seen, changed, mut changes) = start_backend(
        "succeeded",
        json!({"error":null,"result":"https://example.com/o/r/pull/7"}),
        cx,
    );
    let request = PostRequest::comment(identity(), "Exact".into()).unwrap();
    let (writes, _heard) = writes(&changed, cx);
    writes
        .update(cx, |writes, cx| writes.post(&request, &identity(), cx))
        .unwrap();
    until("unconfirmed post", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| writes.read(cx).uncertain(cx))
    })
    .await;
    assert!(!cx.update(|cx| writes.read(cx).free(cx)));
    assert_eq!(
        cx.update(|cx| writes.read(cx).post_error(cx)).as_deref(),
        Some("The post was not confirmed. Refresh before trying again.")
    );
}
fn project_owner() -> PrOwner {
    PrOwner::Project {
        id: "project".into(),
        source: ExpectedRowSource {
            machine: "engine".into(),
            generation: 3,
            route_machine: Some("lane".into()),
        },
        root: "/lane/r".into(),
    }
}
fn method(seen: &Mutex<Vec<Value>>, name: &str) -> Vec<Value> {
    (seen.lock().unwrap().iter())
        .filter(|frame| frame["method"] == name)
        .map(|frame| frame["args"].clone())
        .collect()
}
#[gpui::test]
async fn a_project_write_waits_for_a_read_then_names_the_project_and_no_session(
    cx: &mut TestAppContext,
) {
    let (_backend, seen, changed, mut changes) =
        start_backend("running", json!({"result":null}), cx);
    let (writes, _heard) = owned_writes(project_owner(), &changed, cx);
    assert!(
        writes
            .update(cx, |writes, cx| writes.merge(&merge(), cx))
            .is_err()
    );
    cx.run_until_parked();
    assert!(!seen.lock().unwrap().iter().any(|frame| frame["t"] == "call"));
    writes.update(cx, |writes, _| {
        writes.show_read("o/r".into(), "/read/root".into())
    });
    writes
        .update(cx, |writes, cx| writes.merge(&merge(), cx))
        .unwrap();
    until("the started merge", &mut changes, &changed, || {
        cx.run_until_parked();
        !method(&seen, "start").is_empty() && cx.update(|cx| {
            writes.read(cx).operation.read(cx).status.is_some()
        })
    })
    .await;
    let host = &method(&seen, "operation_host")[0];
    assert_eq!(host["session"], Value::Null);
    assert_eq!(host["projectId"], "project");
    assert_eq!(host["expectedSource"]["routeMachine"], "lane");
    assert!(host.get("expectedRepository").is_none());
    let start = &method(&seen, "start")[0];
    assert_eq!(start["session"], Value::Null);
    assert_eq!(start["action"], "merge");
    assert_eq!(start["projectId"], "project");
    assert_eq!(start["expectedRepository"], "o/r");
    assert_eq!(start["expectedCheckoutRoot"], "/lane/r");
    assert_eq!(start["expectedSource"]["machine"], "engine");
    writes.update(cx, |writes, cx| writes.cancel(cx));
    until("the cancel", &mut changes, &changed, || {
        cx.run_until_parked();
        !method(&seen, "cancel").is_empty()
    })
    .await;
    let cancel = &method(&seen, "cancel")[0];
    assert_eq!(cancel["session"], Value::Null);
    assert_eq!(cancel["projectId"], "project");
    assert_eq!(cancel["operationId"], start["operationId"]);
    assert_eq!(cancel["expectedSource"]["routeMachine"], "lane");
    assert!(cancel.get("expectedRepository").is_none());
    assert!(cancel.get("expectedCheckoutRoot").is_none());
}
#[gpui::test]
async fn a_session_write_sends_back_the_source_and_root_its_read_showed(cx: &mut TestAppContext) {
    let (_backend, seen, changed, mut changes) =
        start_backend("running", json!({"result":null}), cx);
    let source = ExpectedRowSource {
        machine: "box".into(),
        generation: 0,
        route_machine: None,
    };
    let owner = PrOwner::Session {
        id: "session".into(),
        source: Some(source),
    };
    let (writes, _heard) = owned_writes(owner, &changed, cx);
    writes.update(cx, |writes, _| {
        writes.show_read("o/r".into(), "/read/root".into())
    });
    writes
        .update(cx, |writes, cx| writes.merge(&merge(), cx))
        .unwrap();
    until("the started merge", &mut changes, &changed, || {
        cx.run_until_parked();
        !method(&seen, "start").is_empty()
    })
    .await;
    let start = &method(&seen, "start")[0];
    assert_eq!(start["session"], "session");
    assert_eq!(start["expectedCheckoutRoot"], "/read/root");
    assert_eq!(start["expectedSource"]["machine"], "box");
    assert!(start.get("projectId").is_none());
}
