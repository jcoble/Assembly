//! Tests for growing a row in place (docs/behavior/g0-shape.md).

use std::cell::{Cell, RefCell};
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::base::animation::cubic_bezier;
use gpui_kit::component::v_flex;
use gpui_kit::{
    AnyElement, AppContext as _, Bounds, Context, Entity, InteractiveElement as _, IntoElement,
    Modifiers, ParentElement as _, Pixels, Render, Styled as _, Subscription, VisualTestContext,
    Window, div, px,
};

use super::expand_row::{ExpandRow, ExpandRowEvent};
use crate::core::theme::grow::{GROW, faded};

/// A row with a 28 px title and a 60 px body, drawn in a window.
fn row(open: bool, cx: &mut TestAppContext) -> (Entity<ExpandRow>, &mut VisualTestContext) {
    hosted(cx, move || {
        ExpandRow::new(open, |_, _, _| line("title", 28.), |_, _| line("body", 60.))
    })
}

/// Draws the row at the top of a window, as tall as it is, as a list would.
struct Host(Entity<ExpandRow>);

impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        v_flex().size_full().child(self.0.clone())
    }
}

fn hosted(
    cx: &mut TestAppContext,
    build: impl FnOnce() -> ExpandRow + 'static,
) -> (Entity<ExpandRow>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let (host, cx) = cx.add_window_view(|_, cx| Host(cx.new(|_| build())));
    cx.run_until_parked();
    let row = cx.update(|_, cx| host.read(cx).0.clone());
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
    let (row, cx) = row(true, cx);
    let title = drawn("title", cx).expect("the title is drawn");
    let body = drawn("body", cx).expect("an open row draws its body without a click");
    assert_eq!(body.top(), title.bottom());
    let now = cx.executor().now();
    assert_eq!(
        cx.update(|_, cx| row.read(cx).openness(now)),
        1.,
        "fully open, with no motion"
    );
}

// Pins: g0-shape#10
#[gpui::test]
fn a_click_during_the_motion_turns_the_row_back_from_where_it_is(cx: &mut TestAppContext) {
    let (row, cx) = row(false, cx);
    click_title(cx);
    cx.executor().advance_clock(GROW / 2);
    let openness = |cx: &mut VisualTestContext| {
        let now = cx.executor().now();
        cx.update(|_, cx| row.read(cx).openness(now))
    };
    let halfway = openness(cx);
    assert!(halfway > 0.4 && halfway < 0.6, "half open: {halfway}");
    click_title(cx);
    assert!((openness(cx) - halfway).abs() < 0.01, "no jump");
    // Back to shut in the time it took to open this far.
    cx.executor().advance_clock(GROW / 2);
    assert_eq!(openness(cx), 0.);
}

// Pins: g0-shape#10
#[test]
fn the_body_fades_with_the_height_from_40_ms_in() {
    let grown_at = |ms: f32| cubic_bezier(0.16, 1., 0.3, 1.)(ms / 160.);
    assert_eq!(faded(grown_at(20.)), 0.);
    assert!(faded(grown_at(40.)).abs() < 0.001);
    // Following the height, it's further in than halfway at 100 ms, where a fade timed from 40 ms to 160 ms is.
    let mid = faded(grown_at(100.));
    assert!(mid > 0.5 && mid < 1., "fading in with the height: {mid}");
    assert_eq!(faded(1.), 1.);
}

/// Everything the row told its owner, in order.
fn heard(
    row: &Entity<ExpandRow>,
    cx: &mut VisualTestContext,
) -> (Rc<RefCell<Vec<ExpandRowEvent>>>, Subscription) {
    let events = Rc::new(RefCell::new(Vec::new()));
    let sink = events.clone();
    let subscription = cx.update(|_, cx| {
        cx.subscribe(row, move |_, event: &ExpandRowEvent, _| {
            sink.borrow_mut().push(event.clone())
        })
    });
    (events, subscription)
}

fn heights(events: &RefCell<Vec<ExpandRowEvent>>) -> Vec<Pixels> {
    events
        .borrow()
        .iter()
        .filter_map(|event| match event {
            ExpandRowEvent::Height(height) => Some(*height),
            _ => None,
        })
        .collect()
}

