//! `InFrame` is a frame the backend sends (dotnet/Assembly.Infrastructure/Socket/SocketDispatcher.cs). `t` names it.
//! `state` on the `rows` topic has `seq` and no items; the `rows` frames after it continue from that seq.

use serde::Deserialize;
use serde_json::Value;

#[derive(Debug, Deserialize)]
#[serde(tag = "t", rename_all = "lowercase")]
pub(super) enum InFrame {
    #[serde(rename = "engine_snapshot")]
    EngineSnapshot {
        id: u64,
        machine: String,
        through: i64,
        items: Vec<Value>,
        more: bool,
        at: Option<super::engine_update::CursorAt>,
    },
    #[serde(rename = "engine_events")]
    EngineEvents {
        id: u64,
        machine: String,
        after: i64,
        through: i64,
        events: Vec<Value>,
        at: Option<super::engine_update::CursorAt>,
    },
    Welcome {
        protocol: u32,
        machine: String,
    },
    Result {
        id: u64,
        result: Value,
    },
    Error {
        id: u64,
        code: String,
        message: String,
        machine: Option<String>,
    },
    State {
        sub: u64,
        seq: Option<i64>,
        items: Vec<Entry>,
    },
    Change {
        sub: u64,
        key: String,
        item: Value,
    },
    Rows {
        sub: u64,
        row: super::row::Row,
    },
}

/// One item of a `state` frame.
#[derive(Debug, Deserialize)]
pub(super) struct Entry {
    pub key: String,
    pub item: Value,
}
