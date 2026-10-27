//! The client thread's `rows` rules against `FakeBackend` (in order, no repeats, a gap re-subscribes from the last
//! seq, a jump), and the one-writer rule under a ping mid-burst.

use futures_executor::block_on;
use serde_json::{Value, json};

use super::fake_backend::{FakeBackend, Reply, hello};
use super::row_update::RowUpdate;
use super::test_client::{TestClient as Client, row};
use super::update::Update;

// Pins: g0-socket#9
#[test]
fn rows_pass_in_order_skip_repeats_and_resubscribe_after_the_last_seq_on_a_gap() {
    let mut subs = 0;
    let backend = FakeBackend::start(move |frame| {
        hello(frame).unwrap_or_else(|| {
            if frame["t"] != "sub" {
                return vec![];
            }
            subs += 1;
            let sub = frame["id"].clone();
            let state =
                |seq: i64| Reply::Frame(json!({"t": "state", "sub": sub, "seq": seq, "items": []}));
            let rows = |r: Value| Reply::Frame(json!({"t": "rows", "sub": sub, "row": r}));
            match subs {
                // After 3: 4 passes, 4 again is skipped, 6 after 4 is a gap.
                1 => vec![state(3), rows(row(4, 3)), rows(row(4, 3)), rows(row(6, 5))],
                // After 4: the missed 5, then 6.
                _ => vec![state(4), rows(row(5, 4)), rows(row(6, 5))],
            }
        })
    });
    let client = Client::start(backend.port);
    let mut rows = client.rows(Some(3));
    let mut seqs = vec![];
    while seqs.len() < 3 {
        match block_on(rows.recv()).ok() {
            Some(RowUpdate::Row(row)) => seqs.push(row.seq),
            other => panic!("expected a row, got {other:?}"),
        }
    }
    assert_eq!(seqs, [4, 5, 6]);
    backend.next(); // hello
    let first = backend.next();
    assert_eq!(
        (
            first["topic"].clone(),
            first["key"].clone(),
            first["after"].clone()
        ),
        (json!("rows"), json!("s1"), json!(3))
    );
    assert_eq!(backend.next(), json!({"t": "unsub", "id": first["id"]}));
    let again = backend.next();
    assert_eq!(
        (again["t"].clone(), again["after"].clone()),
        (json!("sub"), json!(4))
    );
    assert_ne!(again["id"], first["id"]);
}

// Pins: g0-socket#9
#[test]
fn a_rows_state_past_the_after_sent_is_a_jump() {
    let backend = FakeBackend::start(|frame| {
        hello(frame).unwrap_or_else(|| match frame["t"].as_str() {
            Some("sub") => {
                let sub = frame["id"].clone();
                vec![
                    Reply::Frame(json!({"t": "state", "sub": sub, "seq": 900, "items": []})),
                    Reply::Frame(json!({"t": "rows", "sub": sub, "row": row(901, 900)})),
                ]
            }
            _ => vec![],
        })
    });
    let client = Client::start(backend.port);
    let mut rows = client.rows(Some(3));
    assert!(matches!(
        block_on(rows.recv()).ok(),
        Some(RowUpdate::Jump { seq: 900 })
    ));
    assert!(matches!(block_on(rows.recv()).ok(), Some(RowUpdate::Row(row)) if row.seq == 901));
}

// Only the client thread writes: a ping in the middle of a burst is answered, and every frame around it arrives.
#[test]
fn a_ping_mid_burst_is_answered_and_no_frame_is_lost() {
    let backend = FakeBackend::start(|frame| {
        hello(frame).unwrap_or_else(|| match frame["t"].as_str() {
            Some("sub") => {
                let sub = frame["id"].clone();
                let change = |n: i64| {
                    Reply::Frame(
                        json!({"t": "change", "sub": sub, "key": n.to_string(), "item": n}),
                    )
                };
                let mut replies =
                    vec![Reply::Frame(json!({"t": "state", "sub": sub, "items": []}))];
                replies.extend((0..50).map(change));
                replies.push(Reply::Ping);
                replies.extend((50..100).map(change));
                replies
            }
            _ => vec![],
        })
    });
    let client = Client::start(backend.port);
    let (_, mut updates) = client.subscribe("burst");
    assert!(matches!(
        block_on(updates.recv()).ok(),
        Some(Update::State(_))
    ));
    for n in 0..100 {
        assert!(
            matches!(block_on(updates.recv()).ok(), Some(Update::Change { item, .. }) if item == json!(n))
        );
    }
    backend.next(); // hello
    backend.next(); // sub
    assert_eq!(backend.next(), json!({"t": "pong"}));
}
