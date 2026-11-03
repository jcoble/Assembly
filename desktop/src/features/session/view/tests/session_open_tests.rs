//! Navigator refusal fixtures use no provider or live data; outgoing state must survive failed preparation.
use gpui::TestAppContext;
use crate::core::engine::SessionKey;
use crate::core::intents::session_open::{SessionOpenRequest,SessionOpenTarget};
use crate::core::state::selected_session::SelectedSession;
use super::SessionPane;

#[gpui::test]
fn an_invalid_reference_never_changes_the_visible_session_or_starts_a_subscription(cx:&mut TestAppContext) {
    let (pane,client,cx)=super::tests::open(cx);
    cx.update(|_,cx|SelectedSession::init(cx));
    pane.update_in(cx,|pane,window,cx|pane.request_open(SessionOpenRequest {
        key:SessionKey{machine:"owner".into(),id:"".into()},request_id:"bad".into(),target:SessionOpenTarget::Engine,
    },window,cx));
    cx.run_until_parked();
    cx.update(|_,cx|{
        assert!(pane.read(cx).session.is_none());
        assert!(SelectedSession::global(cx).read(cx).session_id().is_none());
    });
    assert!(client.subscribed().is_empty());
    assert!(client.calls().is_empty());
}

#[gpui::test]
fn a_valid_request_without_a_live_captured_connection_preserves_outgoing_rows(cx:&mut TestAppContext) {
    let (pane,client,cx)=super::tests::open(cx);
    cx.update(|_,cx|SelectedSession::init(cx));
    pane.update_in(cx,|pane,window,cx| {
        pane.session=Some("outgoing".into());
        pane.request_open(SessionOpenRequest {
            key:SessionKey{machine:"owner".into(),id:"target".into()},request_id:"open".into(),target:SessionOpenTarget::Historical,
        },window,cx);
    });
    cx.run_until_parked();
    cx.update(|_,cx|assert_eq!(pane.read(cx).session.as_deref(),Some("outgoing")));
    assert!(client.subscribed().is_empty());
    assert!(client.calls().is_empty());
}

#[gpui::test]
fn qualified_selection_changes_even_when_two_owners_use_the_same_session_id(cx:&mut TestAppContext) {
    cx.update(|cx| {
        SelectedSession::init(cx);
        let selected=SelectedSession::global(cx);
        selected.update(cx,|selected,cx|selected.commit_open(SessionKey{machine:"first".into(),id:"same".into()},crate::core::socket::row_source::ExpectedRowSource{machine:"first".into(),generation:3,route_machine:Some("first route".into())},false,cx));
        selected.update(cx,|selected,cx|selected.commit_open(SessionKey{machine:"second".into(),id:"same".into()},crate::core::socket::row_source::ExpectedRowSource{machine:"second".into(),generation:7,route_machine:Some("second route".into())},true,cx));
        assert_eq!(selected.read(cx).key().unwrap().machine,"second");
        assert!(selected.read(cx).read_only());
    });
}

#[gpui::test]
fn readonly_composer_deactivation_starts_no_config_pending_watch_or_write(cx:&mut TestAppContext) {
    use crate::features::session::view::session_action::SessionAction;
    let (pane,client,cx)=super::tests::open(cx);
    pane.update_in(cx,|pane,window,cx|pane.composer.update(cx,|composer,cx|composer.set_session(Some("outgoing".into()),window,cx)));
    cx.run_until_parked();
    let before=(client.config_calls().len(),client.engine_calls().len(),client.calls().len());
    pane.update_in(cx,|pane,window,cx| {
        pane.read_only=true;pane.session=Some("history".into());
        pane.composer.update(cx,|composer,cx|composer.set_target(None,None,window,cx));
        for action in [SessionAction::RetryPrompt{client_id:"p".into()},SessionAction::Answer{ask_id:"ask".into(),answer:serde_json::Value::Null},SessionAction::StopTask{task:"native".into()}] {
            pane.on_action(action,window,cx);
        }
    });
    cx.run_until_parked();
    assert_eq!((client.config_calls().len(),client.engine_calls().len(),client.calls().len()),before);
    assert_eq!(client.message_subscriptions(),0);
}

