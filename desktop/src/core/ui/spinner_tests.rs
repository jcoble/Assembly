use std::cell::Cell;
use std::rc::Rc;
use std::time::Duration;

use gpui::TestAppContext;
use gpui_kit::{Context, IntoElement, ParentElement as _, Render, Window, div};

use super::{Spinner, stepped};

struct Counted {
    renders: Rc<Cell<u32>>,
}

impl Render for Counted {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        self.renders.set(self.renders.get() + 1);
        div().child(Spinner::new())
    }
}

fn renders_in_a_second(cx: &mut TestAppContext) -> u32 {
    cx.update(gpui_kit::init);
    let renders = Rc::new(Cell::new(0));
    let seen = renders.clone();
    let (_, cx) = cx.add_window_view(move |_, _| Counted { renders: seen });
    cx.run_until_parked();
    let before = renders.get();
    for _ in 0..1000 {
        cx.executor().advance_clock(Duration::from_millis(1));
        cx.run_until_parked();
    }
    renders.get() - before
}

/// The kit's redraws on every display frame.
#[gpui::test]
fn it_redraws_its_panel_about_ten_times_a_second(cx: &mut TestAppContext) {
    let renders = renders_in_a_second(cx);
    assert!((8..=11).contains(&renders), "{renders} redraws in a second");
}

#[test]
fn it_moves_a_whole_spoke_at_a_time() {
    assert_eq!(stepped(0.), 0.);
    assert_eq!(stepped(0.124), 0.);
    assert_eq!(stepped(0.125), 0.125);
    assert_eq!(stepped(0.99), 0.875);
}
