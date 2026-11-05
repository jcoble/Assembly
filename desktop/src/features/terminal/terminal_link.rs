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

use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::error_code::ErrorCode;
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

/// One row of the `terminal` topic (OutputRow.cs): the bytes from `prev_seq` to `seq`, the exit, or why the rows
/// stopped, which carries no offsets.
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct OutputRow {
    seq: Option<i64>,
    prev_seq: Option<i64>,
    data: Option<String>,
    exit: Option<i32>,
    unavailable: Option<Availability>,
}

/// Why a terminal's rows have stopped, as a row's `unavailable` says (OutputRow.cs).
#[derive(Clone, Copy, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum Availability {
    ConnectionLost,
    TerminalMissing,
    SourceChanged,
}

/// What the link hands the pane.
#[derive(Debug, PartialEq)]
pub enum Output {
    Bytes(Vec<u8>),
    Exit(i32),
    Unavailable(Availability),
    /// A row with offsets that adds no bytes: the connection is back and nothing was missed.
    Caught,
}

/// Whether a call failed because the connection is down: it comes back by itself.
#[allow(dead_code)] // The pane classifies with it from Task 3.
pub fn connection_down(failure: &CallFailure) -> bool {
    matches!(
        failure.code,
        ErrorCode::ConnectionLost | ErrorCode::Unreachable { .. }
    )
}

/// Whether a call failed because the terminal's source changed.
#[allow(dead_code)] // The pane classifies with it from Task 3.
pub fn source_changed(failure: &CallFailure) -> bool {
    matches!(&failure.code, ErrorCode::Refused { code } if code == "source_changed")
}

impl TerminalLink {
    /// The session's running shell, or a new one in its folder at this size, with the size it has and the output of
    /// the shell it replaces, if that one ended: shown above the new one, as before. The backend picks the folder from
    /// the owner (StartArgs.cs), routes to the machine that owns the session, and drops the ended shell on the start.
    pub async fn open(
        session: &str,
        (cols, rows): (usize, usize),
        cx: &mut AsyncApp,
    ) -> Result<(Self, (usize, usize), Vec<u8>), CallFailure> {
        let list: Vec<TerminalInfo> = call(cx, "list", json!({"owner": session})).await?;
        let (mut running, mut ended) = (None, None);
        for info in list {
            if info.owner.as_deref() == Some(session) && info.command.is_none() {
                match info.exit_code {
                    None => running = Some(info),
                    Some(_) => ended = Some(info),
                }
            }
        }
        let mut before = Vec::new();
        let info = match running {
            Some(info) => info,
            None => {
                if let Some(ended) = ended {
                    let mut link = Self {
                        id: ended.id,
                        end: 0,
                    };
                    before = link.replay(cx).await?;
                }
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
            before,
        ))
    }

    /// The output kept so far, read page by page from the start, or from 4 MB before the end when there's more.
    pub async fn replay(&mut self, cx: &mut AsyncApp) -> Result<Vec<u8>, CallFailure> {
        let mut bytes = Vec::new();
        let mut boundary = None;
        loop {
            let args = json!({ "terminal": self.id, "offset": self.end });
            let piece: OutputPiece = call(cx, "read", args).await?;
            // Output may grow during every read. This replay covers only the first response's end;
            // later bytes belong to live updates (or the next catch-up), including a page straddling that end.
            let end = *boundary.get_or_insert(piece.end);
            if bytes.is_empty() && end - piece.offset > REPLAY {
                self.end = end - REPLAY;
                continue;
            }
            let data = decode(&piece.data).map_err(failed)?;
            let take = data.len().min((end - piece.offset).max(0) as usize);
            self.end = piece.offset + take as i64;
            bytes.extend_from_slice(&data[..take]);
            if self.end >= end {
                return Ok(bytes);
            }
            if take == 0 {
                return Err(failed("the terminal read made no progress".into()));
            }
        }
    }

    /// The terminal's live rows.
    pub fn follow(&self, cx: &App) -> Result<TopicSubscription<Update>, CallFailure> {
        let client = SocketClient::try_terminal(cx).ok_or_else(no_answer)?;
        Ok(client.read(cx).subscribe("terminal", Some(&self.id)))
    }

