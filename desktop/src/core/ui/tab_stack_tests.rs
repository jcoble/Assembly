use std::cell::Cell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::{
    Context, Element as _, InteractiveElement as _, IntoElement, Modifiers, ParentElement as _,
    Render, StatefulInteractiveElement as _, Styled as _, Window, div, point, px,
};

use super::tab_stack;

struct Tabs {
    selected: usize,
    clicks: Rc<Cell<[usize; 2]>>,
}

impl Render for Tabs {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        let tab = |at: usize, height: f32, clicks: Rc<Cell<[usize; 2]>>| {
            div()
                .id(at)
                .h(px(height))
                .w_full()
                .on_click(move |_, _, _| {
                    let mut seen = clicks.get();
                    seen[at] += 1;
                    clicks.set(seen);
                })
                .into_any()
        };
        div().w(px(200.)).child(
            tab_stack(self.selected, [tab(0, 30., self.clicks.clone()), tab(1, 100., self.clicks.clone())])
                .debug_selector(|| "stack".to_string()),
        )
    }
}

#[gpui::test]
fn both_tabs_take_the_tallest_height_and_only_the_selected_one_clicks(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    let clicks = Rc::new(Cell::new([0, 0]));
    let seen = clicks.clone();
    let (tabs, cx) = cx.add_window_view(move |_, _| Tabs { selected: 0, clicks: seen });
    cx.run_until_parked();
    let shown = cx.debug_bounds("stack").expect("the stack is drawn");
    assert_eq!(shown.size.height, px(100.), "the short tab is selected, the stack keeps the tall one's height");
    let inside = point(shown.origin.x + px(20.), shown.origin.y + px(10.));
    cx.simulate_click(inside, Modifiers::default());
    assert_eq!(clicks.get(), [1, 0], "the hidden tab, on top of it, doesn't take the click");
    tabs.update(cx, |tabs, cx| {
        tabs.selected = 1;
        cx.notify();
    });
    cx.run_until_parked();
    assert_eq!(cx.debug_bounds("stack").expect("the stack is drawn").size.height, px(100.));
    cx.simulate_click(inside, Modifiers::default());
    assert_eq!(clicks.get(), [1, 1]);
}