// Fake wire fixture: this proves client cancellation/correlation, never backend native authority.
#[gpui::test]
async fn a_source_refusal_on_the_prepared_first_page_preserves_the_outgoing_view(cx:&mut TestAppContext) {
    use std::{rc::Rc,sync::{Arc,Mutex},cell::RefCell};
    use futures_channel::mpsc::unbounded;
    use gpui_kit::AppContext as _;
    use serde_json::json;
    use crate::core::socket::{client::SocketClient,endpoint::Endpoint,fake_backend::{FakeBackend,Reply},test_wait::until};
    use crate::core::intents::session_open::SessionOpenResult;
    use crate::features::session::client::socket_session_client::SocketSessionClient;
    let (changed,mut changes)=unbounded();let tell=changed.clone();
    let seen=Arc::new(Mutex::new(Vec::new()));let wire=seen.clone();
    let backend=FakeBackend::start(move|frame| {
        wire.lock().unwrap().push(frame.clone());let _=tell.unbounded_send(());
        let id=&frame["id"];
        match (frame["t"].as_str(),frame["method"].as_str(),frame["topic"].as_str()) {
            (Some("hello"),..)=>vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"box","engine":"owner"}))],
            (Some("call"),Some("open_target"),_)=>vec![Reply::Frame(json!({"t":"result","id":id,"result":{
                "kind":"historical","machine":"owner","generation":0,"routeMachine":null,"sessionId":"target",
                "parentSessionId":null,"itemId":null,"seq":null,"payloadSessionId":null,"readOnly":true,"hasHistoryRows":true,
                "originalKey":{"machine":"owner","id":"target"},"requestId":frame["args"]["requestId"],"checkoutRoot":null,
                "expectedSource":{"machine":"owner","generation":0,"routeMachine":null},"transcriptOnly":true
            }}))],
            (Some("sub"),_,Some("rows"))=>vec![Reply::Frame(json!({"t":"state","sub":id,"seq":7,"items":[]}))],
            (Some("call"),Some("page"),_)=>vec![Reply::Frame(json!({"t":"error","id":id,"code":"source_changed","message":"actual fixture source refusal"}))],
            _=>vec![],
        }
    });
    cx.executor().allow_parking();
    let (main,bulk)=cx.update(|cx| {
        gpui_kit::init(cx);SelectedSession::init(cx);
        let endpoint=Endpoint{port:backend.port,token:"secret".into()};
        (SocketClient::start(endpoint.clone(),cx),SocketClient::start_bulk(endpoint,cx))
    });
    let ready_tell=changed.clone();let bulk_tell=changed.clone();
    let _main=cx.update(|cx|cx.observe(&main,move|_,_|{let _=ready_tell.unbounded_send(());}));
    let _bulk=cx.update(|cx|cx.observe(&bulk,move|_,_|{let _=bulk_tell.unbounded_send(());}));
    until("both captured socket generations",&mut changes,&changed,||cx.update(|cx|main.read(cx).connection_generation().is_some()&&bulk.read(cx).connection_generation().is_some())).await;
    let (pane,cx)=cx.add_window_view(|window,cx|SessionPane::new(Rc::new(SocketSessionClient),window,cx));
    let results=Rc::new(RefCell::new(Vec::new()));let captured=results.clone();let result_tell=changed.clone();
    let _results=cx.update(|_,cx|cx.subscribe(&SelectedSession::global(cx),move|_,event:&SessionOpenResult,_| {
        captured.borrow_mut().push(event.clone());let _=result_tell.unbounded_send(());
    }));
    pane.update_in(cx,|pane,window,cx| {
        pane.session=Some("outgoing".into());
        pane.request_open(SessionOpenRequest{key:SessionKey{machine:"owner".into(),id:"target".into()},request_id:"correlated".into(),target:SessionOpenTarget::Historical},window,cx);
    });
    until("the exact source refusal result",&mut changes,&changed,||!results.borrow().is_empty()).await;
    let result=results.borrow();assert_eq!(result.len(),1);assert_eq!(result[0].request_id,"correlated");assert_eq!(result[0].result.as_ref().unwrap_err().code,"source_changed");
    cx.update(|_,cx| {let pane=pane.read(cx);assert_eq!(pane.session.as_deref(),Some("outgoing"));assert!(pane.pending_open.is_none());});
    let seen=seen.lock().unwrap();
    let source=json!({"machine":"owner","generation":0,"routeMachine":null});
    assert_eq!(seen.iter().find(|frame|frame["t"]=="sub"&&frame["topic"]=="rows").unwrap()["expectedSource"],source);
    assert_eq!(seen.iter().find(|frame|frame["method"]=="page").unwrap()["args"]["expectedSource"],source);
}

