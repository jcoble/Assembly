//! `Connection` is one open socket: connected, `hello` sent with the token and protocol, and its `welcome` read. Then
//! the socket goes to its reader thread, which blocks on it and sends each frame to the client thread, then `Closed`
//! when a read fails or a close arrives (`Broken` for a frame that doesn't parse). The client thread writes
//! through the `WebSocket` the handshake made, and the reader reads through a second one on a clone of the stream
//! (tungstenite's read and send both take `&mut`). Only the client thread writes: the reader's stream drops writes,
//! so tungstenite's own pong and close replies never reach the wire, and a ping goes to the client thread as
//! `Event::Ping` for it to answer. Nothing is lost in the split: the backend sends nothing after the welcome until
//! the client writes. Dropping the connection shuts the stream down, which ends its reader, and waits for it.
//! Every write stays bounded: a backend that stops reading can't hold the client thread, since a write that fails
//! shuts the stream down, so the reader sends `Closed`, as on a drop.

use std::io::{self, Read, Write};
use std::net::{Shutdown, TcpStream};
use std::sync::mpsc::Sender;
use std::thread::{self, JoinHandle};
use std::time::Duration;

use tungstenite::protocol::Role;
use tungstenite::{Message, WebSocket};

use super::endpoint::Endpoint;
use super::event::Event;
use super::in_frame::InFrame;
use super::open_failure::OpenFailure;
use super::out_frame::OutFrame;

pub(super) const PROTOCOL: u32 = 1;
/// How long opening may take, connected to welcomed, on the same machine, and how long any one write may take.
const OPENING: Duration = Duration::from_secs(5);

pub(super) struct Connection {
    pub machine: String,
    writer: WebSocket<TcpStream>,
    reader: Option<JoinHandle<()>>,
}

impl Connection {
    /// Opens the socket and waits for its welcome; an error if it can't. Then starts its reader.
    pub fn open(endpoint: &Endpoint, events: Sender<Event>) -> Result<Self, OpenFailure> {
        let stream = TcpStream::connect(("127.0.0.1", endpoint.port)).map_err(failed)?;
        // A backend that takes the connection but never welcomes it (wedged, or another process on the port) would
        // hold the client thread, and every call waiting on it, for good: opening gets a bound. Reads lose it after
        // the welcome (the reader waits as long as the socket is quiet); writes keep it.
        stream.set_read_timeout(Some(OPENING)).map_err(failed)?;
        stream.set_write_timeout(Some(OPENING)).map_err(failed)?;
        let read_half = stream.try_clone().map_err(failed)?;
        let url = format!("ws://127.0.0.1:{}/socket", endpoint.port);
        let (mut writer, _) = tungstenite::client(url, stream).map_err(failed)?;
        let hello = OutFrame::Hello {
            token: &endpoint.token,
            protocol: PROTOCOL,
        };
        writer.send(text(&hello)).map_err(failed)?;
        let machine = match writer.read().map_err(failed)? {
            Message::Text(text) => match serde_json::from_str(text.as_str()) {
                Ok(InFrame::Welcome { machine, .. }) => machine,
                _ => return Err(failed(format!("expected a welcome, got {text}"))),
            },
            Message::Close(Some(frame)) if matches!(u16::from(frame.code), 4400 | 4401) => {
                return Err(OpenFailure::Refused(frame.code.into()));
            }
            other => return Err(failed(format!("expected a welcome, got {other:?}"))),
        };
        let opened = writer.get_ref();
        opened.set_read_timeout(None).map_err(failed)?;
        let reader = WebSocket::from_raw_socket(ReadOnly(read_half), Role::Client, None);
        let reader = thread::Builder::new()
            .name("socket reader".into())
            .spawn(move || read_frames(reader, events))
            .map_err(failed)?;
        Ok(Self {
            machine,
            writer,
            reader: Some(reader),
        })
    }

    /// Sends one frame. A write that fails or runs past its bound shuts the stream down, so the reader sends `Closed`.
    pub fn send(&mut self, frame: &OutFrame<'_>) {
        self.write(text(frame));
    }

    pub fn pong(&mut self, payload: Vec<u8>) {
        self.write(Message::Pong(payload.into()));
    }

    fn write(&mut self, message: Message) {
        if let Err(error) = self.writer.send(message) {
            eprintln!("socket: a write failed, so the connection ends: {error}");
            let _ = self.writer.get_ref().shutdown(Shutdown::Both);
        }
    }
}

fn failed(error: impl ToString) -> OpenFailure {
    OpenFailure::Failed(error.to_string())
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
            Ok(Message::Close(Some(frame))) if matches!(u16::from(frame.code), 4400 | 4401) => {
                eprintln!(
                    "socket: the backend closed it ({}): {}",
                    frame.code, frame.reason
                );
                let _ = events.send(Event::Refused(frame.code.into()));
                return;
            }
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
        if let Some(reader) = self.reader.take() {
            let _ = reader.join();
        }
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
