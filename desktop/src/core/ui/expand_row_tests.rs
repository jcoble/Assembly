//! Tests for growing a row in place (docs/behavior/g0-shape.md).

use gpui::TestAppContext;
use gpui_kit::{
    AnyElement, Bounds, Entity, InteractiveElement as _, IntoElement, Modifiers,
    ParentElement as _, Pixels, Styled as _, VisualTestContext, div, px,
};

use super::expand_row::{ExpandRow, GROW};

/// A row with a 28 px title and a 60 px body, drawn in a window.
fn row(open: bool, cx: &mut TestAppContext) -> (Entity<ExpandRow>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let (row, cx) = cx.add_window_view(|_, _| {
        ExpandRow::new(open, |_, _| line("title", 28.), |_, _| line("body", 60.))
    });
    cx.run_until_parked();
    (row, cx)
}

fn line(name: &'static str, height: f32) -> AnyElement {
    div()
        .debug_selector(move || name.into())
        .h(px(height))
        .child(name)
        .into_any_element()
}

fn drawn(name: &'static str, cx: &mut VisualTestContext) -> Option<Bounds<Pixels>> {
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.debug_bounds(name)
}

fn click_title(cx: &mut VisualTestContext) {
    let title = drawn("title", cx).expect("the title is drawn");
    cx.simulate_click(title.center(), Modifiers::none());
    cx.run_until_parked();
}

fn finish_motion(cx: &mut VisualTestContext) {
    cx.executor().advance_clock(GROW);
    cx.run_until_parked();
}

// Pins: g0-shape#10
#[gpui::test]
fn a_click_opens_the_row_and_another_closes_it_after_its_motion(cx: &mut TestAppContext) {
    let (row, cx) = row(false, cx);
    assert!(drawn("body", cx).is_none(), "a shut row builds no body");
    click_title(cx);
    assert!(cx.update(|_, cx| row.read(cx).is_open()));
    finish_motion(cx);
    assert!(drawn("body", cx).is_some());
    click_title(cx);
    assert!(!cx.update(|_, cx| row.read(cx).is_open()));
    assert!(
        drawn("body", cx).is_some(),
        "the body stays while the row shrinks"
    );
    finish_motion(cx);
    assert!(drawn("body", cx).is_none());
}

// Pins: g0-shape#10
#[gpui::test]
fn the_title_stays_put_while_the_row_grows(cx: &mut TestAppContext) {
    let (_, cx) = row(false, cx);
    let before = drawn("title", cx);
    click_title(cx);
    assert_eq!(drawn("title", cx), before);
    finish_motion(cx);
    assert_eq!(drawn("title", cx), before);
}

// Pins: g0-shape#10
#[gpui::test]
fn a_row_open_at_first_render_shows_its_body_under_the_title(cx: &mut TestAppContext) {
    let (_, cx) = row(true, cx);
    let title = drawn("title", cx).expect("the title is drawn");
    let body = drawn("body", cx).expect("an open row draws its body without a click");
    assert_eq!(body.top(), title.bottom());
}
