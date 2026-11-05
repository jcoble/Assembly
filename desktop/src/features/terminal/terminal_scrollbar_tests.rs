//! The scrollbar's numbers, from the old renderer (ghostty-web.js `renderScrollbar`, `handleMouseDown`), and how it
//! shows, hides and takes the mouse on the Terminal tab.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::TestAppContext;
use gpui_kit::{Modifiers, MouseButton, Pixels, Point, point, px};
use serde_json::json;

use super::mouse::{at, wheel};
use super::{Open, Terminals, following, open};
use crate::features::terminal::terminal_scrollbar::{offset_at, thumb};

fn none() -> Modifiers {
    Modifiers::none()
}

/// The tab following a shell with 200 lines printed.
async fn history<'a>(cx: &'a mut TestAppContext) -> Open<'a> {
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
    open
}

fn opacity(open: &mut Open<'_>) -> f32 {
    open.pane
        .read_with(open.cx, |pane, _| pane.scrollbar_opacity)
}

/// The grid's height, and a point on the bar's column at this height: the bar is 8 px wide and 4 px from the grid's right edge.
fn on_the_bar(open: &mut Open<'_>, y: Pixels) -> Point<Pixels> {
    open.pane.read_with(open.cx, |pane, _| {
        let right = pane.origin.x + pane.cell.width * pane.size.0 as f32;
        point(right - px(8.), pane.origin.y + y)
    })
}

fn grid_height(open: &mut Open<'_>) -> Pixels {
    (open.pane).read_with(open.cx, |pane, _| pane.cell.height * pane.size.1 as f32)
}

fn offset(open: &mut Open<'_>) -> usize {
    (open.pane).read_with(open.cx, |pane, _| pane.grid.display_offset())
}

#[test]
fn the_thumb_sits_by_offset_and_has_a_minimum() {
    let height = px(600.);
    // The track is 592 tall, inside 4 px of padding; 28 rows of 1028 would be under the 20 px minimum.
    assert_eq!(thumb(28, 1000, 0, height), (px(576.), px(20.)));
    assert_eq!(thumb(28, 1000, 500, height), (px(290.), px(20.)));
    assert_eq!(thumb(28, 1000, 1000, height), (px(4.), px(20.)));
    // 20 rows of 40 is half of a 392 px track.
    assert_eq!(thumb(20, 20, 10, px(400.)), (px(102.), px(196.)));
}

#[test]
fn a_track_position_maps_to_an_offset() {
    let height = px(600.);
    assert_eq!(offset_at(px(4.), 1000, height), 1000);
    assert_eq!(offset_at(px(300.), 1000, height), 500);
    assert_eq!(offset_at(px(596.), 1000, height), 0);
    // Past the ends of the track, it stops at them.
    assert_eq!(offset_at(px(0.), 1000, height), 1000);
    assert_eq!(offset_at(px(600.), 1000, height), 0);
}

#[gpui::test]
async fn scrolling_shows_it_and_it_fades_and_hides_after_1500_ms(cx: &mut TestAppContext) {
    let mut open = history(cx).await;
    assert_eq!(opacity(&mut open), 0.);
    wheel(&mut open, 3.);
    open.advance(200);
    assert_eq!(opacity(&mut open), 1., "faded in over 200 ms");
    open.advance(1100);
    assert_eq!(opacity(&mut open), 1., "still shown 1.3 s on");
    // Another scroll starts the wait over.
    wheel(&mut open, 3.);
    open.advance(1300);
    assert_eq!(
        opacity(&mut open),
        1.,
        "still shown 1.3 s after the last scroll"
    );
    open.advance(600);
    assert_eq!(opacity(&mut open), 0., "hidden 1.5 s after it, once faded");
}