#[gpui::test]
fn a_refused_captured_source_cannot_start_more_detail_or_write_calls(cx:&mut TestAppContext) {
    use crate::core::socket::row_source::ExpectedRowSource;
    use crate::features::session::view::session_action::SessionAction;
    let (pane,client,cx)=super::tests::open(cx);
    pane.update_in(cx,|pane,window,cx| {
        pane.session=Some("history".into());
        pane.row_source=Some(ExpectedRowSource{machine:"owner".into(),generation:5,route_machine:Some("captured".into())});
        pane.on_rows(crate::features::session::client::session_client::RowUpdate::Refused{code:"source_changed".into(),message:"The captured owner changed.".into()},cx);
        pane.on_action(SessionAction::ShowImage{name:"image".into()},window,cx);
        pane.on_action(SessionAction::RetryPrompt{client_id:"prompt".into()},window,cx);
    });
    cx.run_until_parked();
    assert!(client.calls().is_empty());assert!(client.config_calls().is_empty());assert!(client.engine_calls().is_empty());
}

#[gpui::test]
fn a_native_card_open_keeps_the_actual_parent_item_seq_and_payload_correlation(cx:&mut TestAppContext) {
    use crate::core::socket::row_source::ExpectedRowSource;
    use crate::features::session::rows::row::Row;
    let (pane,_,cx)=super::tests::open(cx);
    pane.update_in(cx,|pane,_,_| {
        pane.session=Some("assembly-parent".into());
        pane.row_source=Some(ExpectedRowSource{machine:"actual-engine".into(),generation:9,route_machine:Some("captured".into())});
        let row:Row=serde_json::from_value(serde_json::json!({"sessionId":"assembly-parent","seq":7,"itemId":"subagent:actual-acp","runId":null,"kind":"subagent",
            "payload":{"sessionId":"assembly-parent","title":"Native","state":"idle","canStop":false},"createdAt":1})).unwrap();
        pane.view.apply(&row);
        let item_key=pane.view.items[0].key.clone();
        let request=pane.native_open_request(&item_key,"assembly-parent").unwrap();
        assert_eq!(request.key,SessionKey{machine:"actual-engine".into(),id:"assembly-parent".into()});
        assert!(matches!(request.target,SessionOpenTarget::Native{parent,item_id,seq:7,payload_session_id} if parent==request.key&&item_id=="subagent:actual-acp"&&payload_session_id=="assembly-parent"));
        assert!(pane.native_open_request(&item_key,"stale-payload").is_err());
    });
}

struct RefusingChatRestore;
impl crate::core::state::workspace_panes::PaneCodec for RefusingChatRestore {
    fn name(&self)->&'static str {"chat"}
    fn capture(&self,_:&gpui_kit::Window,_:&gpui_kit::App)->Result<serde_json::Value,String>{Ok(serde_json::json!({"anchor":"actual-outgoing"}))}
    fn validate(&self,_:&serde_json::Value,_:&crate::core::state::workspace_panes::WorkspaceKey,_:&serde_json::Value,_:&gpui_kit::App)->Result<(),String>{Ok(())}
    fn restore(&self,_:&serde_json::Value,_:&mut gpui_kit::Window,cx:&mut gpui_kit::App){
        crate::core::state::workspace_panes::WorkspacePanes::refuse_restore("actual fixture codec failure".into(),cx);
    }
}

