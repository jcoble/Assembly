//! `SessionClient` is what the session view needs from the socket: a session's rows from a seq on, and calls. The
//! window's is `SocketSessionClient`, over the socket client; tests use `FakeClient`. It answers off the main thread
//! and never blocks the window. Dropping a call's task cancels the call. Reconnect and the seq gap checks are the
//! socket client's.

use gpui_kit::*;
use serde_json::Value;

pub use crate::core::socket::row_update::RowUpdate;

pub trait SessionClient: 'static {
    /// Subscribes to a session's rows: `after` is the last seq held, `None` on a first open. Events arrive on the
    /// main thread until the subscription drops.
    fn subscribe_rows(
        &self,
        session: &str,
        after: Option<i64>,
        sink: RowsSink,
        cx: &App,
    ) -> Subscription;

    /// One call, by feature and method (rows/page, rows/tool, send/send, attachments/begin, …). With `key`, a newer
    /// call with the same key answers this one `Aborted` and cancels it, so a fast switch never waits on a stale load.
    fn call(
        &self,
        feature: &str,
        method: &str,
        args: Value,
        key: Option<&str>,
        cx: &App,
    ) -> Task<Result<Value, CallError>>;
}

/// Where a rows subscription's events go, on the main thread.
pub type RowsSink = Box<dyn FnMut(RowUpdate, &mut App)>;

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
