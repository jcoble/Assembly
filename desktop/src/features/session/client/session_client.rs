//! `SessionClient` is what the session view needs from the socket: a session's rows from a seq on, and calls, in
//! the shapes of lane g0-socket's client (`subscribe_rows`, `call` with a latest-wins key, `RowUpdate`, `ErrorCode`)
//! so the swap to it is a change of `use`s. It answers off the main thread and never blocks the window. Dropping a
//! call's task cancels the call. Reconnect and the seq gap checks are the socket client's.

use gpui_kit::*;
use serde_json::Value;

use crate::features::session::rows::row::Row;

pub trait SessionClient: 'static {
    /// Subscribes to a session's rows: `after` is the last seq held, `None` on a first open. Events arrive on the
    /// main thread until the subscription drops.
    fn subscribe_rows(&self, session: &str, after: Option<i64>, sink: RowsSink) -> Subscription;

    /// One call, by feature and method (rows/page, rows/tool, send/send, attachments/begin, …). With `key`, a newer
    /// call with the same key answers this one `Aborted` and cancels it, so a fast switch never waits on a stale load.
    fn call(
        &self,
        feature: &str,
        method: &str,
        args: Value,
        key: Option<&str>,
    ) -> Task<Result<Value, CallError>>;
}

/// Where a rows subscription's events go, on the main thread.
pub type RowsSink = Box<dyn FnMut(RowsEvent, &mut App)>;

pub enum RowsEvent {
    Row(Box<Row>),
    /// The rows before `seq` weren't sent (a first open, or one too far behind): drop what's held and load the page
    /// below `seq + 1`.
    Jump {
        seq: i64,
    },
}

/// Why a call has no result: the backend's error codes (SocketFrames.cs), with its message, or `Aborted` (a newer
/// call took its key, or it was cancelled), which shows nothing.
#[derive(Clone, Debug, PartialEq)]
pub enum CallError {
    NotFound(String),
    BadArgs(String),
    Failed(String),
    Timeout(String),
    Unreachable { machine: String },
    Aborted,
}
