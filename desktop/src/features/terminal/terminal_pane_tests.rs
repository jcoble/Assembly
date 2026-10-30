//! The Terminal tab on the socket client against `FakeBackend`, which plays the backend's `terminal` feature
//! (TerminalFeature.cs): when it starts, reuses, writes to, resizes and reads its session's shell, and what it shows.

use std::sync::{Arc, Mutex};

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{Entity, Subscription, VisualTestContext, px};
use serde_json::{Value, json};

use super::{Shown, TerminalPane};
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::socket::update::Update;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::session_item::SessionItem;
use crate::core::state::sessions::Sessions;

/// The fake backend's terminals and what it saw.
#[derive(Default)]
struct Terminals {
    /// Each call and sub, as (method or "sub", args or the sub frame), in order.
    seen: Vec<(String, Value)>,
    /// The running terminals: (id, owner).
    running: Vec<(String, String)>,
    /// The output `terminal/read` answers, for every terminal.
    kept: Vec<u8>,
    /// The rows each `sub` is answered with, after its state.
    rows: Vec<Value>,
}

impl Terminals {
    fn of(&self, method: &str) -> Vec<Value> {
        (self.seen.iter())
            .filter(|(seen, _)| seen == method)
            .map(|(_, args)| args.clone())
            .collect()
    }
}

fn backend(terminals: Arc<Mutex<Terminals>>, heard: UnboundedSender<()>) -> FakeBackend {
    FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
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
            (Some("sub"), _) => {
                terminals.seen.push(("sub".into(), frame.clone()));
                let seq = terminals.kept.len();
                let mut replies = vec![Reply::Frame(
                    json!({"t": "state", "sub": id, "seq": seq, "items": []}),
                )];
                for row in &terminals.rows {
                    replies.push(Reply::Frame(json!({"t": "rows", "sub": id, "row": row})));
                }
                replies
            }
            (Some("call"), Some(method)) => {
                terminals.seen.push((method.into(), args.clone()));
                match method {
                    "list" => result(Value::Array(
                        (terminals.running.iter())
                            .map(|(id, owner)| info(id, owner, &json!(96), &json!(28)))
                            .collect(),
                    )),
                    "start" => {
                        let (terminal, owner) = (
                            format!("term-{}", terminals.running.len() + 1),
                            args["owner"].as_str().unwrap_or_default().to_string(),
                        );
                        terminals.running.push((terminal.clone(), owner.clone()));
                        result(info(&terminal, &owner, &args["cols"], &args["rows"]))
                    }
                    "read" => {
                        let offset = args["offset"].as_u64().unwrap_or_default() as usize;
                        let kept = &terminals.kept;
                        let data = STANDARD.encode(&kept[offset.min(kept.len())..]);
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
fn info(id: &str, owner: &str, cols: &Value, rows: &Value) -> Value {
    json!({"id": id, "cwd": "/home/me", "shell": "/bin/bash", "cols": cols, "rows": rows, "pid": 7,
        "owner": owner, "startedAt": "2026-10-29T00:00:00Z"})
}

fn session(id: &str, machine: Option<&str>) -> SessionItem {
    serde_json::from_value(
        json!({"sessionId": id, "agent": "claude", "repoUrl": "/repo", "machine": machine,
        "updatedAt": 0}),
    )
    .unwrap()
}

struct Open<'a> {
    pane: Entity<TerminalPane>,
    terminals: Arc<Mutex<Terminals>>,
    changes: UnboundedReceiver<()>,
    changed: UnboundedSender<()>,
    cx: &'a mut VisualTestContext,
    _heard: Subscription,
    _backend: FakeBackend,
}

/// The pane on a fake backend whose terminals start as `terminals`, with sessions a and b (b on another machine
/// when `remote`), and the tab shown on session a.
async fn open(cx: &mut TestAppContext, terminals: Terminals, remote: bool) -> Open<'_> {
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

fn pick(session: &str, cx: &mut VisualTestContext) {
    let session = Some(session.to_string());
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| selected.select(session, cx))
    });
}

impl Open<'_> {
    async fn until(&mut self, what: &str, mut done: impl FnMut(&Terminals, &TerminalPane) -> bool) {
        let (terminals, pane, cx) = (self.terminals.clone(), self.pane.clone(), &mut *self.cx);
        until(what, &mut self.changes, &self.changed, || {
            cx.run_until_parked();
            let terminals = terminals.lock().unwrap();
            cx.update(|_, cx| done(&terminals, pane.read(cx)))
        })
        .await;
    }

    fn shown(&mut self, active: bool) {
        (self.pane).update_in(self.cx, |pane, window, cx| {
            pane.set_active(active, window, cx)
        });
    }
}

