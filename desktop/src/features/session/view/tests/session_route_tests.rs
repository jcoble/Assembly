//! Existing fake sockets prove one supersession lifetime; route discovery is not history provenance.
use std::{cell::RefCell,rc::Rc,sync::{Arc,Mutex}};
use futures_channel::{mpsc::unbounded,oneshot};
use gpui::TestAppContext;
use serde_json::{Value,json};
use super::SessionPane;
use crate::core::{engine::SessionKey,intents::session_open::{SessionOpenRequest,SessionOpenResult,SessionOpenTarget},state::selected_session::SelectedSession,socket::{client::SocketClient,endpoint::Endpoint,fake_backend::{FakeBackend,Reply},test_wait::until}};
use crate::features::session::client::socket_session_client::SocketSessionClient;
async fn supersede_with(use_route:bool,cx:&mut TestAppContext){
    let(changed,mut changes)=unbounded();let tell=changed.clone();let(held,seen_held)=oneshot::channel();let held=Arc::new(Mutex::new(Some(held)));
    let seen=Arc::new(Mutex::new(Vec::<Value>::new()));let recorded=seen.clone();let mut first:Option<Value>=None;
    let backend=FakeBackend::start(move|frame|{
        recorded.lock().unwrap().push(frame.clone());let _=tell.unbounded_send(());let id=&frame["id"];
        match(frame["t"].as_str(),frame["method"].as_str()){
            (Some("hello"),_)=>vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"box","engine":"actual-local"}))],
            (Some("call"),Some("resolve_source"))=>{
                if first.is_none(){first=Some(id.clone());if let Some(held)=held.lock().unwrap().take(){let _=held.send(());}vec![]}
                else{vec![Reply::Frame(json!({"t":"result","id":id,"result":{"machine":"actual-new","generation":9,"routeMachine":"new saved route"}}))]}
            },
            (Some("cancel"),_)if first.as_ref()==Some(id)=>vec![Reply::Frame(json!({"t":"result","id":id,"result":{"machine":"actual-local","generation":0,"routeMachine":null}}))],
            (Some("call"),Some("open_target"))=>vec![Reply::Frame(json!({"t":"error","id":id,"code":"unknown_session","message":"Fixture target missing."}))],
            _=>vec![],
        }
    });
    cx.executor().allow_parking();
    let(main,bulk)=cx.update(|cx|{
        gpui_kit::init(cx);SelectedSession::init(cx);let endpoint=Endpoint{port:backend.port,token:"secret".into()};
        (SocketClient::start(endpoint.clone(),cx),SocketClient::start_bulk(endpoint,cx))
    });
    let ready=changed.clone();let ready_bulk=changed.clone();
    let _main=cx.update(|cx|cx.observe(&main,move|_,_|{let _=ready.unbounded_send(());}));let _bulk=cx.update(|cx|cx.observe(&bulk,move|_,_|{let _=ready_bulk.unbounded_send(());}));
    until("both route-discovery sockets",&mut changes,&changed,||cx.update(|cx|main.read(cx).connection_generation().is_some()&&bulk.read(cx).connection_generation().is_some())).await;
    let(pane,cx)=cx.add_window_view(|window,cx|SessionPane::new(Rc::new(SocketSessionClient),window,cx));
    let results=Rc::new(RefCell::new(Vec::new()));let got=results.clone();let result_tell=changed.clone();
    let _result=cx.update(|_,cx|cx.subscribe(&SelectedSession::global(cx),move|_,event:&SessionOpenResult,_|{got.borrow_mut().push(event.clone());let _=result_tell.unbounded_send(());}));
    pane.update_in(cx,|pane,window,cx|pane.request_current_route(None,"old".into(),"old-request".into(),SessionOpenTarget::Historical,window,cx));
    seen_held.await.expect("actual first resolve_source call entered");
    pane.update_in(cx,|pane,window,cx|{
        if use_route {pane.request_current_route(Some("new saved route".into()),"new".into(),"new-request".into(),SessionOpenTarget::Historical,window,cx);}
        else {pane.request_open(SessionOpenRequest{key:SessionKey{machine:"actual-new".into(),id:"new".into()},request_id:"new-request".into(),target:SessionOpenTarget::Historical},window,cx);}
    });
    until("both exactly correlated terminal results",&mut changes,&changed,||results.borrow().len()==2).await;
    cx.run_until_parked();
    let results=results.borrow();let old=results.iter().find(|result|result.request_id=="old-request").unwrap();let new=results.iter().find(|result|result.request_id=="new-request").unwrap();
    assert!(old.key.is_none());assert_eq!(old.result.as_ref().unwrap_err().code,"cancelled");
    assert_eq!(new.key,Some(SessionKey{machine:"actual-new".into(),id:"new".into()}));assert_eq!(new.result.as_ref().unwrap_err().code,"unknown_session");
    let seen=seen.lock().unwrap();let targets:Vec<_>=seen.iter().filter(|frame|frame["method"]=="open_target").collect();
    assert_eq!(targets.len(),1,"cancelled discovery cannot start or overwrite a newer target phase");assert_eq!(targets[0]["args"]["session"],"new");assert_eq!(targets[0]["args"]["requestId"],2);
    let discovery=seen.iter().find(|frame|frame["method"]=="resolve_source").unwrap();assert_eq!(discovery["args"],json!({"routeMachine":null}));
    assert!(seen.iter().any(|frame|frame["t"]=="cancel"));
}
#[gpui::test]
async fn a_late_held_discovery_cannot_overwrite_a_new_actual_source_request(cx:&mut TestAppContext){supersede_with(false,cx).await;}
#[gpui::test]
async fn a_late_held_discovery_cannot_overwrite_a_new_route_discovery(cx:&mut TestAppContext){supersede_with(true,cx).await;}

#[test]
fn only_recorded_nonresumable_engine_states_are_classified_as_historical() {
    use super::opening::online_historical;
    for state in ["ended","lost","moved","handing_off"] { assert!(online_historical(state)); }
    for state in ["running","idle","starting","", "unknown"] { assert!(!online_historical(state)); }
}