#[gpui::test]
async fn no_scrollback_shows_no_bar(cx: &mut TestAppContext) {
    let terminals = Terminals {
        kept: b"short\r\n".to_vec(),
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    open.until("the output", |_, pane| {
        following(pane) && pane.grid.text() == "short"
    })
    .await;
    wheel(&mut open, 3.);
    open.advance(200);
    assert_eq!(opacity(&mut open), 0.);
    // With scrollback, the same wheel shows it.
    let rows = open.pane.read_with(open.cx, |pane, _| pane.size.1);
    let lines = "x\r\n".repeat(rows * 2);
    let from = 7;
    open.push_row(
        json!({"seq": from + lines.len(), "prevSeq": from, "data": STANDARD.encode(&lines)}),
    );
    wheel(&mut open, 3.);
    open.advance(200);
    assert_eq!(opacity(&mut open), 1.);
}

#[gpui::test]
async fn dragging_the_thumb_scrolls_and_keeps_it_shown(cx: &mut TestAppContext) {
    let mut open = history(cx).await;
    wheel(&mut open, 20.);
    open.advance(200);
    assert_eq!(offset(&mut open), 20);
    let (rows, history) =
        (open.pane).read_with(open.cx, |pane, _| (pane.size.1, pane.grid.history_size()));
    let height = grid_height(&mut open);
    let (top, size) = thumb(rows, history, 20, height);
    let on_the_thumb = on_the_bar(&mut open, top + size / 2.);
    open.cx
        .simulate_mouse_down(on_the_thumb, MouseButton::Left, none());
    assert!(
        open.pane
            .read_with(open.cx, |pane, _| pane.dragging_bar.is_some())
    );
    // Up the track is further back.
    let higher = on_the_thumb - point(px(0.), px(40.));
    open.cx
        .simulate_mouse_move(higher, MouseButton::Left, none());
    assert!(offset(&mut open) > 20);
    // It doesn't hide during a drag.
    open.advance(3000);
    assert_eq!(opacity(&mut open), 1.);
    open.cx.simulate_mouse_up(higher, MouseButton::Left, none());
    assert!(
        open.pane
            .read_with(open.cx, |pane, _| pane.dragging_bar.is_none())
    );
    open.advance(2000);
    assert_eq!(opacity(&mut open), 0., "it hides once the drag ends");
}

#[gpui::test]
async fn clicking_the_track_jumps_there(cx: &mut TestAppContext) {
    let mut open = history(cx).await;
    // At the bottom, the thumb is at the track's foot, so its top half is track.
    let history = open
        .pane
        .read_with(open.cx, |pane, _| pane.grid.history_size());
    assert!(history > 0);
    let height = grid_height(&mut open);
    let y = height / 4.;
    let track = on_the_bar(&mut open, y);
    open.cx
        .simulate_mouse_down(track, MouseButton::Left, none());
    open.cx.simulate_mouse_up(track, MouseButton::Left, none());
    // As ghostty-web.js `handleMouseDown`: 1 - (y - 4) / track, of the history.
    let expected = ((1. - (y - px(4.)) / (height - px(8.))) * history as f32).round() as usize;
    assert_eq!(offset(&mut open), expected);
    assert_eq!(
        open.pane
            .read_with(open.cx, |pane, _| pane.grid.selection_text()),
        None
    );
}

#[gpui::test]
async fn a_press_on_the_bar_selects_nothing_and_reaches_no_program(cx: &mut TestAppContext) {
    let mut open = history(cx).await;
    wheel(&mut open, 3.);
    open.advance(200);
    let press = on_the_bar(&mut open, px(100.));
    open.cx
        .simulate_mouse_down(press, MouseButton::Left, none());
    open.pane.read_with(open.cx, |pane, _| {
        assert!(!pane.selecting, "a press on the bar started a selection");
    });
    open.cx.simulate_mouse_up(press, MouseButton::Left, none());

    // A program takes the mouse while the bar shows: the bar's press still doesn't reach it.
    wheel(&mut open, 3.);
    let end = open
        .pane
        .read_with(open.cx, |pane, _| pane.link.as_ref().unwrap().end) as usize;
    let modes = "\x1b[?1000h\x1b[?1006h";
    open.push_row(
        json!({"seq": end + modes.len(), "prevSeq": end, "data": STANDARD.encode(modes)}),
    );
    let press = on_the_bar(&mut open, px(100.));
    open.cx
        .simulate_mouse_down(press, MouseButton::Left, none());
    open.cx.simulate_mouse_up(press, MouseButton::Left, none());
    open.cx.run_until_parked();
    assert!(open.terminals.lock().unwrap().of("write").is_empty());
    // A press elsewhere does reach it.
    let elsewhere = at(&mut open, 1.2, 1.2);
    open.cx
        .simulate_mouse_down(elsewhere, MouseButton::Left, none());
    open.until("the program heard", |terminals, pane| {
        !terminals.of("write").is_empty() && !pane.writing()
    })
    .await;
}

#[gpui::test]
async fn no_timer_runs_once_it_has_hidden_or_while_hidden(cx: &mut TestAppContext) {
    let mut open = history(cx).await;
    let running = |open: &mut Open<'_>| {
        open.pane
            .read_with(open.cx, |pane, _| pane._scrollbar.is_some())
    };
    wheel(&mut open, 3.);
    open.cx.run_until_parked();
    assert!(running(&mut open), "its timer runs while it shows");
    open.advance(2000);
    assert_eq!(opacity(&mut open), 0.);
    assert!(!running(&mut open), "its timer stopped once hidden");

    wheel(&mut open, 3.);
    open.cx.run_until_parked();
    assert!(running(&mut open));
    open.shown(false);
    assert!(!running(&mut open), "a hidden pane runs no timer");
    assert_eq!(opacity(&mut open), 0.);
}
