//! `ClientThread` is the socket's one thread of logic. It owns the open connection, the calls waiting on an answer,
//! the latest call per key, and every subscription, and it blocks on its `Event` channel (with a timeout only while `Disconnected`).
//! - A call is sent if the socket is open; while it's opening, events wait in the channel; while `Disconnected` it
//!   fails at once.
//! - A newer call with the same key answers the older `Aborted` and sends its `cancel`.
//! - Calls and subscriptions share one id counter, so an `error` names one or the other: on a subscription it drops
//!   it and ends its updates.
//! - When the socket closes: every waiting call settles `Aborted`, it tries to reopen once at once, and on success
//!   sends every subscription again (rows with `after` = their last seq). On failure it's `Disconnected`, and tries
//!   again after a short wait that doubles up to 5 s, or at once on `Reconnect` (the backend's ready line).
//! - A connection that closes before its first frame doesn't get the reopen at once again: it waits like a failed
//!   open, so a backend that welcomes and drops can't make it loop.

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::AtomicU64;
use std::sync::mpsc::{Receiver, RecvTimeoutError, Sender};
use std::thread;
use std::time::{Duration, Instant};

use futures_channel::mpsc::UnboundedSender;
use futures_channel::oneshot;
use serde_json::Value;

use super::call_failure::CallFailure;
use super::connection::Connection;
use super::connection_state::ConnectionState;
use super::endpoint::Endpoint;
use super::error_code::ErrorCode;
use super::event::Event;
use super::open_failure::OpenFailure;
use super::out_frame::OutFrame;
use super::outcome::Outcome;
use super::row_update::RowUpdate;
use super::rows_feed::RowsFeed;
use super::update::Update;

mod frames;

const ROWS: &str = "rows";
/// While `Disconnected` it tries again after a wait that doubles from the first to the longest; a `Reconnect` (the
/// backend's ready line) tries at once. A connection's first frame starts the waits over.
const FIRST_WAIT: Duration = Duration::from_millis(500);
const LONGEST_WAIT: Duration = Duration::from_secs(5);

pub(super) struct ClientThread {
    endpoint: Endpoint,
    events: Receiver<Event>,
    /// Handed to each connection's reader.
    sender: Sender<Event>,
    connection: Option<Connection>,
    state: ConnectionState,
    /// The wait before the next try while `Disconnected`, and when that try is due; events don't put it off.
    wait: Duration,
    next_try: Instant,
    /// The reopen at once was used, and no frame has come since.
    reopened: bool,
    /// Read on the foreground, which updates the `SocketClient` entity.
    states: UnboundedSender<ConnectionState>,
    /// The window's call and subscription ids; a re-subscribe after a gap takes a fresh one.
    ids: Arc<AtomicU64>,
    calls: HashMap<u64, oneshot::Sender<Outcome<Value>>>,
    /// The newest call per key.
    keys: HashMap<String, u64>,
    /// Keyed topics and rows, by the id on the wire. `handle` is the id the window holds, for `Unsubscribe`.
    topics: HashMap<u64, Topic>,
    rows: HashMap<u64, Rows>,
}

struct Topic {
    topic: String,
    key: Option<String>,
    updates: UnboundedSender<Update>,
}

struct Rows {
    handle: u64,
    feed: RowsFeed,
    updates: UnboundedSender<RowUpdate>,
}

impl ClientThread {
    /// Starts the thread, which opens the socket at once.
    pub fn spawn(
        endpoint: Endpoint,
        events: Receiver<Event>,
        sender: Sender<Event>,
        states: UnboundedSender<ConnectionState>,
        ids: Arc<AtomicU64>,
    ) {
        let mut client = Self {
            endpoint,
            events,
            sender,
            connection: None,
            state: ConnectionState::Disconnected,
            wait: FIRST_WAIT,
            next_try: Instant::now(),
            reopened: false,
            states,
            ids,
            calls: HashMap::new(),
            keys: HashMap::new(),
            topics: HashMap::new(),
            rows: HashMap::new(),
        };
        thread::Builder::new()
            .name("socket".into())
            .spawn(move || client.run())
            .expect("spawn the socket thread");
    }

    fn run(&mut self) {
        self.open();
        loop {
            let event = if self.state == ConnectionState::Disconnected {
                match self
                    .events
                    .recv_timeout(self.next_try.saturating_duration_since(Instant::now()))
                {
                    Ok(event) => event,
                    Err(RecvTimeoutError::Timeout) => {
                        self.wait = (self.wait * 2).min(LONGEST_WAIT);
                        self.open();
                        continue;
                    }
                    Err(RecvTimeoutError::Disconnected) => return,
                }
            } else {
                match self.events.recv() {
                    Ok(event) => event,
                    Err(_) => return,
                }
            };
            self.handle(event);
        }
    }

