//! Tests for the Cmd-K palette (docs/behavior/g0-host.md g0-host#11, #12).

use std::cell::Cell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::component::WindowExt as _;
use gpui_kit::{AppContext as _, EmptyView, VisualTestContext, actions, px, size};

use super::PaletteView;
use crate::core::commands::command_registry::CommandRegistry;

actions!(palette_test, [Ping]);

/// A window with the kit's root, the palette set up as at start, and a "Ping" command that counts its runs.
fn open(cx: &mut TestAppContext) -> (&mut VisualTestContext, Rc<Cell<usize>>) {
    let pings = Rc::new(Cell::new(0));
    let counter = pings.clone();
    cx.update(|cx| {
        gpui_kit::init(cx);
        cx.set_reduce_motion(true);
        PaletteView::init(cx);
        CommandRegistry::add("Ping", Box::new(Ping), cx);
        cx.on_action(move |_: &Ping, _| counter.set(counter.get() + 1));
    });
    let (_, cx) = cx.add_window_view(|window, cx| {
        let content = cx.new(|_| EmptyView);
        gpui_kit::base::Root::new(content, window, cx)
    });
    cx.simulate_resize(size(px(1000.), px(800.)));
    // The palette opens in the active window; a test window isn't active until it's asked to be.
    cx.update(|window, _| window.activate_window());
    draw(cx);
    (cx, pings)
}

fn draw(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

fn keys(cx: &mut VisualTestContext, keystrokes: &str) {
    cx.simulate_keystrokes(keystrokes);
    draw(cx);
}

fn palette_up(cx: &mut VisualTestContext) -> bool {
    cx.update(|window, cx| window.has_active_dialog(cx))
}

// Pins: g0-host#11, g0-host#12
#[gpui::test]
fn cmd_k_opens_the_palette_and_enter_runs_the_typed_command_and_closes_it(cx: &mut TestAppContext) {
    let (cx, pings) = open(cx);
    keys(cx, "secondary-k");
    assert!(palette_up(cx));

    cx.simulate_input("pin");
    keys(cx, "enter");
    assert_eq!(pings.get(), 1);
    assert!(!palette_up(cx));
}

// Pins: g0-host#11
#[gpui::test]
fn cmd_k_or_escape_closes_the_palette(cx: &mut TestAppContext) {
    let (cx, _) = open(cx);
    keys(cx, "secondary-k");
    keys(cx, "secondary-k");
    assert!(!palette_up(cx));

    keys(cx, "secondary-k");
    assert!(palette_up(cx));
    keys(cx, "escape");
    assert!(!palette_up(cx));
}