fn following(pane: &TerminalPane) -> bool {
    pane.shown == Shown::Open && !pane.catching_up()
}

// Pins: terminal#1, terminal#9
#[gpui::test]
async fn opening_the_tab_starts_a_shell_in_the_session_folder(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    let size = open.pane.read_with(open.cx, |pane, _| pane.size);
    {
        let terminals = open.terminals.lock().unwrap();
        // No folder: the backend starts it in the owner's session folder (StartArgs.cs, terminal#1).
        assert_eq!(
            terminals.of("start"),
            vec![json!({"owner": "a", "cols": size.0, "rows": size.1})]
        );
    }
    open.shown(false);
    open.shown(true);
    open.until("opened again", |terminals, pane| {
        following(pane) && terminals.of("list").len() == 2 && terminals.of("read").len() == 2
    })
    .await;
    let terminals = open.terminals.lock().unwrap();
    assert_eq!(terminals.of("start").len(), 1);
    assert_eq!(
        terminals.of("read")[1],
        json!({"terminal": "term-1", "offset": 0})
    );
}

// Pins: terminal#2
#[gpui::test]
async fn typing_sends_bytes_in_order(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    let focus = open.pane.read_with(open.cx, |pane, _| pane.focus.clone());
    open.cx.update(|window, cx| window.focus(&focus, cx));
    for (count, key) in ["a", "enter", "up"].into_iter().enumerate() {
        open.cx.simulate_keystrokes(key);
        open.until("the write answered", |terminals, pane| {
            terminals.of("write").len() == count + 1 && !pane.writing()
        })
        .await;
    }
    let terminals = open.terminals.lock().unwrap();
    let written: Vec<Vec<u8>> = (terminals.of("write").iter())
        .map(|args| STANDARD.decode(args["data"].as_str().unwrap()).unwrap())
        .collect();
    assert_eq!(
        written,
        vec![b"a".to_vec(), b"\r".to_vec(), b"\x1b[A".to_vec()]
    );
    assert!(
        terminals
            .of("write")
            .iter()
            .all(|args| args["terminal"] == "term-1")
    );
}

// Pins: terminal#3
#[gpui::test]
async fn output_rows_reach_the_grid_even_when_a_sequence_is_split(cx: &mut TestAppContext) {
    let bytes = "\x1b[32mé\x1b[0m".as_bytes();
    // One cut inside the colour's escape, one inside é's two bytes.
    let rows = vec![
        json!({"seq": 3, "prevSeq": 0, "data": STANDARD.encode(&bytes[..3])}),
        json!({"seq": 6, "prevSeq": 3, "data": STANDARD.encode(&bytes[3..6])}),
        json!({"seq": bytes.len(), "prevSeq": 6, "data": STANDARD.encode(&bytes[6..])}),
    ];
    let mut open = open(
        cx,
        Terminals {
            rows,
            ..Default::default()
        },
        false,
    )
    .await;
    open.until("both rows fed", |_, pane| pane.grid.text() == "é")
        .await;
    open.pane.read_with(open.cx, |pane, _| {
        let cell = pane.grid.cells().into_iter().next().unwrap();
        use alacritty_terminal::vte::ansi::{Color, NamedColor};
        assert_eq!((cell.text, cell.fg), ('é', Color::Named(NamedColor::Green)));
    });
}

// Pins: terminal#4
#[gpui::test]
async fn resizing_the_pane_resizes_the_program(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    let before = open.pane.read_with(open.cx, |pane, _| pane.size);
    open.cx.simulate_resize(gpui_kit::size(px(700.), px(300.)));
    open.until("resized", |terminals, _| !terminals.of("resize").is_empty())
        .await;
    let size = open.pane.read_with(open.cx, |pane, _| pane.size);
    assert_ne!(size, before);
    let terminals = open.terminals.lock().unwrap();
    assert_eq!(
        terminals.of("resize"),
        vec![json!({"terminal": "term-1", "cols": size.0, "rows": size.1})]
    );
    assert_eq!(
        open.pane.read_with(open.cx, |pane, _| pane.grid.size()),
        size
    );
}

