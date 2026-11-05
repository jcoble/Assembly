//! The mouse selecting on the Terminal tab: copying when the button comes up, a double-click's word, clearing on a click
//! outside, scrolling while dragging near an edge, and the right-click menu.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::TestAppContext;
use gpui_kit::{ClipboardItem, Modifiers, MouseButton, MouseDownEvent, Pixels, point, px};
use serde_json::json;

use super::mouse::{at, top_line, wheel};
use super::{Open, Terminals, following, open};

fn none() -> Modifiers {
    Modifiers::none()
}

fn clipboard(open: &mut Open<'_>) -> Option<String> {
    (open.cx).update(|_, cx| cx.read_from_clipboard().and_then(|item| item.text()))
}

fn set_clipboard(open: &mut Open<'_>, text: &str) {
    let item = ClipboardItem::new_string(text.into());
    open.cx.update(|_, cx| cx.write_to_clipboard(item));
}

fn written(open: &Open<'_>) -> Vec<u8> {
    (open.terminals.lock().unwrap().of("write").iter())
        .flat_map(|args| STANDARD.decode(args["data"].as_str().unwrap()).unwrap())
        .collect()
}

/// The tab following a shell that has printed `text`.
async fn printed<'a>(cx: &'a mut TestAppContext, text: &str) -> Open<'a> {
    let terminals = Terminals {
        kept: text.as_bytes().to_vec(),
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    let expected = text.to_string();
    open.until("the output", |_, pane| {
        following(pane) && pane.grid.text() == expected
    })
    .await;
    open
}

/// The tab following a shell with 200 lines printed, so most are history. The line on top, at the bottom of the
/// history, comes back too.
async fn history<'a>(cx: &'a mut TestAppContext) -> (Open<'a>, usize) {
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
    let bottom = top_line(&mut open).parse().unwrap();
    (open, bottom)
}

fn drag(open: &mut Open<'_>, from: gpui_kit::Point<Pixels>, to: gpui_kit::Point<Pixels>) {
    open.cx.simulate_mouse_down(from, MouseButton::Left, none());
    open.cx.simulate_mouse_move(to, MouseButton::Left, none());
    open.cx.simulate_mouse_up(to, MouseButton::Left, none());
}

fn click(open: &mut Open<'_>, at: gpui_kit::Point<Pixels>) {
    open.cx.simulate_mouse_down(at, MouseButton::Left, none());
    open.cx.simulate_mouse_up(at, MouseButton::Left, none());
}

fn double_click(open: &mut Open<'_>, at: gpui_kit::Point<Pixels>) {
    open.cx.simulate_event(MouseDownEvent {
        position: at,
        button: MouseButton::Left,
        modifiers: none(),
        click_count: 2,
        first_mouse: false,
    });
    open.cx.simulate_mouse_up(at, MouseButton::Left, none());
}

fn selection(open: &mut Open<'_>) -> Option<String> {
    (open.pane).read_with(open.cx, |pane, _| pane.grid.selection_text())
}

/// Near the grid's top edge, inside it.
fn near_top(open: &mut Open<'_>) -> gpui_kit::Point<Pixels> {
    let (origin, _) = open
        .pane
        .read_with(open.cx, |pane, _| (pane.origin, pane.cell));
    point(origin.x + px(20.), origin.y + px(5.))
}

/// Near the grid's bottom edge, inside it.
fn near_bottom(open: &mut Open<'_>) -> gpui_kit::Point<Pixels> {
    let (origin, cell, rows) =
        (open.pane).read_with(open.cx, |pane, _| (pane.origin, pane.cell, pane.size.1));
    point(
        origin.x + px(20.),
        origin.y + cell.height * rows as f32 - px(5.),
    )
}

/// Starts a selecting drag in the middle of the grid, far from either edge.
fn start_drag(open: &mut Open<'_>) {
    let rows = open.pane.read_with(open.cx, |pane, _| pane.size.1);
    let from = at(open, 1.2, rows as f32 / 2.);
    open.cx.simulate_mouse_down(from, MouseButton::Left, none());
}

fn drag_to(open: &mut Open<'_>, to: gpui_kit::Point<Pixels>) {
    open.cx.simulate_mouse_move(to, MouseButton::Left, none());
}

