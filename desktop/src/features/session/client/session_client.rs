//! `SessionClient` is what the session view needs from the socket: a session's rows from a seq on, its history a
//! page at a time, and calls. Every method answers off the main thread; nothing here blocks the window. A newer
//! load for the same key replaces an older one (the socket client's latest-wins), and dropping a task cancels it.

use gpui_kit::*;
use serde_json::Value;

use crate::features::session::rows::row::Row;

pub trait SessionClient: 'static {
    /// The session's rows after `after`, then each new one, in seq order, until the returned subscription drops.
    /// `on_rows` gets each batch that arrived together, on the main thread.
    fn rows(&self, session: &str, after: Option<i64>, on_rows: Box<dyn FnMut(Vec<Row>, &mut App)>) -> Subscription;

    /// `rows/page`: the newest rows of items that start below `before`.
    fn page(&self, session: &str, before: Option<i64>, limit: u32) -> Task<Result<RowPage, CallError>>;

    /// Any other call, by feature and method (send/send, send/stop, rows/tool, attachments/begin, …).
    fn call(&self, feature: &str, method: &str, args: Value) -> Task<Result<Value, CallError>>;
}

/// `rows/page`'s answer.
#[derive(Clone, Debug, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RowPage {
    pub rows: Vec<Row>,
    pub has_before: bool,
    /// The run going now when the page was read, if any.
    pub active_run_id: Option<String>,
}

/// Why a call has no result. `Aborted` (a newer call took its place, or the socket closed) shows nothing.
#[derive(Clone, Debug, PartialEq)]
pub enum CallError {
    Failed { code: String, message: String },
    Aborted,
}
