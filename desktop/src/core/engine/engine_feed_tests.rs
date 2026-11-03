//! Source-derived socket fixtures; no provider or native acceptance is claimed.
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use serde_json::json;
use super::{EngineFeed, EngineScope};
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply};
use crate::core::socket::test_wait::until;

#[gpui::test]
async fn two_consumers_share_a_scope_and_the_last_drop_unsubscribes(cx: &mut TestAppContext) {
    let backend = FakeBackend::start(|frame| {
        match frame["t"].as_str() {
            Some("hello") => vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"friendly","engine":"owner"}))],
            Some("sub") => vec![Reply::Frame(json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner",
                "through":1,"items":[],"more":false}))],
            Some("call") => vec![Reply::Frame(json!({"t":"result","id":frame["id"],"result":{}}))],
            _ => vec![],
        }
    });
    cx.executor().allow_parking();
    let socket = cx.update(|cx| SocketClient::start(Endpoint { port:backend.port, token:"secret".into() }, cx));
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|cx| cx.observe(&socket, move |_, _| { let _ = tell.unbounded_send(()); }));
    until("connected", &mut changes, &changed, || cx.update(|cx| {
        matches!(socket.read(cx).state(), ConnectionState::Connected { .. })
    })).await;
    assert_eq!(backend.next()["t"], "hello");
    let scope = EngineScope { machine:"owner".into(), topic:"sessions".into(), key:None };
    let feed = cx.update(EngineFeed::global);
    let (first, second) = cx.update(|cx| feed.update(cx, |feed, cx| {
        (feed.watch(scope.clone(), cx).unwrap(), feed.watch(scope.clone(), cx).unwrap())
    }));
    let tell = changed.clone();
    let _feed_heard = cx.update(|cx| cx.observe(&feed, move |_, _| { let _ = tell.unbounded_send(()); }));
    until("complete scope", &mut changes, &changed, || cx.update(|cx| feed.read(cx).loaded(&scope))).await;
    let sub = backend.next();
    assert_eq!(sub["t"], "sub");
    assert_eq!(backend.next()["t"], "ack");
    drop(first);
    let call = cx.update(|cx| socket.read(cx).call::<serde_json::Value>("test", "barrier", json!({}), None));
    cx.run_until_parked();
    assert_eq!(backend.next()["t"], "call", "the shared scope is still subscribed");
    let _ = call.await;
    drop(second);
    cx.run_until_parked();
    assert_eq!(backend.next(), json!({"t":"unsub","id":sub["id"]}));
}
