//! The Terminal tab's test fixture: `FakeBackend` playing the backend's `terminal` feature (TerminalFeature.cs), and
//! the pane opened on it with sessions a and b.

use std::sync::{Arc, Mutex};

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{Entity, Subscription, VisualTestContext};
use serde_json::{Value, json};

use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::socket::update::Update;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::session_item::SessionItem;
use crate::core::state::sessions::Sessions;
use crate::features::terminal::terminal_pane::{Shown, TerminalPane};

/// The fake backend's terminals and what it saw.
#[derive(Default)]
pub(super) struct Terminals {
    /// Each call and sub, as (method or "sub", args or the sub frame), in order.
    pub(super) seen: Vec<(String, Value)>,
    /// The running terminals: (id, owner).
    pub(super) running: Vec<(String, String)>,
    /// The output `terminal/read` answers, for every terminal.
    pub(super) kept: Vec<u8>,
    /// A paged producer that adds output on each read, for bounded replay tests.
    pub(super) read_limit: Option<usize>,
    pub(super) grow_reads: usize,
    /// The rows each `sub` is answered with, after its state.
    pub(super) rows: Vec<Value>,
    /// A shell that ended: (id, owner, its output), which `list` shows with its exit code.
    pub(super) ended: Option<(String, String, Vec<u8>)>,
    /// `write` fails with this message.
    pub(super) refuse_writes: Option<&'static str>,
    /// Calls that fail: (method, wire code, message).
    pub(super) refused: Vec<(String, String, String)>,
    /// The next call of this method drops the connection instead of answering.
    pub(super) drop_on: Option<String>,
    /// Hellos are dropped, so a socket that drops stays down.
    pub(super) refuse_hello: bool,
    /// The `machines` topic's state, as its items.
    pub(super) machines: Vec<Value>,
}

impl Terminals {
    pub(super) fn of(&self, method: &str) -> Vec<Value> {
        (self.seen.iter())
            .filter(|(seen, _)| seen == method)
            .map(|(_, args)| args.clone())
            .collect()
    }

    /// Later calls of `method` fail with this wire code and message.
    pub(super) fn refuse(&mut self, method: &str, code: &str, message: &str) {
        (self.refused).push((method.into(), code.into(), message.into()));
    }

    /// Later calls of `method` are answered again.
    pub(super) fn allow(&mut self, method: &str) {
        self.refused.retain(|(refused, _, _)| refused != method);
    }
}

pub(super) fn backend(terminals: Arc<Mutex<Terminals>>, heard: UnboundedSender<()>) -> FakeBackend {
    FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return match terminals.lock().unwrap().refuse_hello {
                true => vec![Reply::Drop],
                false => replies,
            };
        }
        let mut terminals = terminals.lock().unwrap();
        let id = frame["id"].clone();
        let args = &frame["args"];
        let result = |result: Value| {
            vec![Reply::Frame(
                json!({"t": "result", "id": id, "result": result}),
            )]
        };
        let replies = match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("sub"), _) if frame["topic"] == "machines" => {
                let items = terminals.machines.clone();
                vec![Reply::Frame(
                    json!({"t": "state", "sub": id, "items": items}),
                )]
            }
            (Some("sub"), _) => {
                terminals.seen.push(("sub".into(), frame.clone()));
                let mut replies = vec![Reply::Frame(json!({"t": "state", "sub": id, "items": []}))];
                for row in &terminals.rows {
                    replies.push(Reply::Frame(json!({"t": "change", "sub": id, "op": "upsert", "key": frame["key"], "item": row})));
                }
                replies
            }
            (Some("call"), Some(method)) => {
                terminals.seen.push((method.into(), args.clone()));
                let refused = (terminals.refused.iter()).find(|(refused, _, _)| refused == method);
                match method {
                    method if terminals.drop_on.as_deref() == Some(method) => {
                        terminals.drop_on = None;
                        vec![Reply::Drop]
                    }
                    _ if refused.is_some() => {
                        let (_, code, message) = refused.unwrap();
                        vec![Reply::Frame(json!({"t": "error", "id": id, "code": code,
                            "message": message, "machine": "Workbox Test"}))]
                    }
                    "write" if terminals.refuse_writes.is_some() => vec![Reply::Frame(
                        json!({"t": "error", "id": id, "code": "failed", "message": terminals.refuse_writes}),
                    )],
                    "list" => {
                        let mut list: Vec<Value> = (terminals.running.iter())
                            .map(|(id, owner)| info(id, owner, &json!(96), &json!(28)))
                            .collect();
                        if let Some((id, owner, _)) = &terminals.ended {
                            let mut ended = info(id, owner, &json!(96), &json!(28));
                            ended["exitCode"] = json!(1);
                            list.push(ended);
                        }
                        result(Value::Array(list))
                    }
                    "start" => {
                        let (terminal, owner) = (
                            format!("term-{}", terminals.running.len() + 1),
                            args["owner"].as_str().unwrap_or_default().to_string(),
                        );
                        terminals.running.push((terminal.clone(), owner.clone()));
                        result(info(&terminal, &owner, &args["cols"], &args["rows"]))
                    }
                    "read" => {
                        if terminals.grow_reads > 0 {
                            terminals.kept.extend_from_slice(b"xyz");
                            terminals.grow_reads -= 1;
                        }
                        let offset = args["offset"].as_u64().unwrap_or_default() as usize;
                        let kept = match &terminals.ended {
                            Some((id, _, kept)) if args["terminal"] == id.as_str() => kept,
                            _ => &terminals.kept,
                        };
                        let start = offset.min(kept.len());
                        let end =
                            (start + terminals.read_limit.unwrap_or(kept.len())).min(kept.len());
                        let data = STANDARD.encode(&kept[start..end]);
                        result(json!({"offset": offset, "end": kept.len(), "data": data}))
                    }
                    _ => result(json!({})),
                }
            }
            _ => Vec::new(),
        };
        drop(terminals);
        _ = heard.unbounded_send(());
        replies
    })
}

