use std::sync::{Arc,Mutex};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::*;
use serde_json::{Value,json};
use super::{CommitTimeline,CommitFiles};
use crate::core::socket::{client::SocketClient,endpoint::Endpoint,fake_backend::{FakeBackend,Reply,hello},test_wait::until};

// Pins: g9#11, g9#15, g9#17, g9#18
#[gpui::test]
async fn hidden_history_does_not_read_and_load_more_appends_once(cx: &mut TestAppContext) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new())); let observed=seen.clone();
    let (changed,mut changes)=unbounded();let heard=changed.clone();
    let backend=FakeBackend::start(move |frame| {
        if let Some(reply)=hello(frame) { return reply; }
        if frame["t"]!="call" { return vec![]; }
        observed.lock().unwrap().push(frame.clone());
        let result=match frame["method"].as_str() {
            Some("history") => {
                let second=frame["args"]["cursor"]=="24";
                json!({"commits":[{"shortSha":if second {"bbbbbbb"}else{"aaaaaaa"},"sha":if second {"bbbbbbbb"}else{"aaaaaaaa"},"subject":"Commit","body":"Body","author":"Jesse","committedAt":"2026-11-03T12:00:00Z","refs":[],"parents":[]}],"nextCursor":if second {Value::Null}else{json!("24")}})
            }
            Some("commitFiles")=>json!([{"path":"a.rs","originalPath":null,"letter":"M"}]),
            _=>panic!("unexpected call")
        };
        let _=heard.unbounded_send(());
        vec![Reply::Frame(json!({"t":"result","id":frame["id"],"result":result}))]
    });
    cx.executor().allow_parking();
    cx.update(|cx| { gpui_kit::init(cx); SocketClient::start(Endpoint{port:backend.port,token:"secret".into()},cx); });
    let timeline=cx.new(|cx|CommitTimeline::new("s1".into(),Some("a.rs".into()),false,cx));
    cx.run_until_parked(); assert!(seen.lock().unwrap().is_empty());
    let tell=changed.clone();let _heard=cx.update(|cx|cx.observe(&timeline,move|_,_|{let _=tell.unbounded_send(());}));
    timeline.update(cx,|timeline,cx|timeline.set_active(true,cx));
    until("history loaded",&mut changes,&changed,||{cx.run_until_parked();cx.update(|cx|timeline.read(cx).history.commits.len()==1)}).await;
    timeline.update(cx,|timeline,cx|{timeline.load_more(cx);timeline.load_more(cx);});
    until("second page loaded",&mut changes,&changed,||{cx.run_until_parked();cx.update(|cx|timeline.read(cx).history.commits.len()==2)}).await;
    assert_eq!(seen.lock().unwrap().len(),2);
    assert_eq!(seen.lock().unwrap()[0]["args"]["path"],"a.rs");
    timeline.update(cx,|timeline,cx|timeline.toggle_commit("aaaaaaaa".into(),cx));
    until("commit files loaded",&mut changes,&changed,||{cx.run_until_parked();cx.update(|cx|matches!(timeline.read(cx).files.get("aaaaaaaa"),Some(CommitFiles::Read(files))if files[0].path=="a.rs"))}).await;
    timeline.update(cx,|timeline,cx|timeline.set_path(None,cx));
    until("cleared path read",&mut changes,&changed,||{cx.run_until_parked();seen.lock().unwrap().len()==4}).await;
    assert!(seen.lock().unwrap()[3]["args"]["path"].is_null());
}
