//! The cursor blinks only while its window is active: GPUI blurs the pane when another window takes over, which leaves
//! the cursor drawn steady with no timer, and it blinks again once its window is active.

use gpui::TestAppContext;

use super::{Open, Terminals, following, open};

fn blinking(open: &mut Open<'_>) -> bool {
    (open.pane).read_with(open.cx, |pane, _| pane._blink.is_some())
}

fn cursor_on(open: &mut Open<'_>) -> bool {
    (open.pane).read_with(open.cx, |pane, _| pane.cursor_on)
}

fn pane_focused(open: &mut Open<'_>) -> bool {
    let pane = open.pane.clone();
    open.cx
        .update(|window, cx| pane.read(cx).focus.is_focused(window))
}

/// Whether the window's last frame drew the cursor's cell, not merely whether the pane means to.
fn cursor_drawn(open: &mut Open<'_>) -> bool {
    open.cx.debug_bounds("terminal-cursor").is_some()
}

#[gpui::test]
async fn the_cursor_stays_steady_while_another_window_is_active(cx: &mut TestAppContext) {
    cx.update(crate::core::state::preferences::Preferences::init);
    let mut open = open(cx, Terminals::default(), false).await;
    open.cx.update(|window, _| window.activate_window());
    open.until("the shell started", |_, pane| following(pane))
        .await;
    open.cx.run_until_parked();
    assert!(blinking(&mut open), "it blinks while active and focused");
    open.advance(530);
    assert!(!cursor_on(&mut open), "mid-blink, the cursor is hidden");
    assert!(!cursor_drawn(&mut open), "and the frame shows it hidden");

    open.cx.deactivate_window();
    assert!(
        cursor_drawn(&mut open),
        "the window drew the steady cursor, not a frame from mid-blink"
    );
    assert!(pane_focused(&mut open), "the pane keeps its focus");
    assert!(
        !blinking(&mut open),
        "a timer ran while the window was inactive"
    );
    assert!(cursor_on(&mut open), "the cursor shows steady at once");
    for _ in 0..3 {
        open.advance(530);
        assert!(cursor_on(&mut open), "the cursor blinked while inactive");
    }

    open.cx.update(|window, _| window.activate_window());
    open.cx.run_until_parked();
    assert!(
        blinking(&mut open),
        "it blinks again once the window is active"
    );
    open.advance(530);
    assert!(!cursor_on(&mut open), "the blink resumed");
}

#[gpui::test]
async fn the_steady_cursor_is_drawn_when_focus_moves_within_the_window(cx: &mut TestAppContext) {
    cx.update(crate::core::state::preferences::Preferences::init);
    let mut open = open(cx, Terminals::default(), false).await;
    open.cx.update(|window, _| window.activate_window());
    open.until("the shell started", |_, pane| following(pane))
        .await;
    open.cx.run_until_parked();
    open.advance(530);
    assert!(!cursor_drawn(&mut open), "mid-blink, the frame hides it");

    // Another focusable thing in the same window, as the "More panels" popup is.
    let elsewhere = open.cx.update(|_, cx| cx.focus_handle());
    open.cx.update(|window, cx| window.focus(&elsewhere, cx));
    open.cx.run_until_parked();
    assert!(!pane_focused(&mut open), "the pane lost the focus");
    assert!(!blinking(&mut open), "a timer ran without the focus");
    assert!(
        cursor_drawn(&mut open),
        "the window drew the steady cursor, not a frame from mid-blink"
    );
    open.advance(530 * 3);
    assert!(cursor_drawn(&mut open), "it stays drawn");
}
