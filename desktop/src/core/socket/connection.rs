//! `Connection` is one open socket: connected, `hello` sent with the token and protocol, and its `welcome` read. Then
//! the socket goes to its reader thread, which blocks on it and sends each frame to the client thread, then `Closed`
//! when a read fails or a close arrives (`Broken` for a frame that doesn't parse). The client thread writes
//! through the `WebSocket` the handshake made, and the reader reads through a second one on a clone of the stream
//! (tungstenite's read and send both take `&mut`). Only the client thread writes: the reader's stream drops writes,
//! so tungstenite's own pong and close replies never reach the wire, and a ping goes to the client thread as
//! `Event::Ping` for it to answer. Nothing is lost in the split: the backend sends nothing after the welcome until
//! the client writes. Dropping the connection shuts the stream down, which ends its reader.

use std::io::{self, Read, Write};
use std::net::{Shutdown, TcpStream};
use std::sync::mpsc::Sender;
use std::thread;
use std::time::Duration;

use tungstenite::protocol::Role;
use tungstenite::{Message, WebSocket};

use super::endpoint::Endpoint;
use super::event::Event;
use super::in_frame::InFrame;
use super::out_frame::OutFrame;

pub(super) const PROTOCOL: u32 = 1;
/// How long opening may take, connected to welcomed, on the same machine.
const OPENING: Duration = Duration::from_secs(5);

pub(super) struct Connection {
    pub machine: String,
    writer: WebSocket<TcpStream>,
}

impl Connection {
    /// Opens the socket and waits for its welcome; an error if it can't. Then starts its reader.
    pub fn open(endpoint: &Endpoint, events: Sender<Event>) -> Result<Self, String> {
        let stream = TcpStream::connect(("127.0.0.1", endpoint.port)).map_err(|e| e.to_string())?;
        // A backend that takes the connection but never welcomes it (wedged, or another process on the port) would
        // hold the client thread, and every call waiting on it, for good: opening gets a bound, lifted after.
        stream
            .set_read_timeout(Some(OPENING))
            .map_err(|e| e.to_string())?;
        stream
            .set_write_timeout(Some(OPENING))
            .map_err(|e| e.to_string())?;
        let read_half = stream.try_clone().map_err(|e| e.to_string())?;
        let url = format!("ws://127.0.0.1:{}/socket", endpoint.port);
        let (mut writer, _) = tungstenite::client(url, stream).map_err(|e| e.to_string())?;
        let hello = OutFrame::Hello {
            token: &endpoint.token,
            protocol: PROTOCOL,
        };
        writer.send(text(&hello)).map_err(|e| e.to_string())?;
        let machine = match writer.read().map_err(|e| e.to_string())? {
            Message::Text(text) => match serde_json::from_str(text.as_str()) {
                Ok(InFrame::Welcome { machine, .. }) => machine,
                _ => return Err(format!("expected a welcome, got {text}")),
            },
            Message::Close(frame) => {
                return Err(format!("the backend refused the hello: {frame:?}"));
            }
            other => return Err(format!("expected a welcome, got {other:?}")),
        };
        let opened = writer.get_ref();
        opened.set_read_timeout(None).map_err(|e| e.to_string())?;
        opened.set_write_timeout(None).map_err(|e| e.to_string())?;
        let reader = WebSocket::from_raw_socket(ReadOnly(read_half), Role::Client, None);
        thread::Builder::new()
            .name("socket reader".into())
            .spawn(move || read_frames(reader, events))
            .map_err(|e| e.to_string())?;
        Ok(Self { machine, writer })
    }

    /// Sends one frame; an error means the socket is gone (its reader sends `Closed`).
    pub fn send(&mut self, frame: &OutFrame<'_>) -> Result<(), String> {
        self.writer.send(text(frame)).map_err(|e| e.to_string())
    }

    pub fn pong(&mut self, payload: Vec<u8>) -> Result<(), String> {
        self.writer
            .send(Message::Pong(payload.into()))
            .map_err(|e| e.to_string())
    }
}

fn text(frame: &OutFrame<'_>) -> Message {
    Message::text(serde_json::to_string(frame).expect("a frame is JSON"))
}

/// The reader thread's loop.
fn read_frames(mut socket: WebSocket<ReadOnly>, events: Sender<Event>) {
    loop {
        let event = match socket.read() {
            Ok(Message::Text(text)) => match serde_json::from_str(text.as_str()) {
                Ok(frame) => Event::Frame(frame),
                Err(error) => {
                    eprintln!("socket: a frame this client doesn't read ({error}): {text}");
                    let _ = events.send(Event::Broken);
                    return;
                }
            },
            Ok(Message::Ping(payload)) => Event::Ping(payload.to_vec()),
            Ok(Message::Close(_)) | Err(_) => break,
            Ok(_) => continue,
        };
        if events.send(event).is_err() {
            return;
        }
    }
    let _ = events.send(Event::Closed);
}

impl Drop for Connection {
    fn drop(&mut self) {
        let _ = self.writer.get_ref().shutdown(Shutdown::Both);
    }
}

/// The reader's side of the stream: reads pass through, writes are dropped (only the client thread writes).
struct ReadOnly(TcpStream);

impl Read for ReadOnly {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        self.0.read(buf)
    }
}

impl Write for ReadOnly {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        Ok(buf.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
