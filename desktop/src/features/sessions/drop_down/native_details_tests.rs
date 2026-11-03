//! Source-derived descriptor/watch fixtures, never native transcript hydration or provider execution proof.
use std::sync::{Arc,Mutex};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use serde_json::{Value,json};
use crate::core::{engine::SessionKey,socket::{client::SocketClient,endpoint::Endpoint,fake_backend::{FakeBackend,Reply},outcome::Outcome,test_wait::until}};
use super::NativeDetails;
#[derive(Default)]
struct Seen {held:Option<Reply>,calls:Vec<Value>,subs:usize,row_sub:Option<Value>}
#[gpui::test]
async fn native_watch_waits_for_the_checked_parent_descriptor_and_stale_card_actions_never_dispatch(cx:&mut TestAppContext) {
    let seen=Arc::new(Mutex::new(Seen::default()));let record=seen.clone();let(changed,mut changes)=unbounded();let tell=changed.clone();
    let backend=fixture(record,tell);

    cx.executor().allow_parking();cx.update(gpui_kit::init);
    let(main,bulk)=cx.update(|cx|{let endpoint=Endpoint{port:backend.port,token:"fixture".into()};(SocketClient::start(endpoint.clone(),cx),SocketClient::start_bulk(endpoint,cx))});
    let connected=changed.clone();let ready=changed.clone();
    let _main=cx.update(|cx|cx.observe(&main,move|_,_|{let _=connected.unbounded_send(());}));
    let _bulk=cx.update(|cx|cx.observe(&bulk,move|_,_|{let _=ready.unbounded_send(());}));
    until("native connections",&mut changes,&changed,||cx.update(|cx|main.read(cx).connection_generation().is_some()&&bulk.read(cx).connection_generation().is_some())).await;
    let(details,visual)=cx.add_window_view(|_,cx|NativeDetails::new(SessionKey{machine:"actual-owner".into(),id:"parent".into()},None,cx));
    let updated=changed.clone();let _details=visual.update(|_,cx|cx.observe(&details,move|_,_|{let _=updated.unbounded_send(());}));
    until("descriptor held",&mut changes,&changed,||seen.lock().unwrap().held.is_some()).await;
    assert_eq!(seen.lock().unwrap().subs,0,"SQL source hints never admit a native watch");
    let call=visual.update(|_,cx|main.read(cx).call::<Value>("fixture","release",json!({}),None));assert!(matches!(call.await,Outcome::Ok(_)));
    until("native complete page",&mut changes,&changed,||details.read_with(visual,|details,_|details.watch.as_ref().is_some_and(|watch|watch.loaded()))).await;
    details.update_in(visual,|details,window,cx| {
        details.refresh(cx);
        // UI's old seq and contradictory persisted payload correlation both refuse before any command/action.
        assert!(details.card("subagent:provider-child",6,"parent",cx).is_none());
        assert!(details.card("subagent:provider-child",7,"invented-child",cx).is_none());
        assert!(!details.allow_stop,"historical journal admission grants no native Stop authority");
        details.stop("subagent:provider-child",7,"parent",cx);
        details.open("subagent:provider-child",6,"parent",window,cx);
    });
    assert!(!seen.lock().unwrap().calls.iter().any(|frame|frame["feature"]=="send"));
}

fn fixture(record:Arc<Mutex<Seen>>,tell:futures_channel::mpsc::UnboundedSender<()>)->FakeBackend {
    FakeBackend::start(move|frame| {
        let mut seen=record.lock().unwrap();seen.calls.push(frame.clone());let _=tell.unbounded_send(());
        let id=frame["id"].clone();let args=&frame["args"];
        let result=|value|Reply::Frame(json!({"t":"result","id":id,"result":value}));
        match(frame["t"].as_str(),frame["feature"].as_str(),frame["method"].as_str(),frame["topic"].as_str()) {
            (Some("hello"),..)=>vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"friendly","engine":"actual-owner"}))],
            (Some("call"),Some("sessions"),Some("resolve_source"),_)=>vec![result(json!({"machine":"actual-owner","generation":0,"routeMachine":null}))],
            (Some("call"),Some("sessions"),Some("open_target"),_)=>{seen.held=Some(result(json!({"kind":args["kind"],"machine":"actual-owner","generation":0,"routeMachine":null,"sessionId":"parent","parentSessionId":null,"itemId":null,"seq":null,"payloadSessionId":null,"readOnly":args["kind"]!="engine","hasHistoryRows":true,"originalKey":{"machine":"actual-owner","id":"parent"},"requestId":args["requestId"],"checkoutRoot":null,"expectedSource":{"machine":"actual-owner","generation":0,"routeMachine":null},"transcriptOnly":true})));vec![]},
            (Some("call"),Some("fixture"),Some("release"),_)=>vec![seen.held.take().unwrap(),result(Value::Null)],
            (Some("sub"),_,_,Some("sessions")) if frame["stream"]=="engine"=>vec![Reply::Frame(json!({"t":"engine_snapshot","id":id,"machine":"actual-owner","through":1,"items":[{"entityKind":"session","id":"parent","machine":"actual-owner","version":1,"provider":"fixture","folder":"/display","state":"idle","activityState":"idle","epoch":1}],"more":false}))],
            (Some("call"),Some("send"),Some("stopTask"),_)=>vec![result(Value::Null)],
            (Some("call"),Some("fixture"),Some("invalidate"),_)=>vec![Reply::Frame(json!({"t":"error","id":seen.row_sub.clone().unwrap(),"code":"source_changed","message":"Actual source replaced"})),result(Value::Null)],
            (Some("sub"),_,_,Some("rows"))=>{seen.subs+=1;seen.row_sub=Some(id.clone());vec![Reply::Frame(json!({"t":"state","sub":id,"seq":7,"items":[]}))]},
            (Some("call"),Some("rows"),Some("subagents"),_)=>vec![result(json!({"items":[{"itemId":"subagent:provider-child","seq":7,"sessionId":"parent","title":"Actual native display","description":null,"state":"running","stopReason":null,"error":null,"toolCallId":null,"canStop":true}],"next":null}))],
            _=>vec![],
        }
    })
}

