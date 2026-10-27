//! A theme switch at runtime redraws the window. `set` relies on the kit for that: every `Theme::update` ends in
//! `cx.refresh_windows()` (gpui-component theme/mod.rs:334), so this guards against a kit bump changing it. Its own
//! file because it draws a view, and apply.rs's tests read files.

use std::{cell::Cell, rc::Rc};

use gpui_kit::{Context, IntoElement, Render, TestAppContext, Window, div, gpui};

use super::theme_id::ThemeId;
use super::{THEMES_DIR, init, set};

struct CountsRenders(Rc<Cell<u32>>);

impl Render for CountsRenders {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        self.0.set(self.0.get() + 1);
        div()
    }
}

#[gpui::test]
fn a_switch_at_runtime_redraws_the_window(cx: &mut TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        init(THEMES_DIR.into(), cx, |_| {});
    });
    cx.run_until_parked();
    let renders = Rc::new(Cell::new(0));
    let counter = renders.clone();
    let (_, window) = cx.add_window_view(|_, _| CountsRenders(counter));
    window.update(|window, cx| set(ThemeId::Assembly, window, cx));
    window.run_until_parked();
    let before = renders.get();
    window.update(|window, cx| set(ThemeId::Houston, window, cx));
    window.run_until_parked();
    assert!(
        renders.get() > before,
        "the switch didn't redraw the window"
    );
}