#[gpui::test]
async fn a_real_post_commit_codec_failure_reports_refusal_with_the_target_transcript_visible(cx:&mut TestAppContext) {
    use std::{rc::Rc,cell::RefCell};
    use futures_channel::mpsc::unbounded;
    use serde_json::json;
    use crate::core::socket::{client::SocketClient,endpoint::Endpoint,fake_backend::{FakeBackend,Reply},test_wait::until};
    use crate::core::state::workspace_panes::WorkspacePanes;
    use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;
    use crate::features::session::client::socket_session_client::SocketSessionClient;
    use crate::core::intents::session_open::SessionOpenResult;
    let (changed,mut changes)=unbounded();let tell=changed.clone();
    let backend=FakeBackend::start(move|frame| {
        let _=tell.unbounded_send(());let id=&frame["id"];
        match(frame["t"].as_str(),frame["feature"].as_str(),frame["method"].as_str(),frame["topic"].as_str()) {
            (Some("hello"),..)=>vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"box","engine":"owner"}))],
            (Some("call"),Some("sessions"),Some("open_target"),_)=>vec![Reply::Frame(json!({"t":"result","id":id,"result":{
                "kind":"historical","machine":"owner","generation":0,"routeMachine":null,"sessionId":"target","parentSessionId":null,
                "itemId":null,"seq":null,"payloadSessionId":null,"readOnly":true,"hasHistoryRows":true,"originalKey":{"machine":"owner","id":"target"},
                "requestId":frame["args"]["requestId"],"checkoutRoot":"/recorded","expectedSource":{"machine":"owner","generation":0,"routeMachine":null},"transcriptOnly":false}}))],
            (Some("sub"),_,_,Some("rows"))=>vec![Reply::Frame(json!({"t":"state","sub":id,"seq":1,"items":[]}))],
            (Some("call"),Some("rows"),Some("page"),_)=>vec![Reply::Frame(json!({"t":"result","id":id,"result":{"rows":[{"sessionId":"target","seq":1,"itemId":"u","runId":null,"kind":"user","payload":{"text":"readable target transcript"},"createdAt":1}],"hasBefore":false,"activeRunId":null}}))],
            (Some("call"),Some("workspaces"),Some("read"),_)=>vec![Reply::Frame(json!({"t":"result","id":id,"result":{"revision":7,"snapshot":{"version":1,"chat":{"anchor":"target"}}}}))],
            _=>vec![],
        }
    });
    cx.executor().allow_parking();
    let(main,bulk)=cx.update(|cx|{
        gpui_kit::init(cx);SelectedSession::init(cx);WorkspacePanes::register(Box::new(RefusingChatRestore),cx).unwrap();WorkspaceCoordinator::init(cx);
        let endpoint=Endpoint{port:backend.port,token:"secret".into()};
        (SocketClient::start(endpoint.clone(),cx),SocketClient::start_bulk(endpoint,cx))
    });
    let first=changed.clone();let second=changed.clone();
    let _main=cx.update(|cx|cx.observe(&main,move|_,_|{let _=first.unbounded_send(());}));
    let _bulk=cx.update(|cx|cx.observe(&bulk,move|_,_|{let _=second.unbounded_send(());}));
    until("captured sockets",&mut changes,&changed,||cx.update(|cx|main.read(cx).connection_generation().is_some()&&bulk.read(cx).connection_generation().is_some())).await;
    let(pane,cx)=cx.add_window_view(|window,cx|SessionPane::new(Rc::new(SocketSessionClient),window,cx));
    let outcomes=Rc::new(RefCell::new(Vec::new()));let got=outcomes.clone();let result_tell=changed.clone();
    let _result=cx.update(|_,cx|cx.subscribe(&SelectedSession::global(cx),move|_,event:&SessionOpenResult,_|{got.borrow_mut().push(event.clone());let _=result_tell.unbounded_send(());}));
    pane.update_in(cx,|pane,window,cx|pane.request_open(SessionOpenRequest{key:SessionKey{machine:"owner".into(),id:"target".into()},request_id:"restore-failure".into(),target:SessionOpenTarget::Historical},window,cx));
    until("correlated post-commit restore failure",&mut changes,&changed,||!outcomes.borrow().is_empty()).await;
    let results=outcomes.borrow();assert_eq!(results.len(),1);assert_eq!(results[0].request_id,"restore-failure");
    let error=results[0].result.as_ref().unwrap_err();assert_eq!(error.code,"workspace_restore_failed");assert!(error.message.contains("actual fixture codec failure"));
    cx.update(|_,cx|{
        let pane=pane.read(cx);assert_eq!(pane.session.as_deref(),Some("target"));assert_eq!(pane.view.items.len(),1);
        assert_eq!(SelectedSession::global(cx).read(cx).key(),Some(&SessionKey{machine:"owner".into(),id:"target".into()}));
    });
}

