//! Single-open ownership and real closing motion, using the existing ExpandRow.
use super::row_drop_down::RowDropDowns;
use crate::core::theme::grow::GROW;
use gpui::TestAppContext;
use gpui_kit::component::v_flex;
use gpui_kit::*;
use std::cell::{Cell, RefCell};
use std::rc::Rc;

struct Host {
    drops: Entity<RowDropDowns>,
    _heard: Subscription,
}
impl Render for Host {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let drops = self.drops.read(cx);
        v_flex().children(
            ["first", "second"]
                .into_iter()
                .filter_map(|key| drops.expand_row(&key.into())),
        )
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
    (drops, cx)
}
fn draw(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
}
fn body(_: &mut Window, _: &mut App) -> AnyElement {
    div()
        .h(px(60.))
        .child("Recorded activity")
        .into_any_element()
}

#[gpui::test]
fn opening_another_shuts_the_first(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    drops.update(cx, |drops, cx| drops.toggle("first".into(), body, cx));
    draw(cx);
    cx.executor().advance_clock(GROW);
    draw(cx);
    drops.update(cx, |drops, cx| drops.toggle("second".into(), body, cx));
    drops.read_with(cx, |drops, _| {
        assert!(!drops.is_open(&"first".into()));
        assert!(drops.is_open(&"second".into()));
        assert!(
            drops.expand_row(&"first".into()).is_some(),
            "closing body stays through its motion"
        );
    });
    draw(cx);
    cx.executor().advance_clock(GROW);
    draw(cx);
    drops.read_with(cx, |drops, _| {
        assert!(drops.expand_row(&"first".into()).is_none())
    });
}

#[gpui::test]
fn the_body_is_built_only_while_open_and_dropped_when_shut(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    let builds = Rc::new(Cell::new(0));
    let count = builds.clone();
    let resource = Rc::new(());
    let weak = Rc::downgrade(&resource);
    draw(cx);
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
    draw(cx);
    cx.executor().advance_clock(GROW);
    draw(cx);
    assert!(builds.get() > 0);
    drops.update(cx, |drops, cx| drops.shut(cx));
    assert!(weak.upgrade().is_some());
    draw(cx);
    cx.executor().advance_clock(GROW);
    draw(cx);
    // Height(0) invalidates the owner; draw that completed owner state too.
    draw(cx);
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
        assert!(drops.expand_row(&"first".into()).is_none());
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
fn reduced_motion_reports_each_completed_height_with_its_row_key(cx: &mut TestAppContext) {
    let (drops, cx) = open(cx);
    cx.update(|_, cx| cx.set_reduce_motion(true));
    let heights = Rc::new(RefCell::new(Vec::new()));
    let heard = heights.clone();
    let _subscription = cx.update(|_, cx| {
        cx.subscribe(&drops, move |_, event, _| {
            if let super::row_drop_down::RowDropDownEvent::Height(key, height) = event {
                heard.borrow_mut().push((key.clone(), *height));
            }
        })
    });
    drops.update(cx, |drops, cx| drops.toggle("first".into(), body, cx));
    draw(cx);
    draw(cx);
    assert_eq!(
        heights.borrow().len(),
        1,
        "no animation frames at reduced motion"
    );
    assert_eq!(heights.borrow()[0].0, "first");
    assert!(heights.borrow()[0].1 > px(0.));
    drops.update(cx, |drops, cx| drops.shut(cx));
    draw(cx);
    draw(cx);
    assert_eq!(
        heights.borrow().last(),
        Some(&(SharedString::from("first"), px(0.)))
    );
    drops.read_with(cx, |drops, _| {
        assert!(drops.expand_row(&"first".into()).is_none())
    });
}
