//! Tests for saving and reloading the window's layout (docs/behavior/g0-shape.md).

use std::cell::RefCell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::component::dock::DockPlacement;
use gpui_kit::{Entity, Task, VisualTestContext, px};
use serde_json::{Value, json};

use super::frame::Frame;
use super::placeholder_pane::PlaceholderPane;

type Writes = Rc<RefCell<Vec<Value>>>;

/// Opens a frame that reads `saved` and records each layout it writes.
fn open(saved: Option<Value>, cx: &mut TestAppContext) -> (Entity<Frame>, Writes, &mut VisualTestContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        PlaceholderPane::register_all(cx);
    });
    let writes = Writes::default();
    let sink = writes.clone();
    let (frame, cx) = cx.add_window_view(move |window, cx| {
        Frame::new(Task::ready(saved), Box::new(move |layout, _| sink.borrow_mut().push(layout)), window, cx)
    });
    cx.run_until_parked();
    (frame, writes, cx)
}

fn dump(frame: &Entity<Frame>, cx: &mut VisualTestContext) -> Value {
    cx.update(|_, cx| serde_json::to_value(frame.read(cx).dock().read(cx).dump(cx)).unwrap())
}

/// A saved layout without its split sizes: those are measurements, and a save made before the next layout pass records
/// the ones from the last pass.
fn without_sizes(mut layout: Value) -> Value {
    fn strip(value: &mut Value) {
        match value {
            Value::Object(map) => {
                map.remove("sizes");
                map.values_mut().for_each(strip);
            }
            Value::Array(items) => items.iter_mut().for_each(strip),
            _ => {}
        }
    }
    strip(&mut layout);
    layout
}

/// The three docks of a saved layout. The center's measured width is left out: it fills what's left, and a save made
/// before the next layout pass records the width from the last one.
fn docks(layout: &Value) -> [&Value; 3] {
    [&layout["left_dock"], &layout["right_dock"], &layout["bottom_dock"]]
}

// Pins: g0-shape#3
#[gpui::test]
fn the_sessions_width_and_fold_survive_a_relaunch(cx: &mut TestAppContext) {
    let (frame, writes, cx) = open(None, cx);
    cx.update(|window, cx| {
        frame.read(cx).dock().clone().update(cx, |dock, cx| {
            dock.set_dock_size(DockPlacement::Left, px(400.), window, cx);
            dock.toggle_dock(DockPlacement::Left, window, cx);
        })
    });
    cx.run_until_parked();
    let saved = writes.borrow().last().cloned().expect("the change was saved");

    let (again, _, cx) = open(Some(saved.clone()), &mut cx.cx);
    assert_eq!(without_sizes(dump(&again, cx)), without_sizes(saved));
    cx.update(|_, cx| {
        let dock = again.read(cx).dock().read(cx);
        assert_eq!(dock.dock_size(DockPlacement::Left), Some(px(400.)));
        assert!(!dock.is_dock_open(DockPlacement::Left));
    });
}

// Pins: g0-shape#3
#[gpui::test]
fn an_old_or_unreadable_layout_opens_the_default(cx: &mut TestAppContext) {
    let (fresh, _, cx) = open(None, cx);
    let default = dump(&fresh, cx);
    let mut old = default.clone();
    old["version"] = json!(0);

    for saved in [json!("{"), old] {
        let (frame, _, cx) = open(Some(saved), &mut cx.cx);
        assert_eq!(dump(&frame, cx), default);
    }
}

// Pins: g0-shape#4
#[gpui::test]
fn a_launch_does_not_save(cx: &mut TestAppContext) {
    let (fresh, _, cx) = open(None, cx);
    let saved = dump(&fresh, cx);
    let (_, writes, _) = open(Some(saved), &mut cx.cx);
    assert!(writes.borrow().is_empty());
}

// Pins: g0-shape#4
#[gpui::test]
fn changes_made_together_save_once(cx: &mut TestAppContext) {
    let (frame, writes, cx) = open(None, cx);
    cx.update(|window, cx| {
        frame.read(cx).dock().clone().update(cx, |dock, cx| {
            dock.toggle_dock(DockPlacement::Right, window, cx);
            dock.set_dock_size(DockPlacement::Left, px(400.), window, cx);
        })
    });
    cx.run_until_parked();
    let writes = writes.borrow();
    assert_eq!(writes.len(), 1);
    assert_eq!(docks(&writes[0]), docks(&dump(&frame, cx)));
}

// Pins: g0-shape#4
#[gpui::test]
fn a_change_that_follows_from_another_in_the_same_update_saves_with_it(cx: &mut TestAppContext) {
    let (frame, writes, cx) = open(None, cx);
    cx.update(|window, cx| {
        let dock = frame.read(cx).dock().clone();
        dock.update(cx, |dock, cx| dock.toggle_dock(DockPlacement::Right, window, cx));
        // As a tab click does: the dock hears about it later in the same update.
        window.defer(cx, move |window, cx| {
            dock.update(cx, |dock, cx| dock.toggle_dock(DockPlacement::Bottom, window, cx))
        });
    });
    cx.run_until_parked();
    let writes = writes.borrow();
    assert_eq!(writes.len(), 1);
    assert_eq!(docks(&writes[0]), docks(&dump(&frame, cx)));
}

// Pins: g0-shape#4
#[gpui::test]
fn each_later_change_saves_the_latest_layout(cx: &mut TestAppContext) {
    let (frame, writes, cx) = open(None, cx);
    for placement in [DockPlacement::Right, DockPlacement::Bottom] {
        cx.update(|window, cx| {
            frame.read(cx).dock().clone().update(cx, |dock, cx| dock.toggle_dock(placement, window, cx))
        });
        cx.run_until_parked();
    }
    let writes = writes.borrow();
    assert_eq!(writes.len(), 2);
    assert_eq!(docks(&writes[1]), docks(&dump(&frame, cx)));
}
