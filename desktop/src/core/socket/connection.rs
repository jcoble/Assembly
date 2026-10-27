//! `Connection` is one open socket: connected, `hello` sent with the token and protocol, and its `welcome` read. Then
//! the socket goes to its reader thread, which blocks on it and sends each frame to the client thread, then `Closed`
//! when a read fails; a frame that doesn't parse ends it the same way. The client thread writes through a second
//! `WebSocket` on a clone of the stream (tungstenite's read and send both take `&mut`). Only the client thread
//! writes: the reader's `WebSocket` sits on a stream whose writes are dropped, so tungstenite's own pong and close
//! replies never reach the wire, and a ping goes to the client thread as `Event::Ping` for it to answer. (The
//! backend sends no pings today.) Dropping the connection shuts the stream down, which
//! ends its reader.

use std::net::TcpStream;
use std::sync::mpsc::Sender;

use tungstenite::WebSocket;

use super::endpoint::Endpoint;
use super::event::Event;
use super::out_frame::OutFrame;

pub(super) const PROTOCOL: u32 = 1;

pub(super) struct Connection {
    /// Numbers this connection's frames and close, so a dropped one's are ignored.
    pub number: u64,
    pub machine: String,
    writer: WebSocket<TcpStream>,
}

impl Connection {
    /// Opens the socket and waits for its welcome; an error if it can't. Then starts its reader.
    pub fn open(
        _endpoint: &Endpoint,
        _number: u64,
        _events: Sender<Event>,
    ) -> Result<Self, String> {
        todo!()
    }

    /// Sends one frame; an error means the socket is gone (its reader sends `Closed`).
    pub fn send(&mut self, _frame: &OutFrame<'_>) -> Result<(), String> {
        todo!()
    }
}

/// The reader thread's loop.
fn read_frames(_socket: WebSocket<TcpStream>, _number: u64, _events: Sender<Event>) {
    todo!()
}

impl Drop for Connection {
    fn drop(&mut self) {
        todo!()
    }
}