/// A terminal as the backend lists it; a new one has the size it was started at.
pub(super) fn info(id: &str, owner: &str, cols: &Value, rows: &Value) -> Value {
    json!({"id": id, "cwd": "/home/me", "shell": "/bin/bash", "cols": cols, "rows": rows, "pid": 7,
        "owner": owner, "startedAt": "2026-10-29T00:00:00Z"})
}

pub(super) fn session(id: &str, machine: Option<&str>) -> SessionItem {
    serde_json::from_value(
        json!({"sessionId": id, "agent": "claude", "repoUrl": "/repo", "machine": machine,
        "updatedAt": 0}),
    )
    .unwrap()
}

pub(super) struct Open<'a> {
    pub(super) pane: Entity<TerminalPane>,
    pub(super) terminals: Arc<Mutex<Terminals>>,
    pub(super) changes: UnboundedReceiver<()>,
    pub(super) changed: UnboundedSender<()>,
    pub(super) cx: &'a mut VisualTestContext,
    pub(super) _heard: Subscription,
    pub(super) _backend: FakeBackend,
}

/// The pane on a fake backend whose terminals start as `terminals`, with sessions a and b (b on another machine
/// when `remote`), and the tab shown on session a.
pub(super) async fn open(cx: &mut TestAppContext, terminals: Terminals, remote: bool) -> Open<'_> {
    let terminals = Arc::new(Mutex::new(terminals));
    let (changed, mut changes) = unbounded();
    let backend = backend(terminals.clone(), changed.clone());
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        let b = session("b", remote.then_some("Workbox Test"));
        Sessions::start_holding(vec![session("a", None), b], false, false, cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        SocketClient::start_terminal(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let (pane, cx) = cx.add_window_view(TerminalPane::new);
    let tell = changed.clone();
    let heard = cx.update(|_, cx| cx.observe(&pane, move |_, _| _ = tell.unbounded_send(())));
    pick("a", cx);
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    let mut open = Open {
        pane,
        terminals,
        changes: unbounded().1,
        changed,
        cx,
        _heard: heard,
        _backend: backend,
    };
    std::mem::swap(&mut open.changes, &mut changes);
    open
}

pub(super) fn pick(session: &str, cx: &mut VisualTestContext) {
    let session = Some(session.to_string());
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| selected.select(session, cx))
    });
}

impl Open<'_> {
    pub(super) async fn until(
        &mut self,
        what: &str,
        mut done: impl FnMut(&Terminals, &TerminalPane) -> bool,
    ) {
        let (terminals, pane, cx) = (self.terminals.clone(), self.pane.clone(), &mut *self.cx);
        until(what, &mut self.changes, &self.changed, || {
            cx.run_until_parked();
            let terminals = terminals.lock().unwrap();
            cx.update(|_, cx| done(&terminals, pane.read(cx)))
        })
        .await;
    }

    /// A row of the shell's output, as the subscription hands it on.
    pub(super) fn push_row(&mut self, item: Value) {
        self.pane.update(self.cx, |pane, cx| {
            let key = (pane.link.as_ref()).map_or(String::new(), |link| link.id.clone());
            pane.output(Update::Change { key, item }, cx)
        });
        self.cx.run_until_parked();
    }

    /// The window's own socket came back: its subscription starts over with a state.
    pub(super) fn socket_returned(&mut self) {
        (self.pane).update(self.cx, |pane, cx| {
            pane.output(Update::State(Vec::new()), cx)
        });
        self.cx.run_until_parked();
    }

    /// Advances the test clock, for the pane's timers.
    pub(super) fn advance(&mut self, millis: u64) {
        let by = std::time::Duration::from_millis(millis);
        self.cx.executor().advance_clock(by);
        self.cx.run_until_parked();
    }

    pub(super) fn shown(&mut self, active: bool) {
        (self.pane).update_in(self.cx, |pane, window, cx| {
            pane.set_active(active, window, cx)
        });
    }
}

pub(super) fn following(pane: &TerminalPane) -> bool {
    pane.shown == Shown::Open && !pane.catching_up()
}
