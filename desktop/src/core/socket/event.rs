//! `Event` is everything the client thread waits on, on one channel: the window's requests, the
//! reader thread's frames, the socket closing, and `Reconnect`.

use futures_channel::mpsc::UnboundedSender;
use futures_channel::oneshot;
use serde_json::Value;

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
    Frame(InFrame),
    /// A ping the reader got: the client thread, the only writer, answers it.
    Ping(Vec<u8>),
    /// The open connection's reader ended. There's one reader at a time: a new connection opens only after this.
    Closed,
    /// The reader got a frame this client can't read and ended. Reopening at once would get the same frame again, so
    /// the client goes `Disconnected` and tries again only after the wait.
    Broken,
    Reconnect(Endpoint),
}