#[gpui::test]
fn readonly_history_does_not_issue_an_unqualified_mark_read_write(cx:&mut TestAppContext) {
    let (pane,client,cx)=super::tests::open(cx);
    pane.update_in(cx,|pane,_,cx|{
        pane.session=Some("history".into());pane.view.newest=9;pane.read_only=true;
        pane.tell_read(false,cx);
    });
    cx.run_until_parked();assert!(client.reads().is_empty());
    pane.update_in(cx,|pane,_,cx|{pane.read_only=false;pane.tell_read(false,cx);});
    cx.run_until_parked();assert_eq!(client.reads(),vec![serde_json::json!({"session":"history","seq":9})]);
}

#[gpui::test]
async fn a_source_refusal_while_workspace_read_is_held_invalidates_the_ready_page(cx:&mut TestAppContext) {
    use std::{rc::Rc,cell::RefCell,sync::{Arc,Mutex}};
    use futures_channel::{mpsc::unbounded,oneshot};
    use serde_json::json;
    use crate::core::socket::{client::SocketClient,endpoint::Endpoint,fake_backend::{FakeBackend,Reply},test_wait::until};
    use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;
    use crate::features::session::client::socket_session_client::SocketSessionClient;
    use crate::core::intents::session_open::SessionOpenResult;
    let (changed,mut changes)=unbounded();let tell=changed.clone();
    let (held,workspace_read)=oneshot::channel();let held=Arc::new(Mutex::new(Some(held)));
    let mut rows_id=None;let mut workspace_id=None;
    let backend=FakeBackend::start(move|frame| {
        let _=tell.unbounded_send(());let id=&frame["id"];
        match(frame["t"].as_str(),frame["feature"].as_str(),frame["method"].as_str(),frame["topic"].as_str()) {
            (Some("hello"),..)=>vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"box","engine":"owner"}))],
            (Some("call"),Some("sessions"),Some("open_target"),_)=>vec![Reply::Frame(json!({"t":"result","id":id,"result":{
                "kind":"historical","machine":"owner","generation":0,"routeMachine":null,"sessionId":"target","parentSessionId":null,
                "itemId":null,"seq":null,"payloadSessionId":null,"readOnly":true,"hasHistoryRows":true,"originalKey":{"machine":"owner","id":"target"},
                "requestId":frame["args"]["requestId"],"checkoutRoot":"/recorded","expectedSource":{"machine":"owner","generation":0,"routeMachine":null},"transcriptOnly":false}}))],
            (Some("sub"),_,_,Some("rows"))=>{rows_id=Some(id.clone());vec![Reply::Frame(json!({"t":"state","sub":id,"seq":1,"items":[]}))]},
            (Some("call"),Some("rows"),Some("page"),_)=>vec![Reply::Frame(json!({"t":"result","id":id,"result":{"rows":[{"sessionId":"target","seq":1,"itemId":"u","runId":null,"kind":"user","payload":{"text":"prepared target"},"createdAt":1}],"hasBefore":false,"activeRunId":null}}))],
            (Some("call"),Some("workspaces"),Some("read"),_)=>{workspace_id=Some(id.clone());if let Some(held)=held.lock().unwrap().take(){let _=held.send(());}vec![]},
            // Fixture control on the existing main socket releases the actual captured watch's error.
            (Some("call"),Some("fixture"),Some("invalidate"),_)=>vec![Reply::Frame(json!({"t":"error","id":rows_id,"code":"source_changed","message":"The captured source changed during the workspace read."})),Reply::Frame(json!({"t":"result","id":id,"result":null}))],
            (Some("cancel"),..)=>{
                if workspace_id.as_ref()==Some(id){vec![Reply::Frame(json!({"t":"result","id":id,"result":{"revision":7,"snapshot":{"version":1}}}))]}else{vec![]}
            },
            _=>vec![],
        }
    });
    cx.executor().allow_parking();
    let(main,bulk)=cx.update(|cx|{
        gpui_kit::init(cx);SelectedSession::init(cx);WorkspaceCoordinator::init(cx);
        let endpoint=Endpoint{port:backend.port,token:"secret".into()};
        (SocketClient::start(endpoint.clone(),cx),SocketClient::start_bulk(endpoint,cx))
    });
    let first=changed.clone();let second=changed.clone();
    let _main=cx.update(|cx|cx.observe(&main,move|_,_|{let _=first.unbounded_send(());}));
    let _bulk=cx.update(|cx|cx.observe(&bulk,move|_,_|{let _=second.unbounded_send(());}));
    until("captured sockets",&mut changes,&changed,||cx.update(|cx|main.read(cx).connection_generation().is_some()&&bulk.read(cx).connection_generation().is_some())).await;
    let(pane,cx)=cx.add_window_view(|window,cx|SessionPane::new(Rc::new(SocketSessionClient),window,cx));
    let outcomes=Rc::new(RefCell::new(Vec::new()));let got=outcomes.clone();let result_tell=changed.clone();
    let _result=cx.update(|_,cx|cx.subscribe(&SelectedSession::global(cx),move|_,event:&SessionOpenResult,_|{got.borrow_mut().push(event.clone());let _=result_tell.unbounded_send(());}));
    pane.update_in(cx,|pane,window,cx|{
        pane.session=Some("outgoing".into());
        pane.request_open(SessionOpenRequest{key:SessionKey{machine:"owner".into(),id:"target".into()},request_id:"held".into(),target:SessionOpenTarget::Historical},window,cx);
    });
    workspace_read.await.expect("the actual workspace target read reached its socket");
    cx.update(|_,cx|assert!(pane.read(cx).pending_open.as_ref().unwrap().stage.as_ref().unwrap().ready()));
    let invalidate=cx.update(|_,cx|main.read(cx).call::<serde_json::Value>("fixture","invalidate",json!({}),None));
    assert!(matches!(invalidate.await,crate::core::socket::outcome::Outcome::Ok(_)));
    until("the source refusal",&mut changes,&changed,||!outcomes.borrow().is_empty()).await;
    cx.run_until_parked();
    let outcomes=outcomes.borrow();assert_eq!(outcomes.len(),1);assert_eq!(outcomes[0].result.as_ref().unwrap_err().code,"source_changed");
    cx.update(|_,cx|{assert_eq!(pane.read(cx).session.as_deref(),Some("outgoing"));assert!(SelectedSession::global(cx).read(cx).session_id().is_none());});
}

