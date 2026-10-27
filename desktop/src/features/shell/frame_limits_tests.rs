//! Tests for the sessions column's and the drawer's drag limits (docs/behavior/g0-shape.md).

use gpui::TestAppContext;
use gpui_kit::component::dock::DockPlacement;
use gpui_kit::{Modifiers, MouseButton, VisualTestContext, point, px};
use serde_json::json;

use super::frame_tests::{dump, open};

// Pins: g0-shape#11
#[gpui::test]
fn dragging_the_sessions_edge_stops_at_its_limits(cx: &mut TestAppContext) {
    let (frame, writes, cx) = open(None, cx);
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let sessions = |cx: &mut VisualTestContext| {
        cx.update(|_, cx| {
            frame
                .read(cx)
                .dock()
                .read(cx)
                .dock_size(DockPlacement::Left)
        })
    };
    let y = px(400.);
    cx.simulate_mouse_down(point(px(324.5), y), MouseButton::Left, Modifiers::none());
    for x in [500., 700., 900.] {
        cx.simulate_mouse_move(point(px(x), y), MouseButton::Left, Modifiers::none());
    }
    cx.run_until_parked();
    assert_eq!(sessions(cx), Some(px(560.)), "held at 560 while dragging");
    assert!(writes.borrow().is_empty(), "nothing saves while dragging");
    cx.simulate_mouse_up(point(px(900.), y), MouseButton::Left, Modifiers::none());
    cx.run_until_parked();
    assert_eq!(writes.borrow().len(), 1);
    assert_eq!(writes.borrow()[0]["left_dock"]["size"], json!(560.0));

    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.simulate_mouse_down(point(px(558.5), y), MouseButton::Left, Modifiers::none());
    for x in [300., 100.] {
        cx.simulate_mouse_move(point(px(x), y), MouseButton::Left, Modifiers::none());
    }
    cx.simulate_mouse_up(point(px(100.), y), MouseButton::Left, Modifiers::none());
    cx.run_until_parked();
    assert_eq!(sessions(cx), Some(px(220.)));
    assert_eq!(writes.borrow().len(), 2);
    assert_eq!(writes.borrow()[1]["left_dock"]["size"], json!(220.0));
}

// Pins: g0-shape#11
#[gpui::test]
fn a_saved_layout_past_the_limits_opens_held_to_them(cx: &mut TestAppContext) {
    let (fresh, _, cx) = open(None, cx);
    let mut saved = dump(&fresh, cx);
    saved["left_dock"]["size"] = json!(900.0);
    saved["right_dock"]["size"] = json!(100.0);
    let (frame, writes, cx) = open(Some(saved), &mut cx.cx);
    cx.update(|_, cx| {
        let dock = frame.read(cx).dock().read(cx);
        assert_eq!(dock.dock_size(DockPlacement::Left), Some(px(560.)));
        assert_eq!(dock.dock_size(DockPlacement::Right), Some(px(240.)));
    });
    assert!(writes.borrow().is_empty(), "a launch doesn't save");
}

// Pins: g0-shape#11
#[gpui::test]
fn the_drawer_is_held_between_240_and_640(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    let drawer = |to: f32, cx: &mut VisualTestContext| {
        cx.update(|window, cx| {
            frame.read(cx).dock().clone().update(cx, |dock, cx| {
                dock.set_dock_size(DockPlacement::Right, px(to), window, cx)
            })
        });
        cx.run_until_parked();
        cx.update(|_, cx| {
            frame
                .read(cx)
                .dock()
                .read(cx)
                .dock_size(DockPlacement::Right)
        })
    };
    assert_eq!(drawer(900., cx), Some(px(640.)));
    assert_eq!(drawer(100., cx), Some(px(240.)));
    assert_eq!(drawer(400., cx), Some(px(400.)));
}
