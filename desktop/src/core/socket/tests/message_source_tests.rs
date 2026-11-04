//! The existing messages stream keeps its original source assertion and returns an actual terminal refusal.
use crate::core::socket::{
    engine_update::EngineUpdate,
    event::Event,
    fake_backend::{FakeBackend, Reply, hello},
    row_source::ExpectedRowSource,
    test_client::{DEADLINE, TestClient},
};
use futures_channel::mpsc::unbounded;
use futures_executor::block_on;
use serde_json::json;
#[test]
fn a_message_source_refusal_keeps_the_original_tuple_and_closes_without_resubscribing() {
    let backend = FakeBackend::start(|frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] == "sub" {
            vec![Reply::Frame(
                json!({"t":"error","id":frame["id"],"code":"source_changed","message":"Captured generation replaced"}),
            )]
        } else {
            vec![]
        }
    });
    let client = TestClient::start(backend.port);
    let id = client.id();
    let (updates, mut received) = unbounded();
    let source = ExpectedRowSource {
        machine: "actual-owner".into(),
        generation: 37,
        route_machine: Some("captured-route".into()),
    };
    client
        .events
        .send(Event::SubscribeEngine {
            id,
            message: "prompt/s/c".into(),
            machine: Some("actual-owner".into()),
            expected_source: Some(source.clone()),
            updates,
        })
        .unwrap();
    let sub = loop {
        let frame = backend.next();
        if frame["t"] == "sub" {
            break frame;
        }
    };
    assert_eq!(sub["stream"], "engine");
    assert_eq!(sub["topic"], "messages");
    assert_eq!(sub["machine"], "actual-owner");
    assert_eq!(sub["expectedSource"], json!(source));
    let (sent, answer) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let first = block_on(received.recv()).unwrap();
        let ended = block_on(received.recv()).is_err();
        sent.send((first, ended)).unwrap();
    });
    let (update, ended) = answer
        .recv_timeout(DEADLINE)
        .expect("terminal source refusal");
    assert!(
        matches!(update,EngineUpdate::Refused{code,message} if code=="source_changed"&&message=="Captured generation replaced")
    );
    assert!(ended);
}