/// Opens the context menu at a cell and draws it.
fn right_click(open: &mut Open<'_>, at: gpui_kit::Point<Pixels>) {
    open.cx.simulate_mouse_down(at, MouseButton::Right, none());
    open.cx.simulate_mouse_up(at, MouseButton::Right, none());
    open.cx.run_until_parked();
    open.cx.update(|window, cx| window.draw(cx).clear(cx));
}

/// Keys for the open menu. The kit's first `down` lands on the first item, whether or not it's off; each later one moves
/// to the next item that's on, wrapping to the first; `up` moves to the one before that's on, wrapping to the last.
fn menu_keys(open: &mut Open<'_>, keys: &str) {
    open.cx.simulate_keystrokes(keys);
    open.cx.run_until_parked();
}

#[gpui::test]
async fn selecting_copies_when_the_button_comes_up(cx: &mut TestAppContext) {
    let mut open = printed(cx, "hello world").await;
    let (from, to) = (at(&mut open, 0.2, 0.5), at(&mut open, 4.8, 0.5));
    open.cx.simulate_mouse_down(from, MouseButton::Left, none());
    open.cx.simulate_mouse_move(to, MouseButton::Left, none());
    assert_eq!(clipboard(&mut open), None, "not until the button comes up");
    open.cx.simulate_mouse_up(to, MouseButton::Left, none());
    assert_eq!(clipboard(&mut open).as_deref(), Some("hello"));
}

#[gpui::test]
async fn a_plain_click_copies_nothing(cx: &mut TestAppContext) {
    let mut open = printed(cx, "hello world").await;
    // A drag does copy, so the click below isn't quiet only because nothing ever copies.
    let (from, to) = (at(&mut open, 0.2, 0.5), at(&mut open, 4.8, 0.5));
    drag(&mut open, from, to);
    assert_eq!(clipboard(&mut open).as_deref(), Some("hello"));
    set_clipboard(&mut open, "kept");
    let on_a_word = at(&mut open, 7.2, 0.5);
    click(&mut open, on_a_word);
    assert_eq!(clipboard(&mut open).as_deref(), Some("kept"));
}

#[gpui::test]
async fn double_click_selects_and_copies_a_word(cx: &mut TestAppContext) {
    let mut open = printed(cx, "foo-bar_1 baz").await;
    let word = at(&mut open, 4.5, 0.5);
    double_click(&mut open, word);
    assert_eq!(selection(&mut open).as_deref(), Some("foo-bar_1"));
    assert_eq!(clipboard(&mut open).as_deref(), Some("foo-bar_1"));
}

#[gpui::test]
async fn double_click_on_a_blank_selects_nothing(cx: &mut TestAppContext) {
    let mut open = printed(cx, "foo   bar").await;
    let word = at(&mut open, 1.5, 0.5);
    double_click(&mut open, word);
    assert_eq!(selection(&mut open).as_deref(), Some("foo"));
    set_clipboard(&mut open, "kept");
    let blank = at(&mut open, 4.5, 0.5);
    double_click(&mut open, blank);
    assert_eq!(selection(&mut open), None);
    assert_eq!(clipboard(&mut open).as_deref(), Some("kept"));
    let found = (open.pane).update(open.cx, |pane, _| pane.grid.select_word(4, 0));
    assert!(!found);
}

#[test]
fn a_word_stops_at_the_row_end() {
    use crate::features::terminal::terminal_grid::TerminalGrid;
    let mut grid = TerminalGrid::new(10, 3);
    // Twelve letters: ten on the first row, wrapped, and two on the next.
    grid.feed(b"abcdefghijkl");
    assert!(grid.select_word(3, 0));
    assert_eq!(grid.selection_text().as_deref(), Some("abcdefghij"));
    assert!(grid.select_word(0, 1));
    assert_eq!(grid.selection_text().as_deref(), Some("kl"));
}

#[gpui::test]
async fn clicking_outside_clears_the_selection(cx: &mut TestAppContext) {
    let mut open = printed(cx, "hello world").await;
    let (from, to) = (at(&mut open, 0.2, 0.5), at(&mut open, 4.8, 0.5));
    drag(&mut open, from, to);
    assert_eq!(selection(&mut open).as_deref(), Some("hello"));
    // The pane fills the test's window, so outside it is past the window's corner.
    let size = open.cx.update(|window, _| window.viewport_size());
    let outside = point(size.width + px(50.), size.height + px(50.));
    click(&mut open, outside);
    assert_eq!(selection(&mut open), None);
}

