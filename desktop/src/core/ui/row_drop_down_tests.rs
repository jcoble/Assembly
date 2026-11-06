//! Single-open ownership and real closing motion on the kit's spring.
use std::cell::Cell;
use std::rc::Rc;
use std::time::Duration;

use gpui::TestAppContext;
use gpui_kit::component::v_flex;
use gpui_kit::{
    AnyElement, App, AppContext as _, Context, Entity, InteractiveElement as _, IntoElement,
    ParentElement as _, Render, Styled as _, Subscription, VisualTestContext, Window, div, px,
};

use super::row_drop_down::{GrowingBody, RowDropDownEvent, RowDropDowns};

struct Host {
    drops: Entity<RowDropDowns>,
    _heard: Subscription,
}

impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        let drops = self.drops.clone();
        v_flex()
            .w(px(240.))
            .children(["first", "second", "uncapped"].into_iter().map(move |key| {
                let body = GrowingBody::new(key.into(), drops.clone(), px(40.));
                div()
                    .debug_selector(move || format!("grown-{key}"))
                    .child(if key == "uncapped" {
                        body.max_rows(None)
                    } else {
                        body
                    })
            }))
    }
}

fn open(cx: &mut TestAppContext) -> (Entity<RowDropDowns>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let (host, cx) = cx.add_window_view(|_, cx| {
        let drops = cx.new(|_| RowDropDowns::default());
        let heard = cx.observe(&drops, |_, _, cx| cx.notify());
        Host {
            drops,
            _heard: heard,
        }
    });
    let drops = cx.update(|_, cx| host.read(cx).drops.clone());
    draw(cx);
    (drops, cx)
}

fn draw(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.run_until_parked();
}

/// Runs the spring to rest.
fn settle(cx: &mut VisualTestContext) {
    for _ in 0..100 {
        cx.executor().advance_clock(Duration::from_millis(16));
        draw(cx);
    }
}

fn height(key: &str, cx: &mut VisualTestContext) -> gpui_kit::Pixels {
    cx.debug_bounds(Box::leak(format!("grown-{key}").into_boxed_str()))
        .expect("the row is drawn")
        .size
        .height
}

fn body(_: &mut Window, _: &mut App) -> AnyElement {
    div()
        .h(px(60.))
        .child("Recorded activity")
        .into_any_element()
}

#[gpui::test]
fn a_row_grows_from_nothing_to_its_body_over_several_frames(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    assert_eq!(height("first", cx), px(0.));
    drops.update(cx, |drops, cx| drops.toggle("first".into(), body, cx));
    draw(cx);
    cx.executor().advance_clock(Duration::from_millis(48));
    draw(cx);
    let moving = height("first", cx);
    assert!(moving > px(0.) && moving < px(68.), "partway: {moving:?}");
    settle(cx);
    assert!(height("first", cx) >= px(60.), "{:?}", height("first", cx));
}

#[gpui::test]
fn a_tall_body_stops_at_three_rows_and_a_short_one_fills_a_row(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    drops.update(cx, |drops, cx| {
        drops.toggle(
            "first".into(),
            |_, _| div().h(px(900.)).into_any_element(),
            cx,
        )
    });
    settle(cx);
    assert_eq!(height("first", cx), px(120.));
    drops.update(cx, |drops, cx| {
        drops.toggle(
            "second".into(),
            |_, _| div().h(px(4.)).into_any_element(),
            cx,
        )
    });
    settle(cx);
    assert_eq!(height("second", cx), px(40.));
}

#[gpui::test]
fn a_caller_can_let_a_body_be_as_tall_as_it_needs(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    drops.update(cx, |drops, cx| {
        drops.toggle(
            "uncapped".into(),
            |_, _| div().h(px(480.)).into_any_element(),
            cx,
        )
    });
    settle(cx);
    assert!(
        height("uncapped", cx) >= px(480.),
        "{:?}",
        height("uncapped", cx)
    );
}

/// An answered question drops from its whole form to one line while its row stays open.
#[gpui::test]
fn an_open_body_that_changes_height_tells_its_list(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    let tall = Rc::new(Cell::new(px(100.)));
    let body_height = tall.clone();
    drops.update(cx, |drops, cx| {
        drops.toggle(
            "first".into(),
            move |_, _| div().h(body_height.get()).into_any_element(),
            cx,
        )
    });
    settle(cx);
    let moves = Rc::new(Cell::new(0));
    let seen = moves.clone();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&drops, move |_, event, _| {
            if matches!(event, RowDropDownEvent::Moved(_)) {
                seen.set(seen.get() + 1);
            }
        })
    });
    let before = height("first", cx);
    tall.set(px(10.));
    drops.update(cx, |_, cx| cx.notify());
    settle(cx);
    assert!(
        height("first", cx) < before,
        "{before:?} to {:?}",
        height("first", cx)
    );
    assert_eq!(
        moves.get(),
        1,
        "the list has to hear about it once, then rest"
    );
}

