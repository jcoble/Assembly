//! `Event` is everything the client thread waits on, on one channel with no timeout: the window's requests, the
//! reader thread's frames, the socket closing, and `Reconnect`. Frames and closes carry their connection's number, so
//! a dropped connection's late frames and close are ignored.

use futures_channel::mpsc::UnboundedSender;
use futures_channel::oneshot;
use serde_json::Value;

use super::connection_state::ConnectionState;
use super::endpoint::Endpoint;
use super::in_frame::InFrame;
use super::outcome::Outcome;
use super::row_update::RowUpdate;
use super::update::Update;

pub(super) enum Event {
    Call {
        id: u64,
        feature: String,
        method: String,
        args: Value,
        key: Option<String>,
        answer: oneshot::Sender<Outcome<Value>>,
    },
    Cancel {
        id: u64,
    },
    Subscribe {
        id: u64,
        topic: String,
        key: Option<String>,
        updates: UnboundedSender<Update>,
    },
    SubscribeRows {
        id: u64,
        session_id: String,
        after: Option<i64>,
        rows: UnboundedSender<RowUpdate>,
    },
    Unsubscribe {
        id: u64,
    },
    Frame {
        connection: u64,
        frame: InFrame,
    },
    Closed {
        connection: u64,
    },
    Reconnect(Endpoint),
    /// Where state changes go: the foreground task that updates the `SocketClient` entity.
    WatchState(UnboundedSender<ConnectionState>),
}
