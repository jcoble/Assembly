//! The `terminal` rows topic and calls against the real desktop backend, through the client's generic rows feed:
//! a new subscriber jumps to the end, typed bytes reach the shell, and its output comes back in order with byte
//! offsets as seqs. Pins docs/behavior/terminal.md.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use futures_executor::block_on;
use serde_json::{Value, json};

use super::outcome::Outcome;
use super::real_backend::RealBackend;
use super::row_update::RowUpdate;
use super::test_client::{TestClient as Client, outcome};

fn call(client: &Client, method: &str, args: Value) -> Value {
    let (_, answer) = client.call_with("terminal", method, args, None);
    match outcome(answer) {
        Outcome::Ok(result) => result,
        other => panic!("terminal/{method}: {other:?}"),
    }
}

// Pins: terminal#1, terminal#2, terminal#3, terminal#5
#[test]
fn a_sessions_terminal_runs_what_is_typed_and_streams_its_output_in_order() {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let mut client = Client::start_with(backend.endpoint());
    client.state();
    client.state();
    let started = call(
        &client,
        "start",
        json!({"owner": "s1", "shell": "/bin/sh", "cols": 80, "rows": 24}),
    );
    let id = started["id"].as_str().expect("an id").to_string();
    assert_eq!(started["cols"], 80);

    let mut rows = client.rows_on::<Value>("terminal", &id, None);
    let Some(RowUpdate::Jump { seq: mut last }) = block_on(rows.recv()).ok() else {
        panic!("a new subscriber starts with a jump to the end");
    };
    call(
        &client,
        "write",
        json!({"terminal": id, "data": STANDARD.encode("echo hi-$((40+2))\n")}),
    );
    let mut seen = Vec::new();
    while !String::from_utf8_lossy(&seen).contains("hi-42") {
        match block_on(rows.recv()).ok() {
            Some(RowUpdate::Row(row)) => {
                assert_eq!(row["prevSeq"], last, "each row continues from the last");
                last = row["seq"].as_i64().expect("a seq");
                let data = STANDARD
                    .decode(row["data"].as_str().unwrap_or(""))
                    .expect("base64");
                seen.extend(data);
            }
            other => panic!("expected output rows, got {other:?}"),
        }
    }
    assert_eq!(seen.len() as i64, last, "seqs count bytes since the start");

    let again = call(&client, "start", json!({"owner": "s1"}));
    assert_eq!(
        again["id"], started["id"],
        "the session gets its running shell back"
    );
    let piece = call(&client, "read", json!({"terminal": id, "offset": 0}));
    let replay = STANDARD.decode(piece["data"].as_str().unwrap()).unwrap();
    assert!(replay.starts_with(&seen), "the replay is the same bytes");
    call(&client, "close", json!({"terminal": id}));
}
