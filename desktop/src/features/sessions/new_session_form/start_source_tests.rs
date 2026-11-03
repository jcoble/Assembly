use super::{StartSource,SocketClient,capture};
use crate::core::socket::{endpoint::Endpoint,fake_backend::{FakeBackend,Reply},test_wait::until};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use serde_json::json;
use std::sync::{Arc,Mutex};
#[gpui::test]
async fn source_discovery_names_local_explicitly_and_rejects_a_replaced_bulk_lifetime(cx:&mut TestAppContext) {
    let (changed,mut changes)=unbounded();let sent=changed.clone();
    let seen=Arc::new(Mutex::new(Vec::<serde_json::Value>::new()));let observed=seen.clone();let mut held=None;
    let backend=FakeBackend::start(move|frame| {
        if frame["t"]=="hello" {return vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"fixture-host","engine":"box"}))];}
        observed.lock().unwrap().push(frame.clone());let _=sent.unbounded_send(());
        if frame["method"]=="resolve_source" {assert_eq!(frame["args"],json!({"routeMachine":null}));held=Some(frame["id"].clone());return vec![];}
        if frame["method"]=="release" {return vec![Reply::Frame(json!({"t":"result","id":held.take().unwrap(),"result":{"machine":"box","generation":0,"routeMachine":null}})),Reply::Frame(json!({"t":"result","id":frame["id"],"result":null}))];}
        vec![]
    });
    cx.executor().allow_parking();cx.update(|cx|{gpui_kit::init(cx);SocketClient::start(Endpoint{port:backend.port,token:"secret".into()},cx);SocketClient::start_bulk(Endpoint{port:backend.port,token:"secret".into()},cx);});
    let _connections=cx.update(|cx| {
        [SocketClient::global(cx),SocketClient::try_bulk(cx).unwrap()].iter().map(|socket| {
            let sent=changed.clone();cx.observe(socket,move|_,_|{let _=sent.unbounded_send(());})
        }).collect::<Vec<_>>()
    });
    until("both source connections",&mut changes,&changed,||{cx.run_until_parked();cx.update(|cx|capture(cx).is_ok())}).await;
    let pending=cx.update(|cx|StartSource::resolve(None,cx));
    until("held current source request",&mut changes,&changed,||{cx.run_until_parked();seen.lock().unwrap().iter().any(|f|f["method"]=="resolve_source")}).await;
    cx.update(|cx|{SocketClient::start_bulk(Endpoint{port:backend.port,token:"secret".into()},cx);});
    let release=cx.update(|cx|SocketClient::global(cx).read(cx).call::<serde_json::Value>("fixture","release",json!({}),None));
    let _=release.await;
    assert!(pending.await.is_err());
    assert!(!seen.lock().unwrap().iter().any(|f|f["method"]=="create"));
}
