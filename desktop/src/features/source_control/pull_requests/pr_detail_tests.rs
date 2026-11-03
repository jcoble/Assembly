use super::{PrDetail, PrOwner};
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
#[gpui::test]
async fn detail_is_a_bulk_read_and_a_different_repository_cannot_replace_the_requested_target(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let sent = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return vec![];
        }
        observed.lock().unwrap().push(frame.clone());
        let _ = sent.unbounded_send(());
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"repository":"other/repository","localRoot":"/fixture","number":7,"title":"Wrong repository","body":"","bodyBlocks":[],"url":"https://github.com/other/repository/pull/7","author":"author","headBranch":"feature","baseBranch":"main","headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"","createdAt":"","mergedAt":"","mergedBy":"","closedAt":"","commits":[],"comments":[],"reviews":[],"reviewers":[],"checks":[],"files":[],"moreFiles":false,"timeline":[]}}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start_bulk(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let detail = cx.new(|cx| {
        PrDetail::new(
            PrOwner::Session {
                id: "actual-session".into(),
                source: None,
            },
            Some("expected/repository".into()),
            7,
            false,
            cx,
        )
    });
    cx.run_until_parked();
    assert!(seen.lock().unwrap().is_empty());
    let sent = changed.clone();
    let _observe = cx.update(|cx| {
        cx.observe(&detail, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    detail.update(cx, |detail, cx| detail.set_active(true, cx));
    until("repository refusal", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| detail.read(cx).failure.is_some())
    })
    .await;
    cx.update(|cx| assert!(detail.read(cx).detail.is_none()));
    let seen = seen.lock().unwrap();
    assert_eq!(seen.len(), 1);
    assert_eq!(seen[0]["feature"], "git");
    assert_eq!(seen[0]["method"], "pr");
    assert_eq!(seen[0]["args"]["session"], "actual-session");
}
