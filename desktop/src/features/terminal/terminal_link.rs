//! `TerminalLink` is one session's shell on the backend's `terminal` feature
//! (dotnet/Assembly.Application/Features/Terminal/TerminalFeature.cs), through the socket client: it finds the
//! session's running shell or starts one (terminal#1, terminal#9), reads its kept output (terminal#5), follows the
//! `terminal` topic for the rest, and writes, resizes and closes it. Bytes travel base64, as the backend's JSON sends
//! `byte[]`. It holds the terminal's id and the offset just past the last byte it handed on.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui_kit::{App, AsyncApp};
use serde::Deserialize;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};

use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::pending_call::PendingCall;
use crate::core::socket::topic_subscription::TopicSubscription;
use crate::core::socket::update::Update;

/// The most a returning window replays (OutputRing.Replay, terminal#5).
const REPLAY: i64 = 4 * 1024 * 1024;

#[derive(Clone)]
pub struct TerminalLink {
    pub id: String,
    /// The offset just past the last byte handed on.
    pub end: i64,
}

/// One terminal as `terminal/list` and `terminal/start` answer (TerminalInfo.cs).
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct TerminalInfo {
    id: String,
    owner: Option<String>,
    command: Option<String>,
    cols: usize,
    rows: usize,
    exit_code: Option<i32>,
}

/// A run of output as `terminal/read` answers (OutputPiece.cs).
#[derive(Deserialize)]
struct OutputPiece {
    offset: i64,
    end: i64,
    data: String,
}

/// One row of the `terminal` topic (OutputRow.cs): the bytes from `prev_seq` to `seq`, or the exit.
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct OutputRow {
    seq: i64,
    prev_seq: i64,
    data: Option<String>,
    exit: Option<i32>,
}

/// What the link hands the pane.
pub enum Output {
    Bytes(Vec<u8>),
    Exit(i32),
}

impl TerminalLink {
    /// The session's running shell, or a new one in its folder at this size, with the size it has. The backend picks
    /// the folder from the owner (StartArgs.cs), and refuses a session that isn't this machine's.
    pub async fn open(
        session: &str,
        (cols, rows): (usize, usize),
        cx: &mut AsyncApp,
    ) -> Result<(Self, (usize, usize)), String> {
        let list: Vec<TerminalInfo> = call(cx, "list", json!({})).await?;
        let running = list.into_iter().find(|info| {
            info.owner.as_deref() == Some(session)
                && info.command.is_none()
                && info.exit_code.is_none()
        });
        let info = match running {
            Some(info) => info,
            None => {
                let args = json!({ "owner": session, "cols": cols, "rows": rows });
                call::<TerminalInfo>(cx, "start", args).await?
            }
        };
        Ok((
            Self {
                id: info.id,
                end: 0,
            },
            (info.cols, info.rows),
        ))
    }

    /// The output kept so far, read page by page from the start, or from 4 MB before the end when there's more.
    pub async fn replay(&mut self, cx: &mut AsyncApp) -> Result<Vec<u8>, String> {
        let mut bytes = Vec::new();
        loop {
            let args = json!({ "terminal": self.id, "offset": self.end });
            let piece: OutputPiece = call(cx, "read", args).await?;
            if bytes.is_empty() && piece.end - piece.offset > REPLAY {
                self.end = piece.end - REPLAY;
                continue;
            }
            let data = decode(&piece.data)?;
            self.end = piece.offset + data.len() as i64;
            bytes.extend(data);
            if self.end >= piece.end {
                return Ok(bytes);
            }
        }
    }

    /// The terminal's live rows.
    pub fn follow(&self, cx: &App) -> TopicSubscription<Update> {
        SocketClient::global(cx)
            .read(cx)
            .subscribe("terminal", Some(&self.id))
    }

    /// A row from the subscription, as what to hand on: nothing for one already handed on, and `Err(())` for one
    /// past a gap (output between the replay and the subscription, or during a reconnect), which `replay` fills.
    pub fn step(&mut self, update: &Update) -> Result<Option<Output>, ()> {
        let Update::Change { item, .. } = update else {
            return Ok(None);
        };
        let Ok(row) = OutputRow::deserialize(item) else {
            return Ok(None);
        };
        if row.prev_seq > self.end {
            return Err(());
        }
        if let Some(code) = row.exit {
            return Ok(Some(Output::Exit(code)));
        }
        if row.seq <= self.end {
            return Ok(None);
        }
        let data = (row.data.as_deref().map(decode))
            .and_then(Result::ok)
            .unwrap_or_default();
        let skip = (self.end - row.prev_seq) as usize;
        self.end = row.seq;
        Ok(Some(Output::Bytes(
            data.get(skip..).unwrap_or_default().to_vec(),
        )))
    }

    /// Typed or pasted bytes. Dropping the answer cancels the write (terminal#2), so the pane holds it.
    pub fn write(
        &self,
        bytes: &[u8],
        cx: &App,
    ) -> impl Future<Output = Result<(), String>> + use<> {
        let args = json!({ "terminal": self.id, "data": STANDARD.encode(bytes) });
        let write = start::<Value>(cx, "write", args);
        async move { settle(write.await).map(|_| ()) }
    }

    // Nothing waits on a resize or a close: the backend runs a read call to its end even when its answer is dropped
    // (SocketDispatcher.AnswerAsync), and a resize that fails leaves the old size until the next.
    pub fn resize(&self, (cols, rows): (usize, usize), cx: &App) {
        let args = json!({ "terminal": self.id, "cols": cols, "rows": rows });
        drop(start::<Value>(cx, "resize", args));
    }

    pub fn close(&self, cx: &App) {
        drop(start::<Value>(cx, "close", json!({ "terminal": self.id })));
    }
}

fn start<T: DeserializeOwned>(cx: &App, method: &str, args: Value) -> PendingCall<T> {
    SocketClient::global(cx)
        .read(cx)
        .call("terminal", method, args, None)
}

async fn call<T: DeserializeOwned>(
    cx: &mut AsyncApp,
    method: &str,
    args: Value,
) -> Result<T, String> {
    let call = cx.update(|cx| start(cx, method, args));
    settle(call.await)
}

fn settle<T>(outcome: Outcome<T>) -> Result<T, String> {
    match outcome {
        Outcome::Ok(value) => Ok(value),
        Outcome::Failed(failure) => Err(failure.message),
        Outcome::Aborted => Err("the backend didn't answer".into()),
    }
}

fn decode(data: &str) -> Result<Vec<u8>, String> {
    STANDARD.decode(data).map_err(|error| error.to_string())
}
