//! `InFrame` is a frame the backend sends (dotnet/Assembly.Infrastructure/Socket/SocketDispatcher.cs). `t` names it.
//! `state` on the `rows` topic has `seq` and no items; the `rows` frames after it continue from that seq.

use serde::Deserialize;
use serde_json::Value;

#[derive(Debug, Deserialize)]
#[serde(tag = "t", rename_all = "lowercase")]
pub(super) enum InFrame {
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
    /// One row, as its topic sends it; its `seq` and `prevSeq` are read by the client thread.
    Rows {
        sub: u64,
        row: Value,
    },
}

/// One item of a `state` frame.
#[derive(Debug, Deserialize)]
pub(super) struct Entry {
    pub key: String,
    pub item: Value,
}
