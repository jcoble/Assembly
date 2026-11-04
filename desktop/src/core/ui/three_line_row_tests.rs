//! The row from plain values, with no session behind it.
use std::cell::Cell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::component::ActiveTheme as _;
use gpui_kit::{
    App, Context, InteractiveElement as _, IntoElement, Modifiers, ParentElement as _, Render, Styled as _,
    Window, div, point, px,
};

use super::{RowArrow, ThreeLineRow};

struct OneRow {
    arrow: Option<RowArrow>,
    clicks: Rc<Cell<usize>>,
}

fn row(arrow: Option<RowArrow>, cx: &App) -> ThreeLineRow {
    ThreeLineRow {
        key: "task:1".into(),
        lead: div().size(px(12.)).into_any_element(),
        title: "A task".into(),
        title_color: cx.theme().foreground,
        time: None,
        arrow,
        icons: Vec::new(),
        second: "worktree · branch".into(),
        spelled_out: "Spelled out".into(),
        third: "Current step".into(),
        dot: false,
    }
}

impl Render for OneRow {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let clicks = self.clicks.clone();
        div().w(px(300.)).child(
            row(self.arrow.clone(), cx)
                .item(cx)
                .debug_selector(|| "the-row".to_string())
                .on_click(move |_, _, _| clicks.set(clicks.get() + 1)),
        )
    }
}

#[gpui::test]
fn the_arrow_and_the_row_each_do_their_own_click(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    let (clicks, toggles) = (Rc::new(Cell::new(0)), Rc::new(Cell::new(0)));
    let (seen, flipped) = (clicks.clone(), toggles.clone());
    let (_, cx) = cx.add_window_view(move |_, _| OneRow {
        arrow: Some(RowArrow {
            open: false,
            on_toggle: Rc::new(move |_| flipped.set(flipped.get() + 1)),
        }),
        clicks: seen,
    });
    cx.run_until_parked();
    let bounds = cx.debug_bounds("the-row").expect("the row is drawn");
    let top = bounds.origin.y + px(15.);
    cx.simulate_click(point(bounds.origin.x + bounds.size.width - px(22.), top), Modifiers::default());
    assert_eq!((clicks.get(), toggles.get()), (0, 1), "the arrow doesn't open the row");
    cx.simulate_click(point(bounds.origin.x + px(60.), top), Modifiers::default());
    assert_eq!((clicks.get(), toggles.get()), (1, 1), "the row doesn't press the arrow");
}