#[gpui::test]
async fn dragging_near_the_edge_scrolls_three_lines_every_50_ms(cx: &mut TestAppContext) {
    let (mut open, bottom) = history(cx).await;
    start_drag(&mut open);
    let top = near_top(&mut open);
    drag_to(&mut open, top);
    let before = selection(&mut open);
    open.advance(50);
    assert_eq!(top_line(&mut open), (bottom - 3).to_string());
    open.advance(50);
    assert_eq!(top_line(&mut open), (bottom - 6).to_string());
    assert!(
        selection(&mut open).map(|text| text.len()) > before.map(|text| text.len()),
        "the selection grows as it scrolls"
    );
    open.cx.simulate_mouse_up(top, MouseButton::Left, none());

    // The bottom edge scrolls back down, from 20 lines up the history.
    wheel(&mut open, 14.);
    open.cx.run_until_parked();
    let from: usize = top_line(&mut open).parse().unwrap();
    start_drag(&mut open);
    let lower = near_bottom(&mut open);
    drag_to(&mut open, lower);
    open.advance(50);
    assert_eq!(top_line(&mut open), (from + 3).to_string());
    open.advance(50);
    assert_eq!(top_line(&mut open), (from + 6).to_string());
    open.cx.simulate_mouse_up(lower, MouseButton::Left, none());
}

#[gpui::test]
async fn dragging_past_the_pane_keeps_scrolling(cx: &mut TestAppContext) {
    let (mut open, bottom) = history(cx).await;
    start_drag(&mut open);
    let above = near_top(&mut open) - point(px(0.), px(200.));
    drag_to(&mut open, above);
    open.advance(50);
    assert_eq!(top_line(&mut open), (bottom - 3).to_string());
    open.advance(50);
    assert_eq!(top_line(&mut open), (bottom - 6).to_string());
    let size = open.cx.update(|window, _| window.viewport_size());
    let below = point(px(20.), size.height + px(100.));
    drag_to(&mut open, below);
    open.advance(50);
    assert_eq!(top_line(&mut open), (bottom - 3).to_string());
    open.cx.simulate_mouse_up(below, MouseButton::Left, none());
}

#[gpui::test]
async fn autoscroll_stops_on_leaving_the_zone_on_release_and_on_hide(cx: &mut TestAppContext) {
    let (mut open, bottom) = history(cx).await;
    let top = near_top(&mut open);
    let rows = open.pane.read_with(open.cx, |pane, _| pane.size.1);
    let middle = at(&mut open, 1.2, rows as f32 / 2.);
    let autoscrolling = |open: &mut Open<'_>| {
        open.pane
            .read_with(open.cx, |pane, _| pane._autoscroll.is_some())
    };

    // Leaving the zone.
    start_drag(&mut open);
    drag_to(&mut open, top);
    open.advance(50);
    assert_eq!(top_line(&mut open), (bottom - 3).to_string());
    assert!(autoscrolling(&mut open));
    drag_to(&mut open, middle);
    assert!(!autoscrolling(&mut open));
    open.advance(500);
    assert_eq!(top_line(&mut open), (bottom - 3).to_string());

    // Letting go.
    drag_to(&mut open, top);
    open.advance(50);
    assert_eq!(top_line(&mut open), (bottom - 6).to_string());
    open.cx.simulate_mouse_up(top, MouseButton::Left, none());
    assert!(!autoscrolling(&mut open));
    open.advance(500);
    assert_eq!(top_line(&mut open), (bottom - 6).to_string());

    // Hiding: the drag is over too, and a hidden pane runs no timer.
    start_drag(&mut open);
    drag_to(&mut open, top);
    assert!(autoscrolling(&mut open));
    open.shown(false);
    open.pane.read_with(open.cx, |pane, _| {
        assert!(pane._autoscroll.is_none());
        assert!(!pane.selecting);
    });
    open.shown(true);
    open.until("back", |_, pane| {
        following(pane) && pane.grid.text().ends_with("200")
    })
    .await;
    open.advance(500);
    assert_eq!(top_line(&mut open), bottom.to_string());
}