#[gpui::test]
fn native_stop_sends_parent_and_the_exact_persisted_provider_child_suffix(cx:&mut TestAppContext) {
    use crate::core::socket::row_source::ExpectedRowSource;
    use crate::features::session::{rows::row::Row,view::session_action::SessionAction};
    let(pane,client,cx)=super::tests::open(cx);
    client.queue_answer(Ok(serde_json::Value::Null));
    pane.update_in(cx,|pane,window,cx|{
        pane.session=Some("parent".into());pane.row_source=Some(ExpectedRowSource{machine:"engine".into(),generation:0,route_machine:None});
        pane._rows=Some(gpui_kit::Subscription::new(||{}));
        let mut row:Row=serde_json::from_value(serde_json::json!({"sessionId":"parent","seq":7,"itemId":"subagent:actual-provider-child","runId":null,"kind":"subagent",
            "payload":{"sessionId":"parent","title":"Native","state":"running","canStop":true},"createdAt":1})).unwrap();
        pane.view.apply(&row);let key=pane.view.items[0].key.clone();
        pane.on_action(SessionAction::StopNative{key:key.clone(),payload_session_id:"parent".into()},window,cx);
        pane.on_action(SessionAction::StopNative{key:key.clone(),payload_session_id:"wrong-parent".into()},window,cx);
        row.seq=8;row.payload["helper"]=serde_json::json!(true);pane.view.apply(&row);
        pane.on_action(SessionAction::StopNative{key,payload_session_id:"parent".into()},window,cx);
    });
    cx.run_until_parked();
    assert_eq!(client.calls(),vec![("send/stopTask".into(),serde_json::json!({"session":"parent","task":"actual-provider-child"}))]);
}

