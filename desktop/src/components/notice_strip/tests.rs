//! A notice strip says its words across its pane and runs its one action when that button is clicked.
use std::cell::Cell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::{
    Context, IntoElement, Modifiers, Render, VisualTestContext, Window, div, prelude::*,
};

use super::NoticeStrip;
use crate::activity::Tone;

struct Host {
    clicks: Rc<Cell<u32>>,
    action: bool,
}

impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        let strip = NoticeStrip::new("notice", "Outside the project.", Tone::Warning);
        let clicks = self.clicks.clone();
        div().size_full().child(if self.action {
            strip.action("Edit anyway", move |_, _, _| clicks.set(clicks.get() + 1))
        } else {
            strip
        })
    }
}

fn drawn(action: bool, cx: &mut TestAppContext) -> (Rc<Cell<u32>>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let clicks = Rc::new(Cell::new(0));
    let counted = clicks.clone();
    let (_, cx) = cx.add_window_view(move |_, _| Host {
        clicks: counted,
        action,
    });
    cx.update(|window, cx| window.draw(cx).clear(cx));
    (clicks, cx)
}

#[gpui::test]
fn its_action_runs_once_per_click(cx: &mut TestAppContext) {
    let (clicks, cx) = drawn(true, cx);
    assert!(cx.debug_bounds("notice").is_some(), "the strip is drawn");
    let button = cx
        .debug_bounds("notice-action")
        .expect("the strip draws its action");
    cx.simulate_click(button.center(), Modifiers::none());
    cx.run_until_parked();
    assert_eq!(clicks.get(), 1);
}

#[gpui::test]
fn a_strip_with_no_action_draws_no_button(cx: &mut TestAppContext) {
    let (_, cx) = drawn(false, cx);
    assert!(cx.debug_bounds("notice").is_some(), "the strip is drawn");
    assert!(cx.debug_bounds("notice-action").is_none());
}