#[gpui::test]
async fn the_menu_has_copy_and_paste_with_copy_off_when_nothing_is_selected(
    cx: &mut TestAppContext,
) {
    let mut open = printed(cx, "hello world").await;
    set_clipboard(&mut open, "pasted");
    // Nothing selected: Copy is off, so `up` from Paste has nowhere before it to go and stays there.
    let on_the_text = at(&mut open, 1.5, 0.5);
    right_click(&mut open, on_the_text);
    menu_keys(&mut open, "down down up enter");
    open.until("pasted", |terminals, pane| {
        !terminals.of("write").is_empty() && !pane.writing()
    })
    .await;
    assert_eq!(written(&open), b"pasted");

    // Selected: Copy, the first item, puts it on the clipboard.
    let (from, to) = (at(&mut open, 0.2, 0.5), at(&mut open, 4.8, 0.5));
    drag(&mut open, from, to);
    set_clipboard(&mut open, "kept");
    right_click(&mut open, on_the_text);
    menu_keys(&mut open, "down enter");
    assert_eq!(clipboard(&mut open).as_deref(), Some("hello"));
    assert_eq!(written(&open), b"pasted");
}

#[gpui::test]
async fn paste_is_off_when_typing_is(cx: &mut TestAppContext) {
    let mut open = printed(cx, "hello world").await;
    let (from, to) = (at(&mut open, 0.2, 0.5), at(&mut open, 4.8, 0.5));
    drag(&mut open, from, to);
    assert!(open.pane.read_with(open.cx, |pane, _| pane.typing()));
    open.push_row(json!({"seq": 12, "prevSeq": 11, "exit": 7}));
    assert!(!open.pane.read_with(open.cx, |pane, _| pane.typing()));
    // Paste is off, so the next item that's on wraps round to Copy.
    set_clipboard(&mut open, "kept");
    let on_the_text = at(&mut open, 1.5, 0.5);
    right_click(&mut open, on_the_text);
    menu_keys(&mut open, "down down enter");
    assert_eq!(clipboard(&mut open).as_deref(), Some("hello"));
    assert!(written(&open).is_empty());
    open.pane.update(open.cx, |pane, cx| pane.paste(cx));
    open.cx.run_until_parked();
    assert!(written(&open).is_empty());
}

#[gpui::test]
async fn paste_from_the_menu_writes(cx: &mut TestAppContext) {
    let mut open = printed(cx, "hello world").await;
    set_clipboard(&mut open, "one\ntwo");
    let on_the_text = at(&mut open, 1.5, 0.5);
    right_click(&mut open, on_the_text);
    menu_keys(&mut open, "down down enter");
    open.until("pasted", |terminals, pane| {
        !terminals.of("write").is_empty() && !pane.writing()
    })
    .await;
    assert_eq!(written(&open), b"one\rtwo");
    open.pane.update(open.cx, |pane, cx| pane.paste(cx));
    open.until("pasted again", |terminals, pane| {
        terminals.of("write").len() == 2 && !pane.writing()
    })
    .await;
    assert_eq!(written(&open), b"one\rtwoone\rtwo");
}

#[gpui::test]
async fn a_program_with_the_mouse_gets_the_right_click_and_no_menu(cx: &mut TestAppContext) {
    let mut open = printed(cx, "hello").await;
    set_clipboard(&mut open, "pasted");
    // With no program holding the mouse, there is a menu.
    let on_the_text = at(&mut open, 1.5, 0.5);
    right_click(&mut open, on_the_text);
    menu_keys(&mut open, "down down enter");
    open.until("pasted", |terminals, pane| {
        !terminals.of("write").is_empty() && !pane.writing()
    })
    .await;
    assert_eq!(written(&open), b"pasted");

    let modes = "\x1b[?1000h\x1b[?1006h";
    open.push_row(json!({"seq": 5 + modes.len(), "prevSeq": 5, "data": STANDARD.encode(modes)}));
    let focus = open.pane.read_with(open.cx, |pane, _| pane.focus.clone());
    open.cx.update(|window, cx| window.focus(&focus, cx));
    let before = written(&open).len();
    right_click(&mut open, on_the_text);
    // No menu took these: they reach the program as keys.
    menu_keys(&mut open, "down enter");
    open.until("the program heard", |terminals, pane| {
        terminals.of("write").len() >= 2 && !pane.writing()
    })
    .await;
    assert_eq!(
        &written(&open)[before..],
        b"\x1b[<2;2;1M\x1b[<2;2;1m\x1b[B\r"
    );
}
