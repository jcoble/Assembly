use super::super::review_draft::{ReviewDraft, ReviewIdentity};
use super::{PostConfirm, PostRequest};
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
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
#[test]
fn changed_head_refuses_confirmation_and_the_exact_review_targets_remain_in_the_preview() {
    let mut draft = ReviewDraft::new(identity());
    draft.body = "  Exact review  ".into();
    let id = draft
        .start_comment(&gpui_kit::component::diff::DiffLinePosition::new(
            "ü.rs",
            gpui_kit::component::diff::DiffSide::Original,
            9,
        ))
        .unwrap();
    draft.update_comment(&id, " Exact line ".into());
    let request = PostRequest::review(&draft, &identity()).unwrap();
    assert_eq!(request.body(), "Exact review");
    assert_eq!(request.comments()[0].path, "ü.rs");
    assert_eq!(request.comments()[0].side, "LEFT");
    assert_eq!(request.comments()[0].line, 9);
    assert_eq!(request.comments()[0].body, "Exact line");
    let mut moved = identity();
    moved.head = "c".repeat(40);
    assert!(request.args(&moved).is_err());
    assert_eq!(draft.comments[0].target.body, " Exact line ");
}
#[gpui::test]
async fn nothing_posts_before_confirm_and_an_uncertain_post_cannot_be_retried(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
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
            json!({"operationId":frame["args"]["operationId"],"instanceId":"owned","machine":"machine","version":2,"state":"uncertain","error":"This may have posted. Refresh before trying again.","result":null})
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
    let request = PostRequest::comment(identity(), "Exact comment".into()).unwrap();
    let confirm = cx.new(|cx| PostConfirm::new("session".into(), request, None, cx));
    cx.run_until_parked();
    assert!(
        !seen
            .lock()
            .unwrap()
            .iter()
            .any(|frame| frame["t"] == "call")
    );
    let sent = changed.clone();
    let _heard = cx.update(|cx| {
        cx.observe(&confirm, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    confirm.update(cx, |confirm, cx| confirm.confirm(&identity(), cx));
    until("uncertain post", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| confirm.read(cx).uncertain(cx))
    })
    .await;
    confirm.update(cx, |confirm, cx| confirm.confirm(&identity(), cx));
    cx.run_until_parked();
    let seen = seen.lock().unwrap();
    let starts: Vec<_> = seen
        .iter()
        .filter(|frame| frame["method"] == "start")
        .collect();
    assert_eq!(starts.len(), 1);
    assert_eq!(starts[0]["args"]["action"], "comment");
    assert_eq!(starts[0]["args"]["session"], "session");
    assert_eq!(starts[0]["args"]["args"]["body"], "Exact comment");
    assert_eq!(starts[0]["args"]["args"]["expectedHead"], "a".repeat(40));
}
