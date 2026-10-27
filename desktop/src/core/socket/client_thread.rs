//! `ClientThread` is the socket's one thread of logic. It owns the open connection, the calls waiting on an answer,
//! the latest call per key, and every subscription, and it blocks on its `Event` channel with no timeout.
//! - A call is sent if the socket is open; while connecting it waits; while `Disconnected` it fails at once.
//! - A newer call with the same key answers the older `Aborted` and sends its `cancel`.
//! - Calls and subscriptions share one id counter, so an `error` names one or the other: on a subscription it drops
//!   it and ends its updates.
//! - When the socket closes: every waiting call settles `Aborted`, it tries to reopen once at once, and on success
//!   sends every subscription again (rows with `after` = their last seq). On failure it's `Disconnected` until
//!   `Reconnect`.

use std::collections::HashMap;
use std::sync::mpsc::{Receiver, Sender};

use futures_channel::mpsc::UnboundedSender;
use futures_channel::oneshot;
use serde_json::Value;

use super::connection::Connection;
use super::connection_state::ConnectionState;
use super::endpoint::Endpoint;
use super::event::Event;
use super::in_frame::InFrame;
use super::outcome::Outcome;
use super::row_update::RowUpdate;
use super::rows_feed::RowsFeed;
use super::update::Update;

pub(super) struct ClientThread {
    endpoint: Endpoint,
    events: Receiver<Event>,
    /// Handed to each connection's reader.
    sender: Sender<Event>,
    connection: Option<Connection>,
    connections_opened: u64,
    state_watch: Option<UnboundedSender<ConnectionState>>,
    calls: HashMap<u64, oneshot::Sender<Outcome<Value>>>,
    /// The newest call per key.
    keys: HashMap<String, u64>,
    topics: HashMap<u64, (String, Option<String>, UnboundedSender<Update>)>,
    rows: HashMap<u64, (RowsFeed, UnboundedSender<RowUpdate>)>,
}

impl ClientThread {
    /// Starts the thread, which opens the socket at once. State changes go to `state_watch`, which the
    /// `SocketClient` entity reads on the foreground.
    pub fn spawn(_endpoint: Endpoint, _events: Receiver<Event>, _sender: Sender<Event>) {
        todo!()
    }

    fn run(&mut self) {
        todo!()
    }

    fn handle(&mut self, _event: Event) {
        todo!()
    }

    fn frame(&mut self, _frame: InFrame) {
        todo!()
    }

    /// The socket closed: waiting calls settle `Aborted`, one try to reopen, then subscribe again or `Disconnected`.
    fn closed(&mut self) {
        todo!()
    }

    fn open(&mut self) {
        todo!()
    }

    fn set_state(&mut self, _state: ConnectionState) {
        todo!()
    }
}
