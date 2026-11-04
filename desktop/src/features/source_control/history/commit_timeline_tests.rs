use super::{CommitFiles, CommitTimeline};
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{AppContext as _, ScrollDelta, ScrollEvent, VisualTestContext, point, px, size};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

// Pins: g9#11, g9#15, g9#17, g9#18
#[gpui::test]
async fn hidden_history_does_not_read_and_load_more_appends_once(cx: &mut TestAppContext) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return vec![];
        }
        observed.lock().unwrap().push(frame.clone());
        let result = match frame["method"].as_str() {
            Some("history") => {
                let second = frame["args"]["cursor"] == "24";
                json!({"commits":[{"shortSha":if second {"bbbbbbb"}else{"aaaaaaa"},"sha":if second {"bbbbbbbb"}else{"aaaaaaaa"},"subject":"Commit","body":"Body","author":"Jesse","committedAt":"2026-11-03T12:00:00Z","refs":[],"parents":[]}],"nextCursor":if second {Value::Null}else{json!("24")}})
            }
            Some("commitFiles") => json!([{"path":"a.rs","originalPath":null,"letter":"M"}]),
            _ => panic!("unexpected call"),
        };
        let _ = heard.unbounded_send(());
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let origin = crate::core::socket::row_source::ExpectedRowSource {
        machine: "owner-engine".into(), generation: 7, route_machine: Some("workbox-test".into()),
    };
    let timeline = cx.new(|cx| CommitTimeline::new("s1".into(), Some("a.rs".into()), Some(origin.clone()), false, cx));
    cx.run_until_parked();
    assert!(seen.lock().unwrap().is_empty());
    let tell = changed.clone();
    let _heard = cx.update(|cx| {
        cx.observe(&timeline, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    timeline.update(cx, |timeline, cx| timeline.set_active(true, cx));
    until("history loaded", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| timeline.read(cx).history.commits.len() == 1)
    })
    .await;
    timeline.update(cx, |timeline, cx| {
        timeline.load_more(cx);
        timeline.load_more(cx);
    });
    until("second page loaded", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| timeline.read(cx).history.commits.len() == 2)
    })
    .await;
    assert_eq!(seen.lock().unwrap().len(), 2);
    assert_eq!(seen.lock().unwrap()[0]["args"]["path"], "a.rs");
    timeline.update(cx, |timeline, cx| {
        timeline.toggle_commit("aaaaaaaa".into(), cx)
    });
    until("commit files loaded",&mut changes,&changed,||{cx.run_until_parked();cx.update(|cx|matches!(timeline.read(cx).files.get("aaaaaaaa"),Some(CommitFiles::Read(files))if files[0].path=="a.rs"))}).await;
    timeline.update(cx, |timeline, cx| timeline.set_path(None, cx));
    until("cleared path read", &mut changes, &changed, || {
        cx.run_until_parked();
        seen.lock().unwrap().len() == 4
    })
    .await;
    assert!(seen.lock().unwrap()[3]["args"]["path"].is_null());
    for request in seen.lock().unwrap().iter() {
        assert_eq!(request["args"]["expectedSource"], serde_json::to_value(&origin).unwrap());
    }
}
fn drawn(visual: &mut VisualTestContext) {
    visual.run_until_parked();
    visual.update(|window, cx| window.draw(cx).clear(cx));
}
// Pins: g9#15
#[gpui::test]
async fn scrolling_to_the_end_loads_once(cx: &mut TestAppContext) {
    let cursors = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = cursors.clone();
    let (changed, mut changes) = unbounded();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return vec![];
        }
        let cursor = frame["args"]["cursor"].clone();
        let second = cursor == "30";
        observed.lock().unwrap().push(cursor);
        let page = if second { "b" } else { "a" };
        let commits: Vec<Value> = (0..30)
            .map(|at| json!({"shortSha":format!("{page}{at:06}"),"sha":format!("{page}{at:039}"),"subject":"Commit","body":"","author":"Jesse","committedAt":"2026-11-03T12:00:00Z","refs":[],"parents":[]}))
            .collect();
        let next = if second { Value::Null } else { json!("30") };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"commits":commits,"nextCursor":next}}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let (timeline, visual) =
        cx.add_window_view(|_, cx| CommitTimeline::new("s1".into(), None, None, true, cx));
    visual.simulate_resize(size(px(1000.), px(400.)));
    visual.update(|window, _| window.activate_window());
    let tell = changed.clone();
    let _heard = visual.update(|_, cx| {
        cx.observe(&timeline, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    until("first page", &mut changes, &changed, || {
        drawn(visual);
        visual.update(|_, cx| timeline.read(cx).history.commits.len() == 30)
    })
    .await;
    assert_eq!(cursors.lock().unwrap().len(), 1, "the first page alone asks nothing more");
    let wheel = ScrollEvent {
        position: point(px(500.), px(200.)),
        delta: ScrollDelta::Pixels(point(px(0.), px(-600.))),
        ..ScrollEvent::default()
    };
    // Rows are measured as they come into view, so the end takes several turns of the wheel.
    for _ in 0..60 {
        if cursors.lock().unwrap().len() > 1 {
            break;
        }
        visual.simulate_event(wheel.clone());
        drawn(visual);
    }
    visual.simulate_event(wheel.clone());
    until("second page", &mut changes, &changed, || {
        drawn(visual);
        visual.update(|_, cx| timeline.read(cx).history.commits.len() == 60)
    })
    .await;
    for _ in 0..4 {
        visual.simulate_event(wheel.clone());
        drawn(visual);
    }
    assert_eq!(*cursors.lock().unwrap(), vec![Value::Null, json!("30")]);
}
