//! What a lost mouse release, a stopped shell and a hide leave behind on the Terminal tab: a move with no button or a
//! new press ends a stale drag and its autoscroll, a stopped shell's last program holds no mouse, and a hide drops the
//! focus the shell's first state asked for.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{ClipboardItem, Modifiers, MouseButton, Pixels, Point, point, px};
use serde_json::json;

use super::mouse::{at, top_line};
use super::{Open, Shown, Terminals, following, open};

fn none() -> Modifiers {
    Modifiers::none()
}

fn autoscrolling(open: &mut Open<'_>) -> bool {
    (open.pane).read_with(open.cx, |pane, _| pane._autoscroll.is_some())
}

fn selecting(open: &mut Open<'_>) -> bool {
    (open.pane).read_with(open.cx, |pane, _| pane.selecting)
}

fn clipboard(open: &mut Open<'_>) -> Option<String> {
    (open.cx).update(|_, cx| cx.read_from_clipboard().and_then(|item| item.text()))
}

fn written(open: &Open<'_>) -> Vec<u8> {
    (open.terminals.lock().unwrap().of("write").iter())
        .flat_map(|args| STANDARD.decode(args["data"].as_str().unwrap()).unwrap())
        .collect()
}

/// Near the grid's top edge, inside it, where a drag scrolls back.
fn near_top(open: &mut Open<'_>) -> Point<Pixels> {
    let origin = open.pane.read_with(open.cx, |pane, _| pane.origin);
    point(origin.x + px(20.), origin.y + px(5.))
}

/// The middle of the grid, far from either edge.
fn middle(open: &mut Open<'_>) -> Point<Pixels> {
    let rows = open.pane.read_with(open.cx, |pane, _| pane.size.1);
    at(open, 1.2, rows as f32 / 2.)
}

#[gpui::test]
async fn a_lost_release_ends_the_drag_and_its_autoscroll(cx: &mut TestAppContext) {
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
    let (top, middle) = (near_top(&mut open), middle(&mut open));

    // The release never came: the next move has no button held.
    open.cx
        .simulate_mouse_down(middle, MouseButton::Left, none());
    open.cx.simulate_mouse_move(top, MouseButton::Left, none());
    open.advance(50);
    assert_eq!(top_line(&mut open), (bottom - 3).to_string());
    assert!(autoscrolling(&mut open));
    open.cx.simulate_mouse_move(top, None, none());
    assert!(!autoscrolling(&mut open), "a move with no button ends it");
    assert!(!selecting(&mut open));
    open.advance(500);
    assert_eq!(top_line(&mut open), (bottom - 3).to_string());

    // The release never came, and the next thing is a new press.
    open.cx
        .simulate_mouse_down(middle, MouseButton::Left, none());
    open.cx.simulate_mouse_move(top, MouseButton::Left, none());
    open.advance(50);
    assert_eq!(top_line(&mut open), (bottom - 6).to_string());
    assert!(autoscrolling(&mut open));
    open.cx
        .simulate_mouse_down(middle, MouseButton::Left, none());
    assert!(!autoscrolling(&mut open), "a new press ends it");
    open.advance(500);
    assert_eq!(top_line(&mut open), (bottom - 6).to_string());
    open.cx.simulate_mouse_up(middle, MouseButton::Left, none());
}

#[gpui::test]
async fn after_exit_a_mouse_program_left_on_holds_no_mouse(cx: &mut TestAppContext) {
    // The last program turned mouse reporting on and never turned it off.
    let kept = "hello\x1b[?1000h\x1b[?1006h";
    let terminals = Terminals {
        kept: kept.as_bytes().to_vec(),
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    open.until("the output", |_, pane| {
        following(pane) && pane.grid.text() == "hello"
    })
    .await;
    open.push_row(json!({"seq": kept.len() + 1, "prevSeq": kept.len(), "exit": 0}));
    let exited = (open.pane).read_with(open.cx, |pane, _| pane.shown == Shown::Exited(0));
    assert!(exited);

    // Selecting and copying.
    let (from, to) = (at(&mut open, 0.2, 0.5), at(&mut open, 4.8, 0.5));
    open.cx.simulate_mouse_down(from, MouseButton::Left, none());
    open.cx.simulate_mouse_move(to, MouseButton::Left, none());
    open.cx.simulate_mouse_up(to, MouseButton::Left, none());
    let selected = (open.pane).read_with(open.cx, |pane, _| pane.grid.selection_text());
    assert_eq!(selected.as_deref(), Some("hello"));
    assert_eq!(clipboard(&mut open).as_deref(), Some("hello"));

    // The menu: its first item, Copy, puts the selection on the clipboard.
    let item = ClipboardItem::new_string("kept".into());
    open.cx.update(|_, cx| cx.write_to_clipboard(item));
    let on_the_text = at(&mut open, 1.5, 0.5);
    open.cx
        .simulate_mouse_down(on_the_text, MouseButton::Right, none());
    open.cx
        .simulate_mouse_up(on_the_text, MouseButton::Right, none());
    open.cx.run_until_parked();
    open.cx.update(|window, cx| window.draw(cx).clear(cx));
    open.cx.simulate_keystrokes("down enter");
    open.cx.run_until_parked();
    assert_eq!(clipboard(&mut open).as_deref(), Some("hello"));
    assert!(written(&open).is_empty(), "the ended program heard the mouse");
}

#[gpui::test]
async fn hiding_drops_the_focus_the_shell_asked_for(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    open.cx.run_until_parked();
    open.cx.update(|window, cx| window.blur(cx));
    open.cx.run_until_parked();
    // As a hide during the first catch-up leaves it: the shell's first state asked for the focus, not yet taken.
    (open.pane).update_in(open.cx, |pane, window, cx| {
        pane.focus_wanted = true;
        pane.set_active(false, window, cx);
    });
    open.cx.run_until_parked();
    open.cx.update(|window, cx| window.draw(cx).clear(cx));
    open.cx.run_until_parked();
    let focus = open.pane.read_with(open.cx, |pane, _| pane.focus.clone());
    assert!(!open.cx.update(|window, _| focus.is_focused(window)));
}
