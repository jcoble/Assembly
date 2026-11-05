//! The Terminal tab on the socket client against `FakeBackend`, which plays the backend's `terminal` feature
//! (TerminalFeature.cs): when it starts, reuses, writes to, resizes and reads its session's shell, and what it shows.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::TestAppContext;
use gpui_kit::{Subscription, px};
use serde_json::{Value, json};

use super::Shown;
use crate::core::socket::client::SocketClient;
use crate::core::socket::test_wait::until;
use crate::core::socket::update::Update;

#[path = "terminal_pane_fixture.rs"]
mod fixture;
use fixture::*;

// Pins: terminal#1, terminal#9
#[gpui::test]
async fn opening_the_tab_starts_a_shell_in_the_session_folder(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    let opening = open.pane.read_with(open.cx, |pane, _| pane.status());
    assert_eq!(opening.as_deref(), Some("Opening terminal…"));
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
async fn a_remote_session_opens_and_reuses_its_shell(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), true).await;
    open.until("a's shell", |_, pane| following(pane)).await;
    pick("b", open.cx);
    open.until("b's remote shell", |terminals, pane| {
        following(pane) && terminals.of("start").len() == 2
    })
    .await;
    pick("a", open.cx);
    open.until("a again", |_, pane| following(pane)).await;
    pick("b", open.cx);
    open.until("b again", |terminals, pane| {
        following(pane) && terminals.of("list").len() == 4
    })
    .await;
    assert_eq!(open.terminals.lock().unwrap().of("start").len(), 2);
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

#[gpui::test]
async fn calls_and_output_use_only_the_terminal_socket(cx: &mut TestAppContext) {
    let mut open = open(
        cx,
        Terminals {
            rows: vec![json!({"seq": 5, "prevSeq": 0, "data": STANDARD.encode(b"hello")})],
            ..Default::default()
        },
        false,
    )
    .await;
    open.until("the terminal output arrived", |_, pane| {
        following(pane) && pane.grid.text().contains("hello")
    })
    .await;
    open.cx.simulate_keystrokes("x");
    open.until("typed", |terminals, pane| {
        !terminals.of("write").is_empty() && !pane.writing()
    })
    .await;
    open.pane
        .update(open.cx, |pane, cx| pane.resized((120, 40), cx));
    open.until("resized", |terminals, _| !terminals.of("resize").is_empty())
        .await;
    open.pane
        .read_with(open.cx, |pane, cx| pane.link.as_ref().unwrap().close(cx));
    open.until("closed", |terminals, _| !terminals.of("close").is_empty())
        .await;
    for method in ["start", "list", "read", "write", "resize", "close"] {
        assert!(
            !open.terminals.lock().unwrap().of(method).is_empty(),
            "missing {method}"
        );
    }
    let subscriptions: Vec<_> = open
        ._backend
        .on
        .try_iter()
        .filter(|(_, frame)| frame["feature"] == "terminal" || frame["topic"] == "terminal")
        .collect();
    assert!(!subscriptions.is_empty());
    assert!(
        subscriptions
            .iter()
            .all(|(path, _)| path == "/socket/terminal")
    );
}

// Pins: terminal#7
#[gpui::test]
async fn an_ended_shell_keeps_its_output_above_the_new_one(cx: &mut TestAppContext) {
    let ended = Some(("term-0".to_string(), "a".to_string(), b"old\r\n".to_vec()));
    let terminals = Terminals {
        kept: b"new\r\n".to_vec(),
        ended,
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    open.until("the new shell", |_, pane| {
        following(pane) && pane.grid.text() == "old\nnew"
    })
    .await;
    let terminals = open.terminals.lock().unwrap();
    assert_eq!(terminals.of("start").len(), 1);
    assert_eq!(
        terminals.of("read")[0],
        json!({"terminal": "term-0", "offset": 0})
    );
}

#[path = "terminal_pane_input_tests.rs"]
mod input;

#[path = "terminal_pane_typing_tests.rs"]
mod typing;

#[path = "terminal_pane_mouse_tests.rs"]
mod mouse;

#[path = "terminal_pane_select_tests.rs"]
mod select;

#[path = "terminal_pane_dock_tests.rs"]
mod dock;

#[path = "terminal_replay_tests.rs"]
mod replay;

#[path = "terminal_pane_state_tests.rs"]
mod state;

#[path = "terminal_scrollbar_tests.rs"]
mod scrollbar;

#[path = "terminal_pane_links_tests.rs"]
mod links;

#[path = "terminal_pane_file_links_tests.rs"]
mod file_links;

#[path = "terminal_pane_lost_release_tests.rs"]
mod lost_release;

#[path = "terminal_pane_window_blink_tests.rs"]
mod window_blink;