    fn handle(&mut self, event: Event) {
        match event {
            Event::Call {
                id,
                feature,
                method,
                args,
                key,
                answer,
            } => {
                if self.connection.is_none() {
                    let message = "the backend isn't connected".to_string();
                    let _ = answer.send(Outcome::Failed(CallFailure {
                        code: ErrorCode::Failed,
                        message,
                    }));
                    return;
                }
                let older = key
                    .as_ref()
                    .and_then(|key| self.keys.insert(key.clone(), id));
                if let Some((older, answer)) =
                    older.and_then(|older| self.calls.remove_entry(&older))
                {
                    self.send(&OutFrame::Cancel { id: older });
                    let _ = answer.send(Outcome::Aborted);
                }
                self.calls.insert(id, answer);
                self.send(&OutFrame::Call {
                    id,
                    feature: &feature,
                    method: &method,
                    args: &args,
                });
            }
            Event::Cancel { id } => {
                if self.calls.contains_key(&id) {
                    self.send(&OutFrame::Cancel { id });
                }
            }
            Event::Subscribe {
                id,
                topic,
                key,
                updates,
            } => {
                self.topics.insert(
                    id,
                    Topic {
                        topic,
                        key,
                        updates,
                    },
                );
                self.send_topic(id);
            }
            Event::SubscribeRows {
                id,
                session_id,
                after,
                rows,
            } => {
                let feed = RowsFeed {
                    session_id,
                    last_seq: after,
                };
                self.rows.insert(
                    id,
                    Rows {
                        handle: id,
                        feed,
                        updates: rows,
                    },
                );
                self.send_rows(id);
            }
            Event::Unsubscribe { id } => {
                let wire = self
                    .rows
                    .iter()
                    .find(|(_, rows)| rows.handle == id)
                    .map(|(wire, _)| *wire);
                let wire = wire.unwrap_or(id);
                if self.topics.remove(&wire).is_some() || self.rows.remove(&wire).is_some() {
                    self.send(&OutFrame::Unsub { id: wire });
                }
            }
            Event::Frame(frame) => {
                self.heard();
                self.frame(frame);
            }
            Event::Ping(payload) => {
                self.heard();
                if let Some(connection) = &mut self.connection {
                    let _ = connection.pong(payload);
                }
            }
            Event::Closed => self.closed(),
            Event::Refused(code) => {
                self.close();
                self.set_state(ConnectionState::Refused { code });
            }
            Event::Broken => {
                self.close();
                self.disconnected();
            }
            Event::Reconnect(endpoint) => {
                if self.connection.is_none() {
                    self.endpoint = endpoint;
                    self.open();
                }
            }
        }
    }

    fn settle(&mut self, id: u64, outcome: Outcome<Value>) {
        self.keys.retain(|_, call| *call != id);
        if let Some(answer) = self.calls.remove(&id) {
            let _ = answer.send(outcome);
        }
    }

    /// The socket closed: waiting calls settle `Aborted`, one try to reopen, then subscribe again or `Disconnected`.
    /// A second close with no frame between waits instead.
    fn closed(&mut self) {
        self.close();
        if self.reopened {
            self.disconnected();
        } else {
            self.reopened = true;
            self.open();
        }
    }

    /// The connection works: the next close may reopen at once, and the waits start over.
    fn heard(&mut self) {
        self.reopened = false;
        self.wait = FIRST_WAIT;
    }

    fn disconnected(&mut self) {
        self.next_try = Instant::now() + self.wait;
        self.set_state(ConnectionState::Disconnected);
    }

    fn close(&mut self) {
        self.connection = None;
        self.keys.clear();
        for (_, answer) in self.calls.drain() {
            let _ = answer.send(Outcome::Aborted);
        }
    }

    fn open(&mut self) {
        self.set_state(ConnectionState::Connecting);
        match Connection::open(&self.endpoint, self.sender.clone()) {
            Ok(connection) => {
                let machine = connection.machine.clone();
                self.connection = Some(connection);
                let topics: Vec<u64> = self.topics.keys().copied().collect();
                topics.into_iter().for_each(|id| self.send_topic(id));
                let rows: Vec<u64> = self.rows.keys().copied().collect();
                rows.into_iter().for_each(|id| self.send_rows(id));
                self.set_state(ConnectionState::Connected { machine });
            }
            Err(OpenFailure::Refused(code)) => {
                eprintln!("socket: the backend refused the hello ({code})");
                self.set_state(ConnectionState::Refused { code });
            }
            Err(OpenFailure::Failed(error)) => {
                eprintln!("socket: couldn't open: {error}");
                self.disconnected();
            }
        }
    }

    fn send_topic(&mut self, id: u64) {
        let Some(Topic { topic, key, .. }) = self.topics.get(&id) else {
            return;
        };
        let (topic, key) = (topic.clone(), key.clone());
        self.send(&OutFrame::Sub {
            id,
            topic: &topic,
            key: key.as_deref(),
            after: None,
        });
    }

    fn send_rows(&mut self, id: u64) {
        let Some(rows) = self.rows.get(&id) else {
            return;
        };
        let (session, after) = (rows.feed.session_id.clone(), rows.feed.last_seq);
        self.send(&OutFrame::Sub {
            id,
            topic: ROWS,
            key: Some(&session),
            after,
        });
    }

    /// Sends on the open connection, if there is one. A failed send needs nothing here: the reader sends `Closed`.
    fn send(&mut self, frame: &OutFrame<'_>) {
        if let Some(connection) = &mut self.connection {
            let _ = connection.send(frame);
        }
    }

    fn set_state(&mut self, state: ConnectionState) {
        if self.state != state {
            self.state = state.clone();
            let _ = self.states.unbounded_send(state);
        }
    }
}
