//! Actual nullable route discovery is separate from SQL rail paging and never names an Engine by its saved route.
use serde_json::json;
use crate::core::state::machine::Machine;
use super::rail_sources::routes;
use crate::features::session::client::source_resolution;
#[test]
fn online_remote_sources_outside_the_current_rail_page_are_discovered_by_explicit_routes() {
    let remote:Machine=serde_json::from_value(json!({"name":"Saved B","kind":"remote","reachable":true,"checked":true})).unwrap();
    let offline:Machine=serde_json::from_value(json!({"name":"Offline","kind":"remote","reachable":false,"checked":true})).unwrap();
    assert_eq!(routes([&remote,&offline]),vec![None,Some("Saved B".into())]);
    let actual=source_resolution::parse_reply(json!({"machine":"actual-B","generation":7,"routeMachine":"Saved B"}),Some("Saved B"),Some("actual-A")).unwrap();
    assert_eq!(actual.machine,"actual-B");
    assert!(source_resolution::parse_reply(json!({"machine":"Saved B","generation":0,"routeMachine":"Saved B"}),Some("Saved B"),Some("actual-A")).is_err());
    assert_eq!(source_resolution::build_args(None).unwrap(),json!({"routeMachine":null}));
}

// Source-derived real socket fixture, not native/provider acceptance.
#[gpui::test]
async fn remote_child_outside_the_sql_rail_page_uses_the_one_discovered_source_scope(cx:&mut gpui::TestAppContext) {
    use futures_channel::mpsc::unbounded;
    use gpui_kit::AppContext as _;
    use crate::core::{socket::{client::SocketClient,endpoint::Endpoint,fake_backend::{FakeBackend,Reply},test_wait::until},engine::{EngineFeed,SessionKey},state::{machines::Machines,selected_session::SelectedSession}};
    use crate::features::sessions::sessions_pane::SessionsPane;
    let backend=FakeBackend::start(|frame| {
        let id=frame["id"].clone();
        let result=|value|vec![Reply::Frame(json!({"t":"result","id":id,"result":value}))];
        match(frame["t"].as_str(),frame["topic"].as_str(),frame["method"].as_str()) {
            (Some("hello"),..)=>vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"Friendly A","engine":"actual-A"}))],
            (Some("sub"),Some("machines"),_)=>vec![Reply::Frame(json!({"t":"state","sub":id,"items":[{"key":"Saved B","item":{"name":"Saved B","kind":"remote","reachable":true,"checked":true}}]}))],
            (Some("call"),_,Some("resolve_source"))=>{
                let remote=frame["args"]["routeMachine"]=="Saved B";
                result(json!({"machine":if remote{"actual-B"}else{"actual-A"},"generation":if remote{7}else{0},"routeMachine":if remote{Some("Saved B")}else{None}}))
            },
            (Some("sub"),Some("sessions"),_) if frame["stream"]=="engine"=>{
                let remote=frame["machine"]=="actual-B";
                let owner=if remote{"actual-B"}else{"actual-A"};
                let row=json!({"entityKind":"session","machine":owner,"id":if remote{"child-off-page"}else{"parent"},"version":1,"parentId":if remote{Some("parent")}else{None},"parentMachine":if remote{Some("actual-A")}else{None},"provider":"fixture","folder":"/recorded","state":"idle","activityState":"idle","epoch":1});
                vec![Reply::Frame(json!({"t":"engine_snapshot","id":id,"machine":owner,"through":1,"items":[row],"more":false}))]
            },
            (Some("sub"),Some("needs_me"),_) if frame["stream"]=="engine"=>vec![Reply::Frame(json!({"t":"engine_snapshot","id":id,"machine":frame["machine"],"through":1,"items":[],"more":false}))],
            (Some("call"),_,_)=>result(json!({})),
            _=>vec![],
        }
    });
    cx.executor().allow_parking();
    let(socket,feed)=cx.update(|cx|{
        gpui_kit::init(cx);SelectedSession::init(cx);
        let socket=SocketClient::start(Endpoint{port:backend.port,token:"fixture".into()},cx);
        Machines::start(cx);(socket,EngineFeed::global(cx))
    });
    let(pane,visual)=cx.add_window_view(SessionsPane::new);
    let(changed,mut changes)=unbounded();let tell=changed.clone();let connected=changed.clone();
    let _feed=visual.update(|_,cx|cx.observe(&feed,move|_,_|{let _=tell.unbounded_send(());}));
    let _socket=visual.update(|_,cx|cx.observe(&socket,move|_,_|{let _=connected.unbounded_send(());}));
    let parent=SessionKey{machine:"actual-A".into(),id:"parent".into()};
    until("remote child outside empty rail",&mut changes,&changed,||visual.update(|_,cx|feed.read(cx).children_of(&parent).len()==1)).await;
    visual.update(|_,cx| {
        assert!(pane.read(cx).rail.sessions.is_empty());
        let children=feed.read(cx).children_of(&parent);
        assert_eq!(children[0].key.machine,"actual-B");assert_eq!(children[0].key.id,"child-off-page");
        assert_eq!(pane.read(cx).engine_watches.len(),4,"one shared sessions/needs lease per actual source");
    });
    drop(pane);
}