#[gpui::test]
fn opening_another_shuts_the_first(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    drops.update(cx, |drops, cx| drops.toggle("first".into(), body, cx));
    settle(cx);
    drops.update(cx, |drops, cx| drops.toggle("second".into(), body, cx));
    drops.read_with(cx, |drops, _| {
        assert!(!drops.is_open(&"first".into()));
        assert!(drops.is_open(&"second".into()));
        assert!(
            drops.is_showing(&"first".into()),
            "closing body stays through its motion"
        );
    });
    settle(cx);
    drops.read_with(cx, |drops, _| assert!(!drops.is_showing(&"first".into())));
}

#[gpui::test]
fn the_body_is_built_only_while_open_and_dropped_when_shut(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    let builds = Rc::new(Cell::new(0));
    let count = builds.clone();
    let resource = Rc::new(());
    let weak = Rc::downgrade(&resource);
    assert_eq!(builds.get(), 0);
    drops.update(cx, |drops, cx| {
        drops.toggle(
            "first".into(),
            move |window, cx| {
                let _ = &resource;
                count.set(count.get() + 1);
                body(window, cx)
            },
            cx,
        )
    });
    settle(cx);
    assert!(builds.get() > 0);
    drops.update(cx, |drops, cx| drops.shut(cx));
    assert!(weak.upgrade().is_some());
    settle(cx);
    assert!(
        weak.upgrade().is_none(),
        "the closing row releases its body and owned watches"
    );
    let finished = builds.get();
    draw(cx);
    assert_eq!(builds.get(), finished);
}

#[gpui::test]
fn closing_a_row_before_it_was_drawn_releases_it_immediately(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    drops.update(cx, |drops, cx| {
        drops.toggle("first".into(), body, cx);
        drops.shut(cx);
        assert!(!drops.is_showing(&"first".into()));
    });
}

#[gpui::test]
fn right_opens_left_and_escape_shut(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    drops.update(cx, |drops, cx| {
        assert!(drops.handle_key("first".into(), "right", body, cx));
        assert!(drops.is_open(&"first".into()));
        assert!(drops.handle_key("first".into(), "right", body, cx));
        assert!(
            drops.is_open(&"first".into()),
            "Right never toggles an open row shut"
        );
        assert!(drops.handle_key("first".into(), "left", body, cx));
        assert!(!drops.is_open(&"first".into()));
        drops.handle_key("second".into(), "right", body, cx);
        assert!(drops.handle_key("second".into(), "escape", body, cx));
        assert_eq!(drops.open_key(), None);
        assert!(!drops.handle_key("first".into(), "enter", body, cx));
    });
}

#[gpui::test]
fn a_moving_row_tells_its_list_and_a_resting_one_stays_quiet(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    let moves = Rc::new(Cell::new(0));
    let seen = moves.clone();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&drops, move |_, event, _| {
            if matches!(event, RowDropDownEvent::Moved(_)) {
                seen.set(seen.get() + 1);
            }
        })
    });
    drops.update(cx, |drops, cx| drops.toggle("first".into(), body, cx));
    settle(cx);
    let after_opening = moves.get();
    assert!(after_opening > 0, "the list never heard the row move");
    settle(cx);
    assert_eq!(moves.get(), after_opening, "a row at rest kept reporting");
}

#[gpui::test]
fn reduced_motion_opens_and_shuts_at_once(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    cx.update(|_, cx| cx.set_reduce_motion(true));
    drops.update(cx, |drops, cx| drops.toggle("first".into(), body, cx));
    draw(cx);
    draw(cx);
    assert!(height("first", cx) >= px(60.));
    drops.update(cx, |drops, cx| drops.shut(cx));
    draw(cx);
    draw(cx);
    drops.read_with(cx, |drops, _| assert!(!drops.is_showing(&"first".into())));
}

#[gpui::test]
fn removing_a_filtered_row_drops_its_body_without_waiting_for_an_invisible_close(
    cx: &mut TestAppContext,
) {
    let (drops, cx) = open(cx);
    let resource = Rc::new(());
    let weak = Rc::downgrade(&resource);
    drops.update(cx, |drops, cx| {
        drops.toggle(
            "first".into(),
            move |window, cx| {
                let _ = &resource;
                body(window, cx)
            },
            cx,
        )
    });
    settle(cx);
    drops.update(cx, |drops, cx| {
        drops.retain_keys(|key| key.as_ref() == "second", cx)
    });
    assert!(
        weak.upgrade().is_none(),
        "filtered rows release their owned tasks even when never painted again"
    );
    drops.read_with(cx, |drops, _| {
        assert_eq!(drops.open_key(), None);
        assert!(!drops.is_showing(&"first".into()));
    });
}

#[gpui::test]
fn retaining_the_same_visible_rows_does_not_schedule_another_list_redraw(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    let notices = Rc::new(Cell::new(0));
    let seen = notices.clone();
    let _observer = cx.update(|_, cx| cx.observe(&drops, move |_, _| seen.set(seen.get() + 1)));
    drops.update(cx, |drops, cx| drops.retain_keys(|_| true, cx));
    cx.run_until_parked();
    assert_eq!(
        notices.get(),
        0,
        "rendering an unchanged virtual list must become idle"
    );
}
