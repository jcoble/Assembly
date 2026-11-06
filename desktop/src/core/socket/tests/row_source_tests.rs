//! Wire fixtures assert propagation only; the backend remains the source/record authority.
use super::out_frame::OutFrame;
use super::row_source::{ExpectedRowSource, page_args};
use serde_json::json;

#[test]
fn local_and_remote_expected_sources_preserve_the_exact_frozen_tuple() {
    for source in [
        ExpectedRowSource {
            machine: "local-engine".into(),
            generation: 0,
            route_machine: None,
        },
        ExpectedRowSource {
            machine: "remote-engine".into(),
            generation: 17,
            route_machine: Some("saved-route".into()),
        },
    ] {
        let frame = serde_json::to_value(OutFrame::Sub {
            id: 4,
            topic: "rows",
            key: Some("same"),
            after: Some(8),
            expected_source: Some(&source),
        })
        .unwrap();
        let value = serde_json::to_value(&source).unwrap();
        assert_eq!(frame["expectedSource"], value);
        assert_eq!(
            page_args("same", 9, 100, Some(&source))["expectedSource"],
            value
        );
        if source.generation == 0 {
            assert!(value["routeMachine"].is_null());
        }
    }
    assert_eq!(
        page_args("legacy", 9, 100, None),
        json!({"session":"legacy","before":9,"limit":100})
    );
    let frame = serde_json::to_value(OutFrame::Sub {
        id: 4,
        topic: "rows",
        key: Some("legacy"),
        after: None,
        expected_source: None,
    })
    .unwrap();
    assert!(frame.get("expectedSource").is_none());
}

#[test]
fn a_gap_and_reconnect_retain_the_captured_source_instead_of_refreshing_its_route() {
    use super::{
        event::Event,
        fake_backend::{FakeBackend, Reply, hello},
        test_client::{TestClient, row},
    };
    use futures_channel::mpsc::unbounded;
    let source = ExpectedRowSource {
        machine: "actual-engine".into(),
        generation: 7,
        route_machine: Some("captured-route".into()),
    };
    let mut subscriptions = 0;
    let backend = FakeBackend::start(move |frame| {
        if let Some(welcome) = hello(frame) {
            return welcome;
        }
        if frame["t"] != "sub" {
            return vec![];
        }
        subscriptions += 1;
        let id = &frame["id"];
        let mut frames = vec![Reply::Frame(
            json!({"t":"state","sub":id,"seq":0,"items":[]}),
        )];
        if subscriptions == 1 {
            frames.push(Reply::Frame(json!({"t":"rows","sub":id,"row":row(2,1)})));
        } else if subscriptions == 2 {
            frames.push(Reply::Drop);
        }
        frames
    });
    let client = TestClient::start(backend.port);
    let (rows, _received) = unbounded();
    client
        .events
        .send(Event::SubscribeRows {
            id: client.id(),
            session_id: "same".into(),
            after: Some(0),
            expected_source: Some(source.clone()),
            rows,
        })
        .unwrap();
    let expected = serde_json::to_value(source).unwrap();
    let mut observed = 0;
    while observed < 3 {
        let frame = backend.next();
        if frame["t"] == "sub" {
            assert_eq!(frame["expectedSource"], expected);
            assert_eq!(frame["key"], "same");
            observed += 1;
        }
    }
}

#[test]
fn an_existing_rows_error_frame_is_a_terminal_typed_refusal_instead_of_a_silent_watch_end() {
    use super::{
        event::Event,
        fake_backend::{FakeBackend, Reply, hello},
        row_update::RowUpdate,
        test_client::TestClient,
    };
    use futures_channel::mpsc::unbounded;
    use futures_executor::block_on;
    let backend = FakeBackend::start(|frame| {
        if let Some(welcome) = hello(frame) {
            return welcome;
        }
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"error","id":frame["id"],"code":"source_changed","message":"The selected session source changed."}),
            )];
        }
        vec![]
    });
    let client = TestClient::start(backend.port);
    let (rows, mut updates) = unbounded();
    client
        .events
        .send(Event::SubscribeRows {
            id: client.id(),
            session_id: "same".into(),
            after: None,
            expected_source: None,
            rows,
        })
        .unwrap();
    let update = block_on(updates.recv()).unwrap();
    assert!(
        matches!(update,RowUpdate::Refused{code,message} if code=="source_changed" && message=="The selected session source changed.")
    );
    assert!(block_on(updates.recv()).is_err());
}
