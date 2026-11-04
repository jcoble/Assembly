//! Real socket + sole EngineFeed fixtures; these prove UI command/lifetime semantics, not provider execution.
use std::sync::{Arc,Mutex};
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use futures_channel::mpsc::unbounded;
use serde_json::{Value,json};
use crate::core::{engine::{EngineFeed,EngineKey,EngineScope,commands::NeedAnswer},socket::{client::SocketClient,endpoint::Endpoint,fake_backend::{FakeBackend,Reply},outcome::Outcome,test_wait::until}};
use super::{NeedRow,NeedRowState};
struct Server {need:Value,commands:Vec<Value>,held:Option<Reply>,subscription:Option<Value>}
#[gpui::test]
async fn conflict_uncertainty_and_source_refusal_keep_the_draft_and_never_replay_a_command(cx:&mut TestAppContext) {
    let need=json!({"entityKind":"needs_me","id":"question","machine":"owner","version":3,"sessionId":"parent","kind":"question","state":"open","text":"Actual supplied question","inputMode":"text","options":[],"freeText":true,"displayNumber":1});
    let state=Arc::new(Mutex::new(Server{need,commands:Vec::new(),held:None,subscription:None}));let server=state.clone();let(changed,mut changes)=unbounded();let tell=changed.clone();
    let backend=FakeBackend::start(move|frame|{
        let mut state=server.lock().unwrap();let _=tell.unbounded_send(());let id=frame["id"].clone();let args=&frame["args"];
        let result=|value|Reply::Frame(json!({"t":"result","id":id,"result":value}));
        match(frame["t"].as_str(),frame["feature"].as_str(),frame["method"].as_str()) {
            (Some("hello"),..)=>vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"Friendly","engine":"owner"}))],
            (Some("sub"),_,_) if frame["stream"]=="engine"=>{
                let items=if frame["topic"]=="needs_me"{state.subscription=Some(id.clone());vec![state.need.clone()]}else{vec![json!({"entityKind":"session","machine":"owner","id":"parent","version":1,"provider":"fixture","folder":"/display","state":"idle","activityState":"idle","epoch":9})]};
                vec![Reply::Frame(json!({"t":"engine_snapshot","id":id,"machine":"owner","through":state.need["version"],"items":items,"more":false}))]
            },
            (Some("call"),Some("engine"),Some("command"))=>{
                state.commands.push(args.clone());let count=state.commands.len();
                if count==1 {
                    state.held=Some(result(json!({"applied":false,"late":false,"error":"version_conflict","version":4,"seq":4,"epoch":9})));vec![]
                }else if count==2{vec![Reply::Frame(json!({"t":"error","id":id,"code":"uncertain","machine":"owner","message":"Actual fixture outcome unknown"}))]}
                else{state.need["version"]=json!(5);state.need["state"]=json!("answered");state.need["answer"]=args["command"]["answer"].clone();state.need["answerText"]=args["command"]["answer"].clone();state.need["answerComplete"]=json!(true);state.need["answeredOrigin"]=json!({"channel":"desktop","surface":"rail"});vec![result(json!({"applied":true,"late":false,"version":5,"seq":5,"epoch":9}))]}
            },
            (Some("call"),Some("engine"),Some("entities"))=>vec![result(json!({"items":[state.need.clone()],"next":null}))],
            (Some("call"),Some("fixture"),Some("release"))=>{state.need["version"]=json!(4);vec![state.held.take().unwrap(),result(Value::Null)]},
            (Some("call"),Some("fixture"),Some("invalidate"))=>vec![Reply::Frame(json!({"t":"error","id":state.subscription.clone().unwrap(),"code":"source_changed","message":"Actual owning scope replaced"})),result(Value::Null)],
            _=>vec![],
        }
    });
    cx.executor().allow_parking();cx.update(gpui_kit::init);
    let(main,bulk,feed)=cx.update(|cx|{let endpoint=Endpoint{port:backend.port,token:"fixture".into()};(SocketClient::start(endpoint.clone(),cx),SocketClient::start_bulk(endpoint,cx),EngineFeed::global(cx))});
    let connected=changed.clone();let ready=changed.clone();let refreshed=changed.clone();
    let _main=cx.update(|cx|cx.observe(&main,move|_,_|{let _=connected.unbounded_send(());}));
    let _bulk=cx.update(|cx|cx.observe(&bulk,move|_,_|{let _=ready.unbounded_send(());}));
    let _feed=cx.update(|cx|cx.observe(&feed,move|_,_|{let _=refreshed.unbounded_send(());}));
    until("both answer sockets",&mut changes,&changed,||cx.update(|cx|main.read(cx).connection_generation().is_some()&&bulk.read(cx).connection_generation().is_some())).await;
    let scope=EngineScope{machine:"owner".into(),topic:"needs_me".into(),key:None};let sessions=EngineScope{topic:"sessions".into(),..scope.clone()};
    let(mut needs,_sessions)=cx.update(|cx|feed.update(cx,|feed,cx|(Some(feed.watch(scope.clone(),cx).unwrap()),feed.watch(sessions.clone(),cx).unwrap())));
    until("coherent question and epoch",&mut changes,&changed,||cx.update(|cx|feed.read(cx).loaded(&scope)&&feed.read(cx).loaded(&sessions))).await;
    let key=EngineKey{machine:"owner".into(),kind:"needs_me".into(),id:"question".into()};
    let(row,visual)=cx.add_window_view(|window,cx|{let need=feed.read(cx).need(&key).unwrap();NeedRow::build(key.clone(),scope.clone(),feed.clone(),"board",need,window,cx)});
    let updated=changed.clone();let _row=visual.update(|_,cx|cx.observe(&row,move|_,_|{let _=updated.unbounded_send(());}));
    row.update_in(visual,|row,window,cx|{row.open(window,cx);row.text.update(cx,|text,cx|text.set_value("  exact draft\n",window,cx));row.answer(NeedAnswer::Answer(json!(row.text.read(cx).value().to_string())),cx);});
    until("held command",&mut changes,&changed,||state.lock().unwrap().held.is_some()).await;
    row.update(visual,|row,cx|{assert!(matches!(row.state,NeedRowState::Sending));row.answer(NeedAnswer::Answer(json!("must not send twice")),cx);});
    assert_eq!(state.lock().unwrap().commands.len(),1);
    let call=visual.update(|_,cx|main.read(cx).call::<Value>("fixture","release",json!({}),None));assert!(matches!(call.await,Outcome::Ok(_)));
    until("conflict reread preserves open editor",&mut changes,&changed,||row.read_with(visual,|row,_|row.reconciled&&row.need.version==4&&matches!(row.state,NeedRowState::Open))).await;
    row.read_with(visual,|row,cx|assert_eq!(row.text.read(cx).value().as_ref(),"  exact draft\n"));
    row.update(visual,|row,cx|row.answer(NeedAnswer::Answer(json!(row.text.read(cx).value().to_string())),cx));
    until("unknown outcome reconciled without replay",&mut changes,&changed,||row.read_with(visual,|row,_|row.reconciled&&matches!(row.state,NeedRowState::Failed{..}))).await;
    visual.run_until_parked();assert_eq!(state.lock().unwrap().commands.len(),2);
    let call=visual.update(|_,cx|main.read(cx).call::<Value>("fixture","invalidate",json!({}),None));assert!(matches!(call.await,Outcome::Ok(_)));
    until("owning source disables controls",&mut changes,&changed,||row.read_with(visual,|row,_|!row.available)).await;
    row.update(visual,|row,cx|row.answer(NeedAnswer::Answer(json!("refused source")),cx));assert_eq!(state.lock().unwrap().commands.len(),2);
    drop(needs.take());needs=Some(visual.update(|_,cx|feed.update(cx,|feed,cx|feed.watch(scope.clone(),cx).unwrap())));
    until("fresh same-owner scope",&mut changes,&changed,||row.read_with(visual,|row,_|row.available)).await;
    row.update_in(visual,|row,window,cx|{row.open(window,cx);row.answer(NeedAnswer::Answer(json!(row.text.read(cx).value().to_string())),cx);});
    until("actual answer settles here",&mut changes,&changed,||row.read_with(visual,|row,_|matches!(row.state,NeedRowState::Answered{here:true,..}))).await;
    let saved=state.lock().unwrap();let commands=&saved.commands;assert_eq!(commands.len(),3);
    assert!(commands.iter().all(|command|command["machine"]=="owner"&&command["surface"]=="board"&&command["epoch"]==9&&command["command"]["answer"]=="  exact draft\n"));
    assert_eq!(commands[0]["expectedVersion"],3);assert_eq!(commands[1]["expectedVersion"],4);assert_eq!(commands[2]["expectedVersion"],4);
    drop(needs);
}