// Pins: g0-shape#10
#[gpui::test]
fn the_owner_hears_the_height_each_frame_while_it_grows(cx: &mut TestAppContext) {
    let (row, cx) = row(false, cx);
    let (events, _subscription) = heard(&row, cx);
    click_title(cx);
    for _ in 0..4 {
        cx.executor().advance_clock(GROW / 4);
        drawn("title", cx);
    }
    let heights = heights(&events);
    // The first frame measures the body, so each later one grows it.
    assert!(heights.len() >= 3, "a height each frame: {heights:?}");
    assert!(
        heights.windows(2).all(|pair| pair[0] < pair[1]),
        "growing: {heights:?}"
    );
    assert_eq!(heights.last(), Some(&px(88.)), "title and body");
}

// Pins: g0-shape#10
#[gpui::test]
fn a_click_tells_the_owner_and_set_open_does_not(cx: &mut TestAppContext) {
    let (row, cx) = row(false, cx);
    let (events, _subscription) = heard(&row, cx);
    let toggles = |events: &RefCell<Vec<ExpandRowEvent>>| {
        events
            .borrow()
            .iter()
            .filter(|event| !matches!(event, ExpandRowEvent::Height(_)))
            .cloned()
            .collect::<Vec<_>>()
    };
    click_title(cx);
    assert_eq!(toggles(&events), [ExpandRowEvent::Opened]);
    finish_motion(cx);
    cx.update(|_, cx| row.update(cx, |row, cx| row.set_open(false, false, cx)));
    assert!(drawn("body", cx).is_none(), "shut at once, without motion");
    assert_eq!(toggles(&events), [ExpandRowEvent::Opened]);
}

// Pins: g0-shape#10
#[gpui::test]
fn set_open_with_motion_grows_like_a_click(cx: &mut TestAppContext) {
    let (row, cx) = row(false, cx);
    cx.update(|_, cx| row.update(cx, |row, cx| row.set_open(true, true, cx)));
    cx.executor().advance_clock(GROW / 2);
    let now = cx.executor().now();
    let halfway = cx.update(|_, cx| row.read(cx).openness(now));
    assert!(halfway > 0.4 && halfway < 0.6, "half open: {halfway}");
}

// Pins: g0-shape#10
#[gpui::test]
fn a_body_that_grows_while_open_grows_into_its_new_height(cx: &mut TestAppContext) {
    let tall = Rc::new(Cell::new(60.));
    let body = tall.clone();
    let (row, cx) = hosted(cx, move || {
        ExpandRow::new(
            true,
            |_, _, _| line("title", 28.),
            move |_, _| line("body", body.get()),
        )
    });
    let (events, _subscription) = heard(&row, cx);
    drawn("title", cx);
    tall.set(100.);
    cx.update(|_, cx| row.update(cx, |_, cx| cx.notify()));
    drawn("title", cx);
    assert!(heights(&events).is_empty(), "not at its new height at once");
    cx.executor().advance_clock(GROW / 2);
    drawn("title", cx);
    let midway = *heights(&events).last().expect("a height");
    assert!(midway > px(88.) && midway < px(128.), "growing: {midway:?}");
    finish_motion(cx);
    drawn("title", cx);
    assert_eq!(heights(&events).last(), Some(&px(128.)));
}

// Pins: g0-shape#10
#[gpui::test]
fn set_body_replaces_what_the_row_reveals(cx: &mut TestAppContext) {
    let (row, cx) = row(true, cx);
    cx.update(|_, cx| row.update(cx, |row, _| row.set_body(|_, _| line("other", 40.))));
    assert!(drawn("body", cx).is_none());
    assert!(drawn("other", cx).is_some());
}

// Pins: g0-shape#10
#[gpui::test]
fn the_band_is_the_callers(cx: &mut TestAppContext) {
    let grown = Rc::new(RefCell::new(Vec::new()));
    let sink = grown.clone();
    let (_, cx) = hosted(cx, move || {
        ExpandRow::new(
            false,
            |_, _, _| line("title", 28.),
            |_, _| line("body", 60.),
        )
        .band(move |row, grown, _| {
            sink.borrow_mut().push(grown);
            row
        })
    });
    cx.run_until_parked();
    drawn("title", cx);
    assert_eq!(grown.borrow().last(), Some(&0.), "shut");
    click_title(cx);
    finish_motion(cx);
    drawn("title", cx);
    assert_eq!(grown.borrow().last(), Some(&1.), "open");
}

/// A row open at first render whose body's height the test sets.
fn sized_row(
    cx: &mut TestAppContext,
) -> (Entity<ExpandRow>, Rc<Cell<f32>>, &mut VisualTestContext) {
    let tall = Rc::new(Cell::new(60.));
    let body = tall.clone();
    let (row, cx) = hosted(cx, move || {
        ExpandRow::new(
            true,
            |_, _, _| line("title", 28.),
            move |_, _| line("body", body.get()),
        )
    });
    (row, tall, cx)
}