#[gpui::test]
fn stale_nonstoppable_malformed_and_refused_native_cards_send_nothing(cx:&mut TestAppContext) {
    use crate::core::socket::row_source::ExpectedRowSource;
    use crate::features::session::{rows::row::Row,view::session_action::SessionAction,client::session_client::RowUpdate};
    let(pane,client,cx)=super::tests::open(cx);
    pane.update_in(cx,|pane,window,cx|{
        pane.session=Some("parent".into());pane.row_source=Some(ExpectedRowSource{machine:"engine".into(),generation:0,route_machine:None});
        pane._rows=Some(gpui_kit::Subscription::new(||{}));
        let mut row:Row=serde_json::from_value(serde_json::json!({"sessionId":"parent","seq":7,"itemId":"subagent:child","runId":null,"kind":"subagent",
            "payload":{"sessionId":"parent","title":"Native","state":"running","canStop":false},"createdAt":1})).unwrap();
        pane.view.apply(&row);let key=pane.view.items[0].key.clone();
        pane.on_action(SessionAction::StopNative{key:key.clone(),payload_session_id:"parent".into()},window,cx);
        row.seq=8;row.payload["canStop"]=serde_json::json!(true);row.payload["state"]=serde_json::json!("idle");pane.view.apply(&row);
        pane.on_action(SessionAction::StopNative{key:key.clone(),payload_session_id:"parent".into()},window,cx);
        row.seq=9;row.item_id=Some("malformed".into());row.payload["state"]=serde_json::json!("running");pane.view.apply(&row);
        let malformed=pane.view.items.last().unwrap().key.clone();
        pane.on_action(SessionAction::StopNative{key:malformed,payload_session_id:"parent".into()},window,cx);
        pane.on_rows(RowUpdate::Refused{code:"source_changed".into(),message:"Changed".into()},cx);
        pane.on_action(SessionAction::StopNative{key,payload_session_id:"parent".into()},window,cx);
    });
    cx.run_until_parked();assert!(client.calls().is_empty());
}

#[gpui::test]
fn a_qualified_readonly_journal_marks_read_with_its_exact_descriptor_source(cx:&mut TestAppContext) {
    use crate::core::socket::row_source::ExpectedRowSource;
    let(pane,client,cx)=super::tests::open(cx);
    let source=ExpectedRowSource{machine:"actual-owner".into(),generation:41,route_machine:Some("saved-route".into())};
    pane.update_in(cx,|pane,_,cx|{pane.session=Some("history".into());pane.view.newest=9;pane.read_only=true;pane.row_source=Some(source.clone());pane.tell_read(false,cx);});
    cx.run_until_parked();
    assert_eq!(client.reads(),vec![serde_json::json!({"session":"history","seq":9,"expectedSource":source})]);
}

#[gpui::test]
fn a_mark_read_source_refusal_is_visible_and_never_retried_without_its_source(cx:&mut TestAppContext) {
    use crate::{core::socket::row_source::ExpectedRowSource,features::session::client::session_client::CallError};
    let(pane,client,cx)=super::tests::open(cx);
    client.queue_read_answer(Err(CallError::Refused{code:"source_changed".into(),message:"captured owner replaced".into()}));
    pane.update_in(cx,|pane,_,cx|{pane.session=Some("history".into());pane.view.newest=9;pane.read_only=true;pane.row_source=Some(ExpectedRowSource{machine:"owner".into(),generation:0,route_machine:None});pane.tell_read(false,cx);});
    cx.run_until_parked();
    pane.update_in(cx,|pane,_,cx|{assert!(pane.status.contains("captured owner replaced"));assert_eq!(pane.session.as_deref(),Some("history"));pane.tell_read(false,cx);});
    cx.run_until_parked();assert_eq!(client.reads().len(),1);assert!(client.reads()[0].get("expectedSource").is_some());
}

#[gpui::test]
fn selected_source_is_only_the_prepared_descriptor_tuple_and_fixture_selection_clears_it(cx:&mut TestAppContext) {
    use crate::core::socket::row_source::ExpectedRowSource;
    cx.update(|cx| {
        SelectedSession::init(cx);let selected=SelectedSession::global(cx);
        let source=ExpectedRowSource{machine:"actual-owner".into(),generation:29,route_machine:Some("captured-route".into())};
        selected.update(cx,|selected,cx|selected.commit_open(SessionKey{machine:"actual-owner".into(),id:"session".into()},source.clone(),true,cx));
        assert_eq!(selected.read(cx).source(),Some(&source));assert!(selected.read(cx).read_only());
        selected.update(cx,|selected,cx|selected.select(Some("session".into()),cx));
        assert!(selected.read(cx).source().is_none());assert!(selected.read(cx).key().is_none());
    });
}