#[gpui::test]
async fn writable_checked_parent_stops_only_the_persisted_child_and_replacement_disables_old_actions(cx:&mut TestAppContext) {
    use crate::core::engine::{EngineFeed,EngineScope};
    let seen=Arc::new(Mutex::new(Seen::default()));let(changed,mut changes)=unbounded();let backend=fixture(seen.clone(),changed.clone());
    cx.executor().allow_parking();cx.update(gpui_kit::init);
    let(main,bulk,feed)=cx.update(|cx|{let endpoint=Endpoint{port:backend.port,token:"fixture".into()};(SocketClient::start(endpoint.clone(),cx),SocketClient::start_bulk(endpoint,cx),EngineFeed::global(cx))});
    let connected=changed.clone();let ready=changed.clone();let refreshed=changed.clone();
    let _main=cx.update(|cx|cx.observe(&main,move|_,_|{let _=connected.unbounded_send(());}));
    let _bulk=cx.update(|cx|cx.observe(&bulk,move|_,_|{let _=ready.unbounded_send(());}));
    let _feed=cx.update(|cx|cx.observe(&feed,move|_,_|{let _=refreshed.unbounded_send(());}));
    until("native connections",&mut changes,&changed,||cx.update(|cx|main.read(cx).connection_generation().is_some()&&bulk.read(cx).connection_generation().is_some())).await;
    let scope=EngineScope{machine:"actual-owner".into(),topic:"sessions".into(),key:None};
    let _lease=cx.update(|cx|feed.update(cx,|feed,cx|feed.watch(scope.clone(),cx).unwrap()));
    until("actual parent display",&mut changes,&changed,||cx.update(|cx|feed.read(cx).loaded(&scope))).await;
    let(details,visual)=cx.add_window_view(|_,cx|NativeDetails::new(SessionKey{machine:"actual-owner".into(),id:"parent".into()},None,cx));
    let updated=changed.clone();let _details=visual.update(|_,cx|cx.observe(&details,move|_,_|{let _=updated.unbounded_send(());}));
    until("descriptor held",&mut changes,&changed,||seen.lock().unwrap().held.is_some()).await;
    assert_eq!(seen.lock().unwrap().subs,0);
    let call=visual.update(|_,cx|main.read(cx).call::<Value>("fixture","release",json!({}),None));assert!(matches!(call.await,Outcome::Ok(_)));
    until("actual native page",&mut changes,&changed,||details.read_with(visual,|details,_|details.watch.as_ref().is_some_and(|watch|watch.loaded()))).await;
    details.update(visual,|details,cx|{assert!(details.allow_stop);details.stop("subagent:provider-child",7,"parent",cx);});
    until("exact Stop dispatch",&mut changes,&changed,||seen.lock().unwrap().calls.iter().any(|call|call["feature"]=="send")).await;
    let sent=seen.lock().unwrap().calls.iter().find(|call|call["feature"]=="send").unwrap()["args"].clone();
    assert_eq!(sent,json!({"session":"parent","task":"provider-child","expectedSource":{"machine":"actual-owner","generation":0,"routeMachine":null}}));
    let call=visual.update(|_,cx|main.read(cx).call::<Value>("fixture","invalidate",json!({}),None));assert!(matches!(call.await,Outcome::Ok(_)));
    until("source invalidated",&mut changes,&changed,||details.read_with(visual,|details,_|details.watch.as_ref().is_some_and(|watch|!watch.loaded()))).await;
    details.update(visual,|details,cx|{assert!(details.card("subagent:provider-child",7,"parent",cx).is_none());details.stop("subagent:provider-child",7,"parent",cx);});
    assert_eq!(seen.lock().unwrap().calls.iter().filter(|call|call["feature"]=="send").count(),1,"no stale command or unqualified retry after replacement");
}