    /// A row from the subscription, as what to hand on: why the rows stopped, `Caught` for one adding nothing new
    /// (the empty boundary row after a reconnect, or one already handed on), and `Err(())` for one past a gap (output
    /// between the replay and the subscription, or during a reconnect), which `replay` fills.
    pub fn step(&mut self, update: &Update) -> Result<Option<Output>, ()> {
        let Update::Change { item, .. } = update else {
            return Ok(None);
        };
        let Ok(row) = OutputRow::deserialize(item) else {
            return Ok(None);
        };
        if let Some(availability) = row.unavailable {
            return Ok(Some(Output::Unavailable(availability)));
        }
        let (Some(seq), Some(prev_seq)) = (row.seq, row.prev_seq) else {
            return Ok(None);
        };
        if prev_seq > self.end {
            return Err(());
        }
        if let Some(code) = row.exit {
            return Ok(Some(Output::Exit(code)));
        }
        if seq <= self.end {
            return Ok(Some(Output::Caught));
        }
        let data = (row.data.as_deref().map(decode))
            .and_then(Result::ok)
            .unwrap_or_default();
        let skip = (self.end - prev_seq) as usize;
        self.end = seq;
        Ok(Some(Output::Bytes(
            data.get(skip..).unwrap_or_default().to_vec(),
        )))
    }

    /// Typed or pasted bytes. Dropping the answer cancels the write (terminal#2), so the pane holds it.
    pub fn write(
        &self,
        bytes: &[u8],
        cx: &App,
    ) -> impl Future<Output = Result<(), CallFailure>> + use<> {
        let args = json!({ "terminal": self.id, "data": STANDARD.encode(bytes) });
        let write = start::<Value>(cx, "write", args);
        async move { settle(write?.await).map(|_| ()) }
    }

    // The socket client owns completion; dropping these replies must not cancel forwarded operations.
    pub fn resize(&self, (cols, rows): (usize, usize), cx: &App) {
        let args = json!({ "terminal": self.id, "cols": cols, "rows": rows });
        if let Ok(resize) = start::<Value>(cx, "resize", args) {
            resize.ignore_reply();
        }
    }

    pub fn close(&self, cx: &App) {
        if let Ok(close) = start::<Value>(cx, "close", json!({ "terminal": self.id })) {
            close.ignore_reply();
        }
    }
}

/// A call on the terminal socket. One made while that socket is down would fail as plain `failed`
/// (client_thread.rs), so it fails here as the connection being down. While `Connecting` the thread is opening the
/// socket and answers the call after that, so it goes through.
fn start<T: DeserializeOwned>(
    cx: &App,
    method: &str,
    args: Value,
) -> Result<PendingCall<T>, CallFailure> {
    let client = SocketClient::try_terminal(cx).ok_or_else(no_answer)?;
    let client = client.read(cx);
    if matches!(
        client.state(),
        ConnectionState::Disconnected | ConnectionState::Refused { .. }
    ) {
        return Err(CallFailure {
            code: ErrorCode::ConnectionLost,
            message: "the backend isn't connected".into(),
        });
    }
    Ok(client.call("terminal", method, args, None))
}

async fn call<T: DeserializeOwned>(
    cx: &mut AsyncApp,
    method: &str,
    args: Value,
) -> Result<T, CallFailure> {
    let call = cx.update(|cx| start(cx, method, args))?;
    settle(call.await)
}

/// A call the socket aborted counts as the connection being down.
fn settle<T>(outcome: Outcome<T>) -> Result<T, CallFailure> {
    match outcome {
        Outcome::Ok(value) => Ok(value),
        Outcome::Failed(failure) => Err(failure),
        Outcome::Aborted => Err(no_answer()),
    }
}

fn no_answer() -> CallFailure {
    CallFailure {
        code: ErrorCode::ConnectionLost,
        message: "the backend didn't answer".into(),
    }
}

fn failed(message: String) -> CallFailure {
    CallFailure {
        code: ErrorCode::Failed,
        message,
    }
}

fn decode(data: &str) -> Result<Vec<u8>, String> {
    STANDARD.decode(data).map_err(|error| error.to_string())
}

#[cfg(test)]
#[path = "terminal_link_tests.rs"]
mod tests;
