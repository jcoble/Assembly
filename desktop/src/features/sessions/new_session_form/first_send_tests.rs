//! Source-derived controller fixtures. Actual fake-socket lifetimes supply the source assertion;
//! these tests do not claim provider or native acceptance.
use super::{NewSessionForm,StartAttempt,StartRequest,StartSource,SelectedSession,CallError};
use std::rc::Rc;
use serde_json::json;
use crate::core::socket::{client::SocketClient,endpoint::Endpoint,fake_backend::{FakeBackend,Reply},test_wait::until};
use crate::features::session::client::fake_client::FakeClient;
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use std::sync::{Arc,Mutex};

#[gpui::test]
async fn unknown_create_looks_up_the_original_source_and_id_before_an_explicit_same_request_retry(cx:&mut TestAppContext) {
    let (changed,mut changes)=unbounded();let sent=changed.clone();
    let frames=Arc::new(Mutex::new(Vec::new()));let seen=frames.clone();
    let backend=FakeBackend::start(move|frame| {
        if frame["t"]=="hello" {return vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"fixture-host","engine":"box"}))];}
        seen.lock().unwrap().push(frame.clone());let _=sent.unbounded_send(());
        if frame["method"]=="resolve_source" {return vec![Reply::Frame(json!({"t":"result","id":frame["id"],"result":{"machine":"box","generation":0,"routeMachine":null}}))];}
        vec![]
    });
    cx.executor().allow_parking();
    cx.update(|cx|{gpui_kit::init(cx);SelectedSession::init(cx);SocketClient::start(Endpoint{port:backend.port,token:"secret".into()},cx);SocketClient::start_bulk(Endpoint{port:backend.port,token:"secret".into()},cx);});
    let _sockets=cx.update(|cx|[SocketClient::global(cx),SocketClient::try_bulk(cx).unwrap()].iter().map(|socket| {
        let changed=changed.clone();cx.observe(socket,move|_,_|{let _=changed.unbounded_send(());})
    }).collect::<Vec<_>>());
    until("welcomed source sockets",&mut changes,&changed,|| {
        cx.run_until_parked();cx.update(|cx|SocketClient::global(cx).read(cx).engine_machine().is_some()&&SocketClient::try_bulk(cx).unwrap().read(cx).engine_machine().is_some())
    }).await;
    let source=cx.update(|cx|StartSource::resolve(None,cx)).await.unwrap();
    let client=FakeClient::default();client.queue_answer(Ok(json!({"agents":[]})));
    let held=client.clone();let (form,cx)=cx.add_window_view(move|window,cx|NewSessionForm::with_client(Rc::new(held),window,cx));
    let initial=json!({"targetId":"actual-advertised","choices":{"model":"chosen","effort":"medium"}});
    let request=StartRequest::capture("advertised-agent".into(),None,None,None,source.source.clone(),initial.clone()).unwrap();
    let original=request.arguments();
    form.update_in(cx,|form,window,_| {
        form.location_read=None;
        form.attempt=Some(StartAttempt{request,text:"retained draft".into(),images:Vec::new(),source:Some(Rc::new(source)),accepted:None,current_operation:None,retry_operation:None,retry_version:0,
            lookup_absent:false,checkout_refused:false,opening:None,prompt_dispatched:false,window:window.window_handle(),watch:None,changed:None,connections:Vec::new(),navigation:None,prompt:None});
    });
    client.queue_answer(Err(CallError::Uncertain{machine:"box".into(),message:"The write may have committed.".into()}));
    client.queue_answer(Ok(json!({"sessionId":null,"projectId":null,"checkoutOperationId":null,"checkoutState":null,"state":null})));
    let tell=changed.clone();let _heard=cx.update(|_,cx|cx.observe(&form,move|_,_|{let _=tell.unbounded_send(());}));
    form.update(cx,|form,cx|form.resolve_start(false,cx));
    until("authoritative absent lookup",&mut changes,&changed,|| {
        cx.run_until_parked();form.read_with(cx,|form,_|form.attempt.as_ref().is_some_and(|attempt|attempt.lookup_absent)&&!form.starting)
    }).await;
    let calls=client.calls();let writes:Vec<_>=calls.iter().filter(|(method,_)|method=="sessions/create").collect();
    assert_eq!(writes.len(),1);assert_eq!(writes[0].1,original);
    let lookup=calls.iter().find(|(method,_)|method=="sessions/byClient").unwrap();
    assert_eq!(lookup.1["clientId"],original["clientId"]);assert_eq!(lookup.1["expectedSource"],original["expectedSource"]);
    assert!(calls.iter().position(|(method,_)|method=="sessions/create")<calls.iter().position(|(method,_)|method=="sessions/byClient"));
    form.read_with(cx,|form,_| {let attempt=form.attempt.as_ref().unwrap();assert_eq!(attempt.text,"retained draft");assert!(attempt.accepted.is_none());assert_eq!(attempt.request.arguments()["initialConfig"],initial);});
    assert!(cx.update(|_,cx|SelectedSession::global(cx).read(cx).key().is_none()));
    client.queue_answer(Err(CallError::Failed("authoritative refusal".into())));
    form.update_in(cx,|form,window,cx|form.first_send("retained draft".into(),Vec::new(),window,cx));
    until("explicit same-ID retry",&mut changes,&changed,|| {
        cx.run_until_parked();client.calls().iter().filter(|(method,_)|method=="sessions/create").count()==2&&form.read_with(cx,|form,_|!form.starting)
    }).await;
    let writes:Vec<_>=client.calls().into_iter().filter(|(method,_)|method=="sessions/create").collect();
    assert_eq!(writes[0].1,writes[1].1);assert!(!client.calls().iter().any(|(method,_)|method=="session/send"||method=="prompt"));
    // A later association is actual metadata, never evidence that the original NoProject request chose it.
    client.queue_answer(Ok(json!({"sessionId":"accepted-a","projectId":"project-a","checkoutOperationId":null,"checkoutState":"ready","state":"idle"})));
    form.update(cx,|form,cx|form.reconcile_start(cx));
    until("actual accepted association",&mut changes,&changed,|| {
        cx.run_until_parked();form.read_with(cx,|form,_|form.attempt.as_ref().and_then(|attempt|attempt.accepted.as_ref()).is_some())
    }).await;
    form.update_in(cx,|form,window,cx|form.first_send("retained draft".into(),Vec::new(),window,cx));
    form.read_with(cx,|form,_| {
        let attempt=form.attempt.as_ref().unwrap();let accepted=attempt.accepted.as_ref().unwrap();
        assert_eq!(accepted.session_id,"accepted-a");assert_eq!(accepted.project_id.as_deref(),Some("project-a"));
        assert!(attempt.request.project_id().is_none());assert!(attempt.opening.is_none());assert!(!attempt.prompt_dispatched);
        assert_eq!(attempt.text,"retained draft");
    });
    assert_eq!(client.calls().iter().filter(|(method,_)|method=="sessions/create").count(),2);
    // A fresh checkout-request fixture receives an actual parsed reply without certified initial proof.
    form.update(cx,|form,cx| {
        let original=form.attempt.take().unwrap();let source=original.source.unwrap();
        let project=serde_json::from_value(json!({"id":"project-a","machine":"local","rootPath":"/owned/project-a","title":"A","repoKey":null,"pinnedAt":null,"removedAt":null,"createdAt":1,"lastUsedAt":null})).unwrap();
        let request=StartRequest::capture("agent".into(),Some(project),None,Some(super::CheckoutIntent::Current),source.source.clone(),json!({})).unwrap();
        form.attempt=Some(StartAttempt{request,text:"retained checkout draft".into(),images:Vec::new(),source:Some(source),accepted:None,current_operation:None,retry_operation:None,retry_version:0,
            lookup_absent:false,checkout_refused:false,opening:None,prompt_dispatched:false,window:original.window,watch:None,changed:None,connections:Vec::new(),navigation:None,prompt:None});
        form.created_answer(Ok(json!({"sessionId":"checkout-session","projectId":"project-a","checkoutOperationId":null,"checkoutState":"ready","state":"idle"})),true,cx);
        form.observe_start(cx);
        assert!(form.error.contains("initial checkout proof"));
        let held=form.attempt.as_ref().unwrap();assert_eq!(held.accepted.as_ref().unwrap().session_id,"checkout-session");
        assert!(held.opening.is_none());assert!(!held.prompt_dispatched);assert_eq!(held.text,"retained checkout draft");
    });
    assert!(cx.update(|_,cx|SelectedSession::global(cx).read(cx).key().is_none()));

}
