//! `FakeBackend` is a test-only stand-in for the backend's `/socket`, so the client's rules are tested without .NET.
//! Each frame the client sends, and each pong, goes to `seen` in order, and to the test's `answer`, which returns what
//! the backend sends back. `answer` is shared by every connection, so it can tell a first hello from a later one.

use std::net::{Shutdown, TcpListener, TcpStream};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::thread;

use serde_json::{Value, json};

use super::test_client::DEADLINE;
use tungstenite::protocol::CloseFrame;
use tungstenite::protocol::frame::coding::CloseCode;
use tungstenite::{Bytes, Message, WebSocket};

/// What the fake backend sends in answer to a frame.
pub enum Reply {
    Frame(Value),
    Ping,
    Close(u16),
    /// Ends the connection with no close frame, as a dropped connection does.
    Drop,
    /// Stops reading the connection and keeps it open, as a wedged backend does.
    Stall,
}

type Answer = Arc<Mutex<dyn FnMut(&Value) -> Vec<Reply> + Send>>;

pub struct FakeBackend {
    pub port: u16,
    /// Every frame the client sent, and `{"t":"pong"}` for each pong.
    pub seen: Receiver<Value>,
    /// One for each socket the client closed or dropped.
    pub closed: Receiver<()>,
}

impl FakeBackend {
    pub fn start(answer: impl FnMut(&Value) -> Vec<Reply> + Send + 'static) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind the fake backend");
        let port = listener.local_addr().expect("its port").port();
        let (seen_tx, seen) = mpsc::channel();
        let (closed_tx, closed) = mpsc::channel();
        let answer: Answer = Arc::new(Mutex::new(answer));
        thread::spawn(move || {
            for stream in listener.incoming().flatten() {
                let (seen_tx, closed_tx, answer) =
                    (seen_tx.clone(), closed_tx.clone(), answer.clone());
                thread::spawn(move || serve(stream, seen_tx, closed_tx, answer));
            }
        });
        Self { port, seen, closed }
    }

    /// The next frame the client sent; panics if none comes within the deadline.
    pub fn next(&self) -> Value {
        self.seen
            .recv_timeout(DEADLINE)
            .expect("a frame from the client within the deadline")
    }
}

/// Welcomes the token "secret" from machine "box", and refuses any other with 4401, as the backend does.
pub fn hello(frame: &Value) -> Option<Vec<Reply>> {
    (frame["t"] == "hello").then(|| {
        if frame["token"] == "secret" && frame["protocol"] == 1 {
            vec![Reply::Frame(
                json!({"t": "welcome", "protocol": 1, "machine": "box"}),
            )]
        } else {
            vec![Reply::Close(4401)]
        }
    })
}

fn serve(stream: TcpStream, seen: Sender<Value>, closed: Sender<()>, answer: Answer) {
    let raw = stream.try_clone().expect("clone the stream");
    let Ok(mut socket) = tungstenite::accept(stream) else {
        return;
    };
    loop {
        let frame = match socket.read() {
            Ok(Message::Text(text)) => {
                serde_json::from_str(text.as_str()).expect("the client sends JSON")
            }
            Ok(Message::Pong(_)) => json!({"t": "pong"}),
            Ok(Message::Close(_)) | Err(_) => {
                let _ = closed.send(());
                return;
            }
            Ok(_) => continue,
        };
        let replies = (answer.lock().expect("the answer"))(&frame);
        if seen.send(frame).is_err() {
            return;
        }
        for reply in replies {
            if !send(&mut socket, &raw, reply) {
                return;
            }
        }
    }
}

fn send(socket: &mut WebSocket<TcpStream>, raw: &TcpStream, reply: Reply) -> bool {
    match reply {
        Reply::Frame(frame) => socket.send(Message::text(frame.to_string())).is_ok(),
        Reply::Ping => socket
            .send(Message::Ping(Bytes::from_static(b"are you there")))
            .is_ok(),
        Reply::Close(code) => {
            let close = CloseFrame {
                code: CloseCode::from(code),
                reason: "".into(),
            };
            let _ = socket.close(Some(close));
            let _ = socket.flush();
            false
        }
        Reply::Drop => {
            let _ = raw.shutdown(Shutdown::Both);
            false
        }
        Reply::Stall => loop {
            thread::park();
        },
    }
}
