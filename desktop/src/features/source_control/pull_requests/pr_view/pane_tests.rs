use super::PrPane;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

const PROJECT: &str = "11111111-1111-1111-1111-111111111111";

#[gpui::test]
async fn a_row_with_no_session_reads_through_its_project_and_the_source_it_just_resolved(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        let _ = heard.unbounded_send(());
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        observed.lock().unwrap().push(frame.clone());
        let result = match frame["method"].as_str() {
            Some("list") => json!({"projects":[{
                "id":PROJECT,"machine":"lane","rootPath":"/lane/r","title":"r",
                "repoKey":"github.com/o/r","createdAt":1,
            }],"next":null}),
            Some("resolve_source") => {
                json!({"machine":"engine","generation":3,"routeMachine":"lane"})
            }
            Some("prs") => json!({"items":[],"cursor":null,"total":0}),
            _ => return vec![],
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = || Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint(), cx);
        SocketClient::start_bulk(endpoint(), cx);
    });
    let window = cx.add_empty_window();
    let pane = window.update(|window, cx| cx.new(|cx| PrPane::new(None, window, cx)));
    until("the project row", &mut changes, &changed, || {
        window.run_until_parked();
        window.update(|_, cx| {
            let projects = pane.read(cx).projects.clone();
            projects.is_some_and(|projects| !projects.read(cx).rows().is_empty())
        })
    })
    .await;
    window.update(|_, cx| {
        pane.update(cx, |pane, cx| pane.select_pr(Some("O/R".into()), 7, cx))
    });
    until("the project read", &mut changes, &changed, || {
        window.run_until_parked();
        seen.lock().unwrap().iter().any(|frame| frame["method"] == "pr")
    })
    .await;
    let seen = seen.lock().unwrap();
    let methods: Vec<_> = seen.iter().map(|frame| frame["method"].clone()).collect();
    let resolved = methods.iter().position(|method| method == "resolve_source");
    let read = methods.iter().position(|method| method == "pr");
    assert!(resolved < read, "{methods:?}");
    let args = &seen[read.unwrap()]["args"];
    assert_eq!(args["session"], Value::Null);
    assert_eq!(args["projectId"], PROJECT);
    assert_eq!(args["number"], 7);
    assert_eq!(args["expectedSource"]["machine"], "engine");
    assert_eq!(args["expectedSource"]["routeMachine"], "lane");
    assert!(args.get("rootPath").is_none());
}