// Pins: terminal#5, terminal#9
#[gpui::test]
async fn switching_sessions_keeps_the_shell_and_replays_on_return(cx: &mut TestAppContext) {
    let kept = b"hello\r\n".to_vec();
    let mut open = open(
        cx,
        Terminals {
            kept,
            ..Default::default()
        },
        false,
    )
    .await;
    open.until("a's shell", |_, pane| {
        following(pane) && pane.grid.text() == "hello"
    })
    .await;
    pick("b", open.cx);
    open.until("b's shell", |terminals, pane| {
        following(pane) && terminals.of("start").len() == 2
    })
    .await;
    let before = open.terminals.lock().unwrap().seen.len();
    pick("a", open.cx);
    open.until("a again", |_, pane| {
        following(pane) && pane.grid.text() == "hello"
    })
    .await;
    let reads = {
        let terminals = open.terminals.lock().unwrap();
        assert!(terminals.of("close").is_empty());
        let back: Vec<_> = terminals.seen[before..]
            .iter()
            .map(|(method, _)| method.as_str())
            .collect();
        assert!(
            !back.contains(&"start"),
            "a's return started a shell: {back:?}"
        );
        assert_eq!(back.first(), Some(&"list"));
        let subs = terminals.of("sub");
        assert_eq!(subs.last().unwrap()["key"], "term-1");
        assert_eq!(
            terminals.of("read").last().unwrap(),
            &json!({"terminal": "term-1", "offset": 0})
        );
        terminals.of("read").len()
    };
    // A row past the last offset (7) reads once more from there.
    open.terminals.lock().unwrap().kept = b"hello\r\nmore\r\n".to_vec();
    let row = json!({"seq": 13, "prevSeq": 10, "data": STANDARD.encode(b"e\r\n")});
    open.pane.update(open.cx, |pane, cx| {
        pane.output(
            Update::Change {
                key: "term-1".into(),
                item: row,
            },
            cx,
        )
    });
    open.until("the gap read", |_, pane| {
        following(pane) && pane.grid.text() == "hello\nmore"
    })
    .await;
    let terminals = open.terminals.lock().unwrap();
    assert_eq!(
        terminals.of("read")[reads..],
        [json!({"terminal": "term-1", "offset": 7})]
    );
}

// Pins: terminal#7
#[gpui::test]
async fn exit_keeps_the_output_and_shows_the_status(cx: &mut TestAppContext) {
    let kept = b"bye\r\n".to_vec();
    let rows = vec![json!({"seq": kept.len() + 1, "prevSeq": kept.len(), "exit": 1})];
    let mut open = open(
        cx,
        Terminals {
            kept,
            rows,
            ..Default::default()
        },
        false,
    )
    .await;
    open.until("exited", |_, pane| pane.shown == Shown::Exited(1))
        .await;
    open.pane.read_with(open.cx, |pane, _| {
        assert_eq!(pane.grid.text(), "bye");
        assert_eq!(pane.status().as_deref(), Some("exited with 1"));
    });
}

// Pins: terminal#12
#[gpui::test]
async fn a_remote_session_shows_the_limitation(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), true).await;
    open.until("a's shell", |_, pane| following(pane)).await;
    pick("b", open.cx);
    open.until("the limitation", |_, pane| pane.status().is_some())
        .await;
    open.pane.read_with(open.cx, |pane, _| {
        assert_eq!(
            pane.status().as_deref(),
            Some("Remote terminals aren't available yet.")
        );
    });
    let terminals = open.terminals.lock().unwrap();
    assert!(
        terminals
            .of("start")
            .iter()
            .all(|args| args["owner"] == "a")
    );
}

// Pins: terminal#5, terminal#7
#[gpui::test]
async fn a_reconnect_reads_what_was_missed_and_keeps_the_exit(cx: &mut TestAppContext) {
    let kept = b"abc".to_vec();
    let mut open = open(
        cx,
        Terminals {
            kept,
            ..Default::default()
        },
        false,
    )
    .await;
    open.until("following", |_, pane| {
        following(pane) && pane.grid.text() == "abc"
    })
    .await;
    // While the socket was down the shell wrote more and ended; the new subscription's state comes first.
    open.terminals.lock().unwrap().kept = b"abcdef".to_vec();
    open.pane.update(open.cx, |pane, cx| {
        pane.output(Update::State(Vec::new()), cx);
        let exit = json!({"seq": 7, "prevSeq": 6, "exit": 0});
        pane.output(
            Update::Change {
                key: "term-1".into(),
                item: exit,
            },
            cx,
        );
    });
    open.until("caught up", |_, pane| pane.shown == Shown::Exited(0))
        .await;
    open.pane
        .read_with(open.cx, |pane, _| assert_eq!(pane.grid.text(), "abcdef"));
}

// Pins: terminal#4
#[gpui::test]
async fn the_size_stays_within_what_the_backend_allows(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    open.pane
        .update(open.cx, |pane, cx| pane.resized((400, 2), cx));
    open.until("resized", |terminals, _| !terminals.of("resize").is_empty())
        .await;
    let first = open.terminals.lock().unwrap().of("resize")[0].clone();
    assert_eq!(first, json!({"terminal": "term-1", "cols": 300, "rows": 4}));
}
