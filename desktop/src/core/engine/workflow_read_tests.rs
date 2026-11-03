//! Actual socket transport fixtures; no provider/native execution is claimed.
use gpui::{AppContext as _,TestAppContext};
use futures_channel::mpsc::unbounded;
use serde_json::json;
use crate::core::socket::{client::SocketClient,endpoint::Endpoint,fake_backend::{FakeBackend,Reply},test_wait::until};
use super::{workflow_read,workflow_items_tests::{run,step}};
#[gpui::test]
async fn workflow_pages_use_one_bulk_connection_and_keep_definition_order(cx:&mut TestAppContext) {
    let backend=FakeBackend::start(|frame|match frame["t"].as_str(){
        Some("hello")=>vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"friendly","engine":"owner"}))],
        Some("call")=>{
            assert_eq!(frame["feature"],"workflow");assert_eq!(frame["method"],"run");assert_eq!(frame["args"]["machine"],"run-owner");
            let mut value=step();let next=if frame["args"]["after"].is_null(){Some("opaque-original")}else{assert_eq!(frame["args"]["after"],"opaque-original");value["id"]=json!("step-a");None};
            vec![Reply::Frame(json!({"t":"result","id":frame["id"],"result":{"run":run(),"steps":[value],"next":next}}))]
        },_=>vec![]
    });
    cx.executor().allow_parking();
    let socket=cx.update(|cx|SocketClient::start_bulk(Endpoint{port:backend.port,token:"secret".into()},cx));
    let(changed,mut changes)=unbounded();let tell=changed.clone();
    let _watch=cx.update(|cx|cx.observe(&socket,move|_,_|{let _=tell.unbounded_send(());}));
    until("bulk welcomed",&mut changes,&changed,||cx.update(|cx|socket.read(cx).connection_generation().is_some())).await;
    assert_eq!(backend.next()["t"],"hello");
    let owner=cx.update(|cx|cx.new(|_|()));
    let read=cx.update(|cx|owner.update(cx,|_,cx|cx.spawn(async move|_,cx|workflow_read::run("run-owner","run",cx).await)));
    let result=read.await.unwrap();assert_eq!(result.steps.iter().map(|step|step.id.as_str()).collect::<Vec<_>>(),["step-z","step-a"]);
    for _ in 0..3 {let(path,frame)=backend.on.recv_timeout(crate::core::socket::test_client::DEADLINE).unwrap();assert_eq!(path,"/socket/bulk");assert!(matches!(frame["t"].as_str(),Some("hello"|"call")));}
    assert_eq!(backend.next()["args"]["after"],serde_json::Value::Null);assert_eq!(backend.next()["args"]["after"],"opaque-original");
}
#[gpui::test]
async fn dropping_the_workflow_read_cancels_the_actual_held_page(cx:&mut TestAppContext) {
    let backend=FakeBackend::start(|frame|if frame["t"]=="hello"{vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"friendly","engine":"owner"}))]}else{vec![]});
    cx.executor().allow_parking();let socket=cx.update(|cx|SocketClient::start_bulk(Endpoint{port:backend.port,token:"secret".into()},cx));
    let(changed,mut changes)=unbounded();let tell=changed.clone();
    let _watch=cx.update(|cx|cx.observe(&socket,move|_,_|{let _=tell.unbounded_send(());}));
    until("bulk welcomed",&mut changes,&changed,||cx.update(|cx|socket.read(cx).connection_generation().is_some())).await;assert_eq!(backend.next()["t"],"hello");
    let owner=cx.update(|cx|cx.new(|_|()));let read=cx.update(|cx|owner.update(cx,|_,cx|cx.spawn(async move|_,cx|workflow_read::run("run-owner","run",cx).await)));
    cx.run_until_parked();let call=backend.next();assert_eq!(call["method"],"run");drop(read);cx.run_until_parked();
    assert_eq!(backend.next(),json!({"t":"cancel","id":call["id"]}));
}
