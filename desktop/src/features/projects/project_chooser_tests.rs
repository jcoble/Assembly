use super::{ProjectChooser,ProjectRecord,default_project};
use crate::core::socket::{client::SocketClient,endpoint::Endpoint,fake_backend::{FakeBackend,Reply,hello},test_wait::until};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use serde_json::json;
use std::sync::{Arc,Mutex};
fn record(id:&str,machine:&str,used:i64)->ProjectRecord {
    serde_json::from_value(json!({"id":id,"machine":machine,"rootPath":format!("/owned/{id}"),"title":"same title","repoKey":null,
        "pinnedAt":null,"removedAt":null,"createdAt":1,"lastUsedAt":used})).unwrap()
}
#[test]
fn opened_project_then_selected_session_then_recent_visible_define_the_unsent_choice() {
    let rows=vec![record("pinned-old","local",1),record("recent","local",10),record("selected","box",3)];
    assert_eq!(default_project(Some("pinned-old"),Some("selected"),&rows).unwrap().id,"pinned-old");
    assert_eq!(default_project(None,Some("selected"),&rows).unwrap().id,"selected");
    assert_eq!(default_project(None,None,&rows).unwrap().id,"recent");
    assert!(default_project(None,None,&[]).is_none());
}
#[gpui::test]
async fn search_uses_sql_filter_and_an_old_answer_cannot_replace_the_current_choices(cx:&mut TestAppContext) {
    let (changed,mut changes)=unbounded();let sent=changed.clone();
    let seen=Arc::new(Mutex::new(Vec::<serde_json::Value>::new()));let observed=seen.clone();
    let mut old=None;
    let backend=FakeBackend::start(move|frame| {
        if let Some(answer)=hello(frame){return answer;}
        observed.lock().unwrap().push(frame.clone());let _=sent.unbounded_send(());
        if frame["t"]=="sub" { return vec![Reply::Frame(json!({"t":"state","sub":frame["id"],"items":[]}))]; }
        if frame["t"]!="call" {return vec![];}
        assert_eq!(frame["feature"],"projects");assert_eq!(frame["method"],"list");
        assert_eq!(frame["args"]["limit"],100);
        let filter=frame["args"]["filter"].as_str().unwrap_or("");
        if filter=="a" {old=Some(frame["id"].clone());return vec![];}
        let id=if filter=="ab"{"ab"}else{"initial"};
        let project=json!({"id":id,"machine":"local","rootPath":format!("/owned/{id}"),"title":id,"repoKey":null,"pinnedAt":null,"removedAt":null,"createdAt":1,"lastUsedAt":1});
        let mut answer=vec![Reply::Frame(json!({"t":"result","id":frame["id"],"result":{"projects":[project],"next":null}}))];
        if filter=="ab" {if let Some(id)=old.take(){answer.push(Reply::Frame(json!({"t":"result","id":id,"result":{"projects":[],"next":null}})));}}
        answer
    });
    cx.executor().allow_parking();
    cx.update(|cx|{gpui_kit::init(cx);SocketClient::start(Endpoint{port:backend.port,token:"secret".into()},cx);});
    let (chooser,cx)=cx.add_window_view(|window,cx|ProjectChooser::new(None,window,cx));
    let sent=changed.clone();let _observe=cx.update(|_,cx|cx.observe(&chooser,move|_,_|{let _=sent.unbounded_send(());}));
    until("initial project choices",&mut changes,&changed,||{cx.run_until_parked();chooser.read_with(cx,|c,_|!c.loading&&!c.rows.is_empty())}).await;
    chooser.update(cx,|chooser,cx|chooser.search("a".into(),cx));
    until("first filtered request",&mut changes,&changed,||{cx.run_until_parked();seen.lock().unwrap().iter().any(|frame|frame["args"]["filter"]=="a")}).await;
    chooser.update(cx,|chooser,cx|chooser.search("ab".into(),cx));
    until("current filtered result",&mut changes,&changed,||{cx.run_until_parked();chooser.read_with(cx,|c,_|!c.loading&&c.rows.first().is_some_and(|p|p.id=="ab"))}).await;
    chooser.read_with(cx,|chooser,_|assert_eq!(chooser.rows.len(),1));
    assert!(!seen.lock().unwrap().iter().any(|frame|frame["feature"]=="sessions"||frame["method"]=="create"));
}
