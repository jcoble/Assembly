//! The mouse on the Terminal tab: dragging selects, Copy puts the selection on the clipboard, and the wheel scrolls
//! back through the history, or goes to a full-screen program as arrow keys.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::TestAppContext;
use gpui_kit::{Modifiers, MouseButton, ScrollDelta, ScrollEvent, point};
use serde_json::json;

use super::{Open, Terminals, following, open};

/// The window position of a cell's left or right half.
fn at(open: &mut Open<'_>, column: f32, line: f32) -> gpui_kit::Point<gpui_kit::Pixels> {
    let (origin, cell) = open
        .pane
        .read_with(open.cx, |pane, _| (pane.origin, pane.cell));
    point(
        origin.x + cell.width * column,
        origin.y + cell.height * line,
    )
}

fn wheel(open: &mut Open<'_>, lines: f32) {
    let position = at(open, 1., 1.);
    open.cx.simulate_event(ScrollEvent {
        position,
        delta: ScrollDelta::Lines(point(0., lines)),
        ..Default::default()
    });
}

fn top_line(open: &mut Open<'_>) -> String {
    let text = open.pane.read_with(open.cx, |pane, _| pane.grid.text());
    text.lines().next().unwrap_or_default().to_string()
}

#[gpui::test]
async fn dragging_selects_and_copy_puts_it_on_the_clipboard(cx: &mut TestAppContext) {
    let hello = STANDARD.encode(b"hello world");
    let terminals = Terminals {
        rows: vec![json!({"seq": 11, "prevSeq": 0, "data": hello})],
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    open.until("the output", |_, pane| {
        following(pane) && pane.grid.text() == "hello world"
    })
    .await;
    let (from, to) = (at(&mut open, 0.2, 0.5), at(&mut open, 4.8, 0.5));
    let none = Modifiers::none();
    open.cx.simulate_mouse_down(from, MouseButton::Left, none);
    open.cx.simulate_mouse_move(to, MouseButton::Left, none);
    open.cx.simulate_mouse_up(to, MouseButton::Left, none);
    open.cx.simulate_keystrokes("ctrl-shift-c");
    open.cx.run_until_parked();
    let copied = open
        .cx
        .update(|_, cx| cx.read_from_clipboard().and_then(|item| item.text()));
    assert_eq!(copied.as_deref(), Some("hello"));
    assert!(open.terminals.lock().unwrap().of("write").is_empty());
}

#[gpui::test]
async fn the_wheel_scrolls_back_and_typing_returns_to_the_bottom(cx: &mut TestAppContext) {
    let kept: String = (1..=200).map(|line| format!("{line}\r\n")).collect();
    let terminals = Terminals {
        kept: kept.into_bytes(),
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    open.until("the output", |_, pane| {
        following(pane) && pane.grid.text().ends_with("200")
    })
    .await;
    let bottom: usize = top_line(&mut open).parse().unwrap();
    wheel(&mut open, 3.);
    open.cx.run_until_parked();
    assert_eq!(top_line(&mut open), (bottom - 3).to_string());
    open.cx.simulate_keystrokes("a");
    open.cx.run_until_parked();
    assert_eq!(top_line(&mut open), bottom.to_string());
}

#[gpui::test]
async fn the_wheel_sends_arrows_to_a_full_screen_program(cx: &mut TestAppContext) {
    let full_screen = STANDARD.encode(b"\x1b[?1049h");
    let terminals = Terminals {
        rows: vec![json!({"seq": 8, "prevSeq": 0, "data": full_screen})],
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    open.until("full screen", |_, pane| {
        following(pane) && pane.grid.wheel_sends_arrows()
    })
    .await;
    wheel(&mut open, 2.);
    open.until("the arrows", |terminals, pane| {
        !terminals.of("write").is_empty() && !pane.writing()
    })
    .await;
    let sent: Vec<u8> = (open.terminals.lock().unwrap().of("write").iter())
        .flat_map(|args| STANDARD.decode(args["data"].as_str().unwrap()).unwrap())
        .collect();
    assert_eq!(sent, b"\x1b[A\x1b[A".to_vec());
}

#[gpui::test]
async fn a_drag_past_the_right_edge_takes_the_last_column(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    let cols = open.pane.read_with(open.cx, |pane, _| pane.size.0);
    let line = "x".repeat(cols);
    let row = json!({"seq": cols, "prevSeq": 0, "data": STANDARD.encode(&line)});
    open.pane.update(open.cx, |pane, cx| {
        pane.output(
            super::Update::Change {
                key: "term-1".into(),
                item: row,
            },
            cx,
        )
    });
    open.cx.run_until_parked();
    let (from, to) = (
        at(&mut open, 0.2, 0.5),
        at(&mut open, cols as f32 + 0.3, 0.5),
    );
    let none = Modifiers::none();
    open.cx.simulate_mouse_down(from, MouseButton::Left, none);
    open.cx.simulate_mouse_move(to, MouseButton::Left, none);
    open.cx.simulate_mouse_up(to, MouseButton::Left, none);
    open.cx.simulate_keystrokes("ctrl-shift-c");
    open.cx.run_until_parked();
    let copied = open
        .cx
        .update(|_, cx| cx.read_from_clipboard().and_then(|item| item.text()));
    assert_eq!(copied.map(|text| text.len()), Some(cols));
}

/// Reports must travel through the pane's real input/write queue, without creating a selection.
#[gpui::test]
async fn sgr_mouse_reports_buttons_drag_release_wheel_and_modifiers(cx: &mut TestAppContext) {
    let mut open = mouse_program(cx, b"\x1b[?1002h\x1b[?1006h").await;
    let (from, to) = (at(&mut open, 2.2, 1.2), at(&mut open, 3.2, 2.2));
    let modifiers = Modifiers {
        shift: true,
        alt: true,
        control: true,
        ..Modifiers::none()
    };
    open.cx
        .simulate_mouse_down(from, MouseButton::Right, modifiers);
    open.cx
        .simulate_mouse_move(to, MouseButton::Right, modifiers);
    open.cx.simulate_mouse_up(to, MouseButton::Right, modifiers);
    open.cx.simulate_event(ScrollEvent {
        position: to,
        modifiers,
        delta: ScrollDelta::Lines(point(0., -2.)),
        ..Default::default()
    });
    assert_eq!(
        sent(&mut open).await,
        b"\x1b[<30;3;2M\x1b[<62;4;3M\x1b[<30;4;3m\x1b[<93;4;3M\x1b[<93;4;3M"
    );
    open.pane.read_with(open.cx, |pane, _| {
        assert!(pane.grid.selection_text().is_none())
    });
}

#[gpui::test]
async fn normal_mouse_reports_click_release_and_wheel_but_not_motion(cx: &mut TestAppContext) {
    let mut open = mouse_program(cx, b"\x1b[?1000h").await;
    let (from, to) = (at(&mut open, 2.2, 1.2), at(&mut open, 3.2, 2.2));
    let none = Modifiers::none();
    open.cx.simulate_mouse_down(from, MouseButton::Left, none);
    open.cx.simulate_mouse_move(to, MouseButton::Left, none);
    open.cx.simulate_mouse_up(to, MouseButton::Left, none);
    wheel(&mut open, 1.);
    assert_eq!(sent(&mut open).await, b"\x1b[M #\"\x1b[M#\x24#\x1b[M`\"\"");
}

#[gpui::test]
async fn any_motion_reports_hover_and_utf8_encoding_keeps_large_columns(cx: &mut TestAppContext) {
    let mut open = mouse_program(cx, b"\x1b[?1003h\x1b[?1005h").await;
    let position = at(&mut open, 250.2, 1.2);
    // This coordinate exceeds the test window's pixel width. Exercise the same handler with its measured grid.
    open.cx.update(|window, cx| {
        open.pane.update(cx, |pane, cx| {
            pane.resized((300, 20), cx);
            pane.mouse_move(
                &gpui_kit::MouseMoveEvent {
                    position,
                    ..Default::default()
                },
                window,
                cx,
            );
        })
    });
    assert_eq!(sent(&mut open).await, "\x1b[MC\u{11b}\"".as_bytes());
}

#[gpui::test]
async fn disabling_mouse_reporting_restores_selection(cx: &mut TestAppContext) {
    let mut open = mouse_program(cx, b"hello\x1b[?1002h\x1b[?1006h\x1b[?1002l").await;
    let (from, to) = (at(&mut open, 0.2, 0.5), at(&mut open, 4.8, 0.5));
    let none = Modifiers::none();
    open.cx.simulate_mouse_down(from, MouseButton::Left, none);
    open.cx.simulate_mouse_move(to, MouseButton::Left, none);
    open.cx.simulate_mouse_up(to, MouseButton::Left, none);
    open.pane.read_with(open.cx, |pane, _| {
        assert_eq!(pane.grid.selection_text().as_deref(), Some("hello"))
    });
    assert!(open.terminals.lock().unwrap().of("write").is_empty());
}

async fn mouse_program<'a>(cx: &'a mut TestAppContext, modes: &[u8]) -> Open<'a> {
    let mut open = open(
        cx,
        Terminals {
            kept: modes.to_vec(),
            ..Default::default()
        },
        false,
    )
    .await;
    open.until("the program's modes", |_, pane| following(pane))
        .await;
    open
}

async fn sent(open: &mut Open<'_>) -> Vec<u8> {
    open.until("mouse input delivered", |terminals, pane| {
        !terminals.of("write").is_empty() && !pane.writing()
    })
    .await;
    open.terminals
        .lock()
        .unwrap()
        .of("write")
        .iter()
        .flat_map(|args| STANDARD.decode(args["data"].as_str().unwrap()).unwrap())
        .collect()
}
