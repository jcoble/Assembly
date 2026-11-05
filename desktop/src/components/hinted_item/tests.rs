//! A hinted menu row shows its hint while the pointer rests on it over the open menu, runs its click, and when it
//! can't run shows why instead and ignores the click.
use std::cell::Cell;
use std::rc::Rc;
use std::time::Duration;

use gpui::TestAppContext;
use gpui_kit::component::menu::ContextMenuExt as _;
use gpui_kit::{
    Context, IntoElement, Modifiers, MouseButton, MouseDownEvent, MouseUpEvent, Pixels, Point,
    Render, VisualTestContext, Window, div, prelude::*,
};

use super::HintedItem;

const HINT: &str = "copy-hint: Puts the link on the clipboard";
const REASON: &str = "copy-hint: Nothing to copy yet";

struct Host {
    clicks: Rc<Cell<u32>>,
    hint: bool,
    reason: Option<&'static str>,
}

impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        let (clicks, hint, reason) = (self.clicks.clone(), self.hint, self.reason);
        div()
            .id("target")
            .size_full()
            .debug_selector(|| "target".into())
            .context_menu(move |menu, _, _| {
                let clicks = clicks.clone();
                let item = HintedItem::new("copy", "Copy")
                    .disabled_reason(reason)
                    .on_click(move |_, _| clicks.set(clicks.get() + 1));
                menu.item(if hint {
                    item.hint("Puts the link on the clipboard")
                } else {
                    item
                })
            })
    }
}

fn settle(cx: &mut VisualTestContext) {
    for _ in 0..3 {
        cx.run_until_parked();
        cx.update(|window, cx| window.draw(cx).clear(cx));
    }
    cx.run_until_parked();
}

fn click(at: Point<Pixels>, button: MouseButton, cx: &mut VisualTestContext) {
    let modifiers = Modifiers::default();
    cx.simulate_mouse_move(at, None, modifiers);
    cx.simulate_event(MouseDownEvent {
        position: at,
        button,
        modifiers,
        click_count: 1,
        first_mouse: false,
    });
    cx.simulate_event(MouseUpEvent {
        position: at,
        button,
        modifiers,
        click_count: 1,
    });
    settle(cx);
}

/// Opens the menu and rests the pointer on the row past the tooltip delay; returns the row's centre.
fn open_and_rest<'a>(
    hint: bool,
    reason: Option<&'static str>,
    cx: &'a mut TestAppContext,
) -> (Rc<Cell<u32>>, Point<Pixels>, &'a mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let clicks = Rc::new(Cell::new(0));
    let host = Host {
        clicks: clicks.clone(),
        hint,
        reason,
    };
    let (_, cx) = cx.add_window_view(|_, _| host);
    cx.update(|window, _| window.activate_window());
    settle(cx);
    let target = cx.debug_bounds("target").expect("the target").center();
    click(target, MouseButton::Right, cx);
    let row = cx
        .debug_bounds("copy")
        .expect("the open menu's row")
        .center();
    cx.simulate_mouse_move(row, None, Modifiers::default());
    settle(cx);
    cx.executor().advance_clock(Duration::from_millis(600));
    settle(cx);
    (clicks, row, cx)
}

#[gpui::test]
fn the_hint_shows_over_the_open_menu_while_the_pointer_rests_on_the_row(cx: &mut TestAppContext) {
    let (_, _, cx) = open_and_rest(true, None, cx);
    assert!(cx.debug_bounds(HINT).is_some(), "the hint is drawn");
    assert!(cx.debug_bounds("copy").is_some(), "the menu is still open");
}

#[gpui::test]
fn the_hint_waits_for_the_pointer(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    let host = Host {
        clicks: Rc::new(Cell::new(0)),
        hint: true,
        reason: None,
    };
    let (_, cx) = cx.add_window_view(|_, _| host);
    cx.update(|window, _| window.activate_window());
    settle(cx);
    let target = cx.debug_bounds("target").expect("the target").center();
    click(target, MouseButton::Right, cx);
    assert!(cx.debug_bounds("copy").is_some(), "the menu is open");
    cx.executor().advance_clock(Duration::from_millis(600));
    settle(cx);
    assert!(
        cx.debug_bounds(HINT).is_none(),
        "no hint until the pointer is on the row"
    );
}

#[gpui::test]
fn a_row_without_a_hint_shows_none(cx: &mut TestAppContext) {
    let (_, _, cx) = open_and_rest(false, None, cx);
    assert!(cx.debug_bounds(HINT).is_none());
    assert!(cx.debug_bounds(REASON).is_none());
}

#[gpui::test]
fn clicking_the_row_runs_it(cx: &mut TestAppContext) {
    let (clicks, row, cx) = open_and_rest(true, None, cx);
    click(row, MouseButton::Left, cx);
    assert_eq!(clicks.get(), 1);
}

#[gpui::test]
fn a_row_that_cant_run_says_why_instead_of_its_hint(cx: &mut TestAppContext) {
    let (_, _, cx) = open_and_rest(true, Some("Nothing to copy yet"), cx);
    assert!(cx.debug_bounds(REASON).is_some(), "the reason is drawn");
    assert!(cx.debug_bounds(HINT).is_none(), "the hint is not");
}

#[gpui::test]
fn a_row_that_cant_run_ignores_its_click(cx: &mut TestAppContext) {
    let (clicks, row, cx) = open_and_rest(false, Some("Nothing to copy yet"), cx);
    click(row, MouseButton::Left, cx);
    assert_eq!(clicks.get(), 0);
}
