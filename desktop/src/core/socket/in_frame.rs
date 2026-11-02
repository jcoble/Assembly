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
    #[serde(rename = "engine_snapshot")]
    EngineSnapshot {
        id: u64,
        machine: String,
        through: i64,
        items: Vec<Value>,
        more: bool,
        at: Option<super::engine_subscription::CursorAt>,
    },
    #[serde(rename = "engine_events")]
    EngineEvents {
        id: u64,
        machine: String,
        after: i64,
        through: i64,
        events: Vec<super::engine_subscription::EngineEvent>,
        more: bool,
        at: Option<super::engine_subscription::CursorAt>,
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

#[cfg(test)]
mod engine_wire_tests {
    use super::InFrame;
    use serde_json::json;

    #[test]
    fn providers_snapshot_pages_are_supported_on_the_existing_socket() {
        for more in [true, false] {
            let frame = json!({"t":"engine_snapshot", "id":17, "machine":"owner-engine",
                "through":41, "items":[], "more":more});
            let parsed = serde_json::from_value::<InFrame>(frame);
            assert!(
                parsed.is_ok(),
                "a bounded providers snapshot must not break the socket: {parsed:?}"
            );
        }
    }

    #[test]
    fn empty_engine_events_can_advance_the_global_cursor() {
        let frame = json!({"t":"engine_events", "id":17, "machine":"owner-engine",
            "after":41, "through":49, "events":[], "more":false});
        let parsed = serde_json::from_value::<InFrame>(frame);
        assert!(
            parsed.is_ok(),
            "an empty committed event page must reach the subscriber: {parsed:?}"
        );
    }
}