/// The row's height on the next frame, with no time passing.
fn height_now(row: &Entity<ExpandRow>, cx: &mut VisualTestContext) -> Pixels {
    cx.update(|_, cx| row.update(cx, |_, cx| cx.notify()));
    drawn("title", cx);
    drawn("title", cx);
    cx.update(|_, cx| row.read(cx).drawn_height())
}

// Pins: g0-shape#10
#[gpui::test]
fn shutting_a_row_while_its_body_grows_starts_from_where_it_is(cx: &mut TestAppContext) {
    let (row, tall, cx) = sized_row(cx);
    tall.set(200.);
    height_now(&row, cx);
    cx.executor().advance_clock(GROW / 8);
    let partway = height_now(&row, cx);
    assert!(
        partway > px(88.) && partway < px(228.),
        "growing: {partway:?}"
    );
    click_title(cx);
    let shutting = height_now(&row, cx);
    assert!(
        (shutting - partway).abs() < px(1.),
        "{shutting:?} from {partway:?}"
    );
}

// Pins: g0-shape#10
#[gpui::test]
fn a_body_that_changes_while_the_row_opens_does_not_jump(cx: &mut TestAppContext) {
    let (row, tall, cx) = sized_row(cx);
    click_title(cx);
    finish_motion(cx);
    click_title(cx);
    cx.executor().advance_clock(GROW / 2);
    let partway = height_now(&row, cx);
    tall.set(100.);
    let after = height_now(&row, cx);
    assert!(
        (after - partway).abs() < px(1.),
        "{after:?} from {partway:?}"
    );
}

// Pins: g0-shape#10
#[gpui::test]
fn a_body_that_empties_while_open_shrinks_to_nothing(cx: &mut TestAppContext) {
    let (row, tall, cx) = sized_row(cx);
    tall.set(0.);
    height_now(&row, cx);
    cx.executor().advance_clock(GROW / 4);
    let partway = height_now(&row, cx);
    assert!(
        partway > px(28.) && partway < px(88.),
        "shrinking: {partway:?}"
    );
}

// Pins: g0-shape#10
#[gpui::test]
fn with_reduce_motion_a_row_switches_at_once(cx: &mut TestAppContext) {
    let (row, tall, cx) = sized_row(cx);
    cx.update(|_, cx| cx.set_reduce_motion(true));
    tall.set(100.);
    assert_eq!(height_now(&row, cx), px(128.), "a new body height at once");
    click_title(cx);
    assert!(drawn("body", cx).is_none(), "shut at once");
}

// Pins: g0-shape#10
#[gpui::test]
fn a_body_that_wraps_again_at_a_new_width_follows_at_once(cx: &mut TestAppContext) {
    let (row, tall, cx) = sized_row(cx);
    tall.set(100.);
    cx.simulate_resize(gpui_kit::size(px(400.), px(600.)));
    assert_eq!(height_now(&row, cx), px(128.));
}

// Pins: g0-shape#10
#[gpui::test]
fn a_body_that_fills_again_after_emptying_grows_from_nothing(cx: &mut TestAppContext) {
    let (row, tall, cx) = sized_row(cx);
    tall.set(0.);
    height_now(&row, cx);
    finish_motion(cx);
    assert_eq!(height_now(&row, cx), px(28.));
    tall.set(60.);
    assert_eq!(
        height_now(&row, cx),
        px(28.),
        "not at its new height at once"
    );
}

// Pins: g0-shape#10
#[gpui::test]
fn turning_on_reduce_motion_ends_a_motion_at_once(cx: &mut TestAppContext) {
    let (_, _, cx) = sized_row(cx);
    click_title(cx);
    cx.executor().advance_clock(GROW / 4);
    cx.update(|_, cx| cx.set_reduce_motion(true));
    assert!(drawn("body", cx).is_none(), "shut at once");
}

// Pins: g0-shape#10
#[gpui::test]
fn a_new_width_mid_resize_follows_at_once(cx: &mut TestAppContext) {
    let (row, tall, cx) = sized_row(cx);
    tall.set(100.);
    height_now(&row, cx);
    cx.executor().advance_clock(GROW / 8);
    cx.simulate_resize(gpui_kit::size(px(400.), px(600.)));
    assert_eq!(height_now(&row, cx), px(128.));
}
