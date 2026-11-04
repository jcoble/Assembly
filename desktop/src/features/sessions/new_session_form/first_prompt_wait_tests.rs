//! Real Navigator, workspace, Chat composer and prompt receipt against the existing source-derived wire fixture.
//! This does not claim provider/native acceptance.
use super::{NewSessionForm,StartAttempt,StartRequest,StartSource,CheckoutIntent,EngineFeed,SessionKey,SelectedSession,Created,SessionPane};
use crate::features::session::{client::socket_session_client::SocketSessionClient,view::session_pane::test_navigation::{NavigationFixture,NavigationHost}};
use crate::core::engine::EngineScope;
use crate::core::socket::test_wait::until;
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use serde_json::{Value,json};
use std::{rc::Rc,cell::Cell};

const INITIAL:&str="8b37d022-bad2-42d0-86b8-9739c08df285";
fn display(version:i64,state:&str,checkout:&str,operation:&str)->Value {
    json!({"entityKind":"session","id":"accepted","machine":"owner","version":version,"provider":"source-derived-fixture","model":null,
        "folder":"/source-derived/accepted","title":null,"parentId":null,"parentMachine":null,"runId":null,"state":state,"activityState":"idle",
        "epoch":1,"holder":"local","projectId":"project","checkoutState":checkout,"checkoutOperationId":operation,
        "checkoutReason":if checkout=="refused"{Some("tracked changes would be overwritten")}else{None}})
}
#[gpui::test]
async fn matching_checkout_and_idle_are_required_before_the_only_navigation_request(cx:&mut TestAppContext) {
    successful_first_prompt(cx,false).await;
}
#[gpui::test]
async fn refused_initial_current_checkout_retries_the_same_session_and_waits_for_its_accepted_operation(cx:&mut TestAppContext) {
    successful_first_prompt(cx,true).await;
}
async fn successful_first_prompt(cx:&mut TestAppContext,refuse:bool) {
    cx.executor().allow_parking();cx.update(gpui_kit::init);cx.update(SelectedSession::init);
    let(host,cx)=cx.add_window_view(|_,_|NavigationHost::default());
    let mut nav=cx.update(|window,cx|NavigationFixture::new(true,window,cx));nav.operational();
    host.update(cx,|host,cx|{host.pane=Some(nav.pane.clone());cx.notify();});
    nav.start_case(display(1,"starting","pending",INITIAL),json!({"sessionId":"accepted","projectId":"project","checkoutOperationId":INITIAL,"checkoutState":"pending","state":"starting"}));
    nav.rows("accepted",vec![]);nav.ready(cx).await;
    let source=cx.update(|_,cx|StartSource::resolve(None,cx)).await.unwrap();
    let form=cx.update(|window,cx|cx.new(|cx|NewSessionForm::with_client(Rc::new(SocketSessionClient),window,cx)));
    let project=serde_json::from_value(json!({"id":"project","machine":"local","rootPath":"/source-derived/accepted","title":"Project","repoKey":null,"pinnedAt":null,"removedAt":null,"createdAt":1,"lastUsedAt":null})).unwrap();
    let config=json!({"targetId":"fixture-target","choices":{"model":"observed-model","effort":"observed-effort"}});
    let request=StartRequest::capture("source-derived-fixture".into(),Some(project),None,Some(CheckoutIntent::Current),source.source.clone(),config.clone()).unwrap();
    let original=request.arguments();
    form.update_in(cx,|form,window,_| {
        form.location_read=None;
        form.attempt=Some(StartAttempt{request,text:"first prompt".into(),images:Vec::new(),source:None,accepted:None,current_operation:None,retry_operation:None,retry_version:0,
            lookup_absent:false,checkout_refused:false,opening:None,prompt_dispatched:false,window:window.window_handle(),watch:None,changed:None,connections:Vec::new(),navigation:None,prompt:None});
    });
    let (changed,mut changes)=unbounded();let created=Rc::new(Cell::new(0));let got=created.clone();let tell=changed.clone();
    let _created=cx.update(|_,cx|cx.subscribe(&form,move|_,_:&Created,_|{got.set(got.get()+1);let _=tell.unbounded_send(());}));
    let tell=changed.clone();let _form=cx.update(|_,cx|cx.observe(&form,move|_,_|{let _=tell.unbounded_send(());}));
    let feed=cx.update(|_,cx|EngineFeed::global(cx));let tell=changed.clone();
    let _feed=cx.update(|_,cx|cx.observe(&feed,move|_,_|{let _=tell.unbounded_send(());}));
    form.update(cx,|form,cx|form.resolve_start(false,cx));
    let key=SessionKey{machine:"owner".into(),id:"accepted".into()};
    let scope=EngineScope{machine:key.machine.clone(),topic:"sessions".into(),key:None};
    until("held pending accepted checkout",&mut changes,&changed,|| {
        cx.run_until_parked();feed.read_with(cx,|feed,_|feed.loaded(&scope)&&feed.session(&key).is_some_and(|session|session.version==1))
    }).await;
    assert_eq!(created.get(),0);assert!(!nav.calls().iter().any(|frame|frame["method"]=="open_target"||frame["feature"]=="send"));
    let (operation,version,text)=if refuse {
        nav.control("start_display",json!({"row":display(2,"starting","refused",INITIAL)}),cx).await;
        until("actual refused checkout",&mut changes,&changed,|| {cx.run_until_parked();form.read_with(cx,|form,_|form.attempt.as_ref().is_some_and(|attempt|attempt.checkout_refused))}).await;
        form.read_with(cx,|form,_| {assert!(form.error.contains("tracked changes"));assert_eq!(form.attempt.as_ref().unwrap().text,"first prompt");});
        form.update_in(cx,|form,window,cx|form.first_send("revised first prompt".into(),Vec::new(),window,cx));
        nav.calls_at_least("command",1).await;
        until("accepted retry operation",&mut changes,&changed,|| {cx.run_until_parked();form.read_with(cx,|form,_|form.attempt.as_ref().is_some_and(|attempt|attempt.current_operation.as_deref()!=Some(INITIAL)&&attempt.retry_operation.is_none()))}).await;
        let calls=nav.calls();let command=calls.iter().find(|frame|frame["method"]=="command").unwrap();
        assert_eq!(command["args"]["entityId"],"accepted");assert_eq!(command["args"]["command"],json!({"kind":"change_checkout","checkoutKind":"current"}));
        let actual=command["args"]["commandId"].as_str().unwrap().to_string();assert_ne!(actual,INITIAL);
        form.read_with(cx,|form,_|assert_eq!(form.attempt.as_ref().unwrap().accepted.as_ref().unwrap().initial_operation_id.as_deref(),Some(INITIAL)));
        (actual,4,"revised first prompt")
    } else {(INITIAL.to_string(),2,"first prompt")};
    nav.control("start_display",json!({"row":display(version,"starting","ready",&operation)}),cx).await;
    until("checkout ready while start is pending",&mut changes,&changed,|| {cx.run_until_parked();feed.read_with(cx,|feed,_|feed.session(&key).is_some_and(|session|session.version==version))}).await;
    assert!(!nav.calls().iter().any(|frame|frame["method"]=="open_target"||frame["feature"]=="send"));assert_eq!(created.get(),0);
    nav.control("start_display",json!({"row":display(version+1,"idle","ready",&operation)}),cx).await;
    nav.opened("accepted",cx).await;nav.calls_at_least("send",1).await;
    until("initial prompt dispatched without receipt",&mut changes,&changed,|| {cx.run_until_parked();form.read_with(cx,|form,_|form.attempt.as_ref().is_some_and(|attempt|attempt.prompt_dispatched))}).await;
    assert_eq!(created.get(),0,"dispatch/task success cannot complete the first send");
    let request_id=form.read_with(cx,|form,_|form.attempt.as_ref().unwrap().opening.clone().unwrap());
    assert!(cx.update(|_,cx|SessionPane::composer_for_open(&key,&request_id,cx).is_ok()));
    nav.control("release_prompt",json!({}),cx).await;
    until("actual correlated Composer Accepted",&mut changes,&changed,|| {cx.run_until_parked();created.get()==1}).await;
    let calls=nav.calls();let creates:Vec<_>=calls.iter().filter(|frame|frame["feature"]=="sessions"&&frame["method"]=="create").collect();
    assert_eq!(creates.len(),1);assert_eq!(creates[0]["args"],original);assert_eq!(creates[0]["args"]["initialConfig"],config);
    assert_eq!(calls.iter().filter(|frame|frame["method"]=="open_target").count(),1);
    let sends:Vec<_>=calls.iter().filter(|frame|frame["feature"]=="send"&&frame["method"]=="send").collect();assert_eq!(sends.len(),1);
    assert_eq!(sends[0]["args"]["session"],"accepted");assert_eq!(sends[0]["args"]["text"],text);assert_eq!(sends[0]["args"]["clientId"],original["clientId"]);
    assert_eq!(sends[0]["args"]["expectedSource"],original["expectedSource"]);
    assert_eq!(cx.update(|_,cx|SelectedSession::global(cx).read(cx).key().cloned()),Some(key));
}
