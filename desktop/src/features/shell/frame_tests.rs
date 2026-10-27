//! Tests for saving and reloading the window's layout (docs/behavior/g0-shape.md).

use std::cell::{Cell, RefCell};
use std::rc::Rc;

use futures_channel::oneshot;
use gpui::TestAppContext;
use gpui_kit::component::dock::{
    BasePanel, DockPlacement, Panel, PanelEvent, panel_handle, register_panel,
};
use gpui_kit::{
    App, AppContext as _, Context, Entity, EventEmitter, FocusHandle, Focusable, IntoElement,
    Modifiers, MouseButton, Render, Task, VisualTestContext, Window, div, point, px, size,
};
use serde_json::{Value, json};

use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::update::Update;

use super::default_layout::{CHAT, LAYOUT_VERSION};
use super::layout_store::{LAYOUT_KEY, WriteLayout, read_saved_layout, saved_layout};

use super::frame::Frame;
use super::placeholder_pane::PlaceholderPane;

type Writes = Rc<RefCell<Vec<Value>>>;

/// Opens a frame that reads `saved` and records each layout it writes, after the app start's setup.
pub(super) fn open(
    saved: Option<Value>,
    cx: &mut TestAppContext,
) -> (Entity<Frame>, Writes, &mut VisualTestContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        PlaceholderPane::register_all(cx);
    });
    open_window(saved, cx)
}

fn open_window(
    saved: Option<Value>,
    cx: &mut TestAppContext,
) -> (Entity<Frame>, Writes, &mut VisualTestContext) {
    let writes = Writes::default();
    let sink = writes.clone();
    let write: WriteLayout = Box::new(move |layout, _| {
        sink.borrow_mut().push(layout);
        Task::ready(())
    });
    let (frame, cx) = open_with(saved, write, cx);
    (frame, writes, cx)
}

fn open_with(
    saved: Option<Value>,
    write: WriteLayout,
    cx: &mut TestAppContext,
) -> (Entity<Frame>, &mut VisualTestContext) {
    open_reading(Task::ready(saved), write, cx)
}

fn open_reading(
    read: Task<Option<Value>>,
    write: WriteLayout,
    cx: &mut TestAppContext,
) -> (Entity<Frame>, &mut VisualTestContext) {
    let (frame, cx) = cx.add_window_view(move |window, cx| Frame::new(read, write, window, cx));
    cx.simulate_resize(size(px(1400.), px(900.)));
    cx.run_until_parked();
    (frame, cx)
}

pub(super) fn dump(frame: &Entity<Frame>, cx: &mut VisualTestContext) -> Value {
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
    [
        &layout["left_dock"],
        &layout["right_dock"],
        &layout["bottom_dock"],
    ]
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
    let saved = writes
        .borrow()
        .last()
        .cloned()
        .expect("the change was saved");

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
    old["version"] = json!(LAYOUT_VERSION - 1);

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
        dock.update(cx, |dock, cx| {
            dock.toggle_dock(DockPlacement::Right, window, cx)
        });
        // As a tab click does: the dock hears about it later in the same update.
        window.defer(cx, move |window, cx| {
            dock.update(cx, |dock, cx| {
                dock.toggle_dock(DockPlacement::Bottom, window, cx)
            })
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
            frame
                .read(cx)
                .dock()
                .clone()
                .update(cx, |dock, cx| dock.toggle_dock(placement, window, cx))
        });
        cx.run_until_parked();
    }
    let writes = writes.borrow();
    assert_eq!(writes.len(), 2);
    assert_eq!(docks(&writes[1]), docks(&dump(&frame, cx)));
}

fn pane_names(group: &Value) -> Vec<&str> {
    group["children"]
        .as_array()
        .unwrap()
        .iter()
        .map(|pane| pane["panel_name"].as_str().unwrap())
        .collect()
}

// Pins: g0-shape#1
#[gpui::test]
fn the_window_opens_in_the_old_layout(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    let layout = dump(&frame, cx);
    let [left, right, bottom] = docks(&layout);
    assert_eq!(
        (&left["size"], &left["open"]),
        (&json!(326.0), &json!(true))
    );
    assert_eq!(pane_names(&left["panel"]), ["Sessions", "Files", "Search"]);
    assert_eq!(
        (&right["size"], &right["open"]),
        (&json!(320.0), &json!(false))
    );
    assert_eq!(
        pane_names(&right["panel"]),
        [
            "Source control",
            "Agents",
            "Tasks",
            "Worktrees",
            "Run",
            "Context",
            "History"
        ]
    );
    assert_eq!(
        (&bottom["size"], &bottom["open"]),
        (&json!(180.0), &json!(false))
    );
    assert_eq!(pane_names(&bottom["panel"]), ["Problems", "Terminal"]);
    assert_eq!(pane_names(&layout["center"]["children"][0]), [CHAT]);
}

/// A real pane, as a feature lane registers one.
struct RealChat {
    focus: FocusHandle,
}

impl EventEmitter<PanelEvent> for RealChat {}

impl Focusable for RealChat {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for RealChat {
    fn panel_name(&self) -> &'static str {
        CHAT
    }
}

impl Panel for RealChat {}

impl Render for RealChat {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}

// Pins: g0-shape#8
#[gpui::test]
fn a_real_pane_registered_after_the_placeholders_replaces_one(cx: &mut TestAppContext) {
    let built = Rc::new(Cell::new(false));
    cx.update(|cx| {
        gpui_kit::init(cx);
        PlaceholderPane::register_all(cx);
        let built = built.clone();
        register_panel(cx, CHAT, move |_, _, cx| {
            built.set(true);
            panel_handle(cx.new(|cx| RealChat {
                focus: cx.focus_handle(),
            }))
        });
    });
    let (_, writes, _) = open_window(None, cx);
    assert!(
        built.get(),
        "the default layout built the real Chat pane, not the placeholder"
    );
    assert!(writes.borrow().is_empty());
}

// Pins: g0-shape#3
#[gpui::test]
fn a_pane_this_build_does_not_know_survives_a_save(cx: &mut TestAppContext) {
    let (fresh, _, cx) = open(None, cx);
    let mut saved = dump(&fresh, cx);
    saved["right_dock"]["panel"]["children"]
        .as_array_mut()
        .unwrap()
        .push(json!({"panel_name": "PaneFromTheFuture", "children": [], "info": {"panel": {"kept": 1}}}));

    let (frame, writes, cx) = open(Some(saved), &mut cx.cx);
    cx.update(|window, cx| {
        frame.read(cx).dock().clone().update(cx, |dock, cx| {
            dock.toggle_dock(DockPlacement::Right, window, cx)
        })
    });
    cx.run_until_parked();
    let written = writes
        .borrow()
        .last()
        .cloned()
        .expect("the toggle was saved");
    let future = written["right_dock"]["panel"]["children"]
        .as_array()
        .unwrap()
        .last()
        .cloned()
        .unwrap();
    assert_eq!(
        future,
        json!({"panel_name": "PaneFromTheFuture", "children": [], "info": {"panel": {"kept": 1}}})
    );
}

// Pins: g0-shape#4
#[gpui::test]
fn dragging_the_sessions_edge_saves_once_when_the_drag_ends(cx: &mut TestAppContext) {
    let (frame, writes, cx) = open(None, cx);
    cx.update(|window, cx| window.draw(cx).clear(cx));
    // The sessions column is 326 wide, so its edge sits just inside x = 326; the panes start under the 42 px top bar.
    let y = px(400.);
    cx.simulate_mouse_down(point(px(324.5), y), MouseButton::Left, Modifiers::none());
    for x in [330., 360., 400.] {
        cx.simulate_mouse_move(point(px(x), y), MouseButton::Left, Modifiers::none());
    }
    cx.run_until_parked();
    assert!(
        writes.borrow().is_empty(),
        "nothing saves while the edge is being dragged"
    );
    cx.simulate_mouse_up(point(px(400.), y), MouseButton::Left, Modifiers::none());
    cx.run_until_parked();
    let writes = writes.borrow();
    assert_eq!(writes.len(), 1);
    assert_eq!(writes[0]["left_dock"]["size"], json!(400.0));
    assert_eq!(
        cx.update(|_, cx| frame
            .read(cx)
            .dock()
            .read(cx)
            .dock_size(DockPlacement::Left)),
        Some(px(400.))
    );
}

// Pins: g0-shape#9
#[gpui::test]
fn the_usage_popover_is_built_only_once_it_opens(cx: &mut TestAppContext) {
    let (_, _, cx) = open(None, cx);
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(cx.debug_bounds("usage-body").is_none());
    let button = cx
        .debug_bounds("usage-button")
        .expect("the strip shows Usage");
    cx.simulate_click(button.center(), Modifiers::none());
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let body = cx.debug_bounds("usage-body").expect("the popover opened");
    assert!(
        body.bottom() <= button.top(),
        "the popover opens above the strip"
    );
}

// Pins: g0-shape#9
#[gpui::test]
fn folding_the_sessions_column_hides_the_strip(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(cx.debug_bounds("usage-button").is_some());
    cx.update(|window, cx| {
        frame.read(cx).dock().clone().update(cx, |dock, cx| {
            dock.toggle_dock(DockPlacement::Left, window, cx);
        })
    });
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(cx.debug_bounds("usage-button").is_none());
}

// Pins: g0-shape#3
#[test]
fn the_saved_layout_is_the_layout_preference_in_the_first_state() {
    let layout = json!({"version": 1});
    let state = Update::State(vec![
        ("theme".into(), json!({"key": "theme", "value": "dark"})),
        (
            LAYOUT_KEY.into(),
            json!({"key": LAYOUT_KEY, "value": layout}),
        ),
    ]);
    assert_eq!(saved_layout(Some(state)), Some(layout));
    assert_eq!(saved_layout(Some(Update::State(Vec::new()))), None);
    // The subscription ended without a state: the backend refused it.
    assert_eq!(saved_layout(None), None);
}

/// Records which of the saves still being written were given up, by their order.
struct GivenUp(usize, Rc<RefCell<Vec<usize>>>);

impl Drop for GivenUp {
    fn drop(&mut self) {
        self.1.borrow_mut().push(self.0);
    }
}

// Pins: g0-shape#4
#[gpui::test]
fn a_newer_save_replaces_one_still_being_written(cx: &mut TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        PlaceholderPane::register_all(cx);
    });
    let given_up = Rc::new(RefCell::new(Vec::new()));
    let (record, saves) = (given_up.clone(), Cell::new(0));
    // A backend that never answers a save.
    let write: WriteLayout = Box::new(move |_, cx| {
        let pending = GivenUp(saves.replace(saves.get() + 1), record.clone());
        cx.spawn(async move |_| {
            let _pending = pending;
            std::future::pending::<()>().await
        })
    });
    let (frame, cx) = open_with(None, write, cx);
    for placement in [DockPlacement::Right, DockPlacement::Bottom] {
        cx.update(|window, cx| {
            frame
                .read(cx)
                .dock()
                .clone()
                .update(cx, |dock, cx| dock.toggle_dock(placement, window, cx))
        });
        cx.run_until_parked();
    }
    assert_eq!(
        *given_up.borrow(),
        [0],
        "the first save was given up for the second"
    );
}

/// Stands in for the socket client's connection state.
struct Connection;

impl EventEmitter<ConnectionState> for Connection {}

// Pins: g0-shape#3
#[gpui::test]
fn a_backend_that_refuses_the_window_opens_the_default_layout(cx: &mut TestAppContext) {
    let (fresh, _, cx) = open(None, cx);
    let default = dump(&fresh, cx);
    let (connection, read) = cx.update(|_, cx| {
        let connection = cx.new(|_| Connection);
        // The preferences state never comes; the refusal is what ends the wait.
        let read = read_saved_layout(
            std::future::pending(),
            std::future::pending(),
            &connection,
            cx,
        );
        (connection, read)
    });
    let (frame, cx) = open_reading(read, Box::new(|_, _| Task::ready(())), &mut cx.cx);
    assert_ne!(
        dump(&frame, cx),
        default,
        "the window waits for the saved layout"
    );
    let refused = ConnectionState::Refused { code: 4401 };
    cx.update(|_, cx| connection.update(cx, |_, cx| cx.emit(refused)));
    cx.run_until_parked();
    assert_eq!(dump(&frame, cx), default);
}

// Pins: g0-shape#3
#[gpui::test]
fn a_backend_that_connects_the_window_opens_the_saved_layout(cx: &mut TestAppContext) {
    let (fresh, _, cx) = open(None, cx);
    let mut saved = dump(&fresh, cx);
    saved["right_dock"]["open"] = json!(true);
    let (state, first) = oneshot::channel::<Update>();
    let (connection, read) = cx.update(|_, cx| {
        let connection = cx.new(|_| Connection);
        let first = async { first.await.ok() };
        let read = read_saved_layout(first, std::future::pending(), &connection, cx);
        (connection, read)
    });
    let (frame, cx) = open_reading(read, Box::new(|_, _| Task::ready(())), &mut cx.cx);
    // The client connects before the preferences state comes.
    let connected = ConnectionState::Connected {
        machine: "mac".into(),
    };
    cx.update(|_, cx| connection.update(cx, |_, cx| cx.emit(connected)));
    let item = json!({"key": LAYOUT_KEY, "value": saved.clone()});
    _ = state.send(Update::State(vec![(LAYOUT_KEY.into(), item)]));
    cx.run_until_parked();
    assert_eq!(without_sizes(dump(&frame, cx)), without_sizes(saved));
}

// Pins: g0-shape#3
#[gpui::test]
fn a_launcher_that_gives_up_after_ready_the_window_opens_the_default_layout(
    cx: &mut TestAppContext,
) {
    let (fresh, _, cx) = open(None, cx);
    let default = dump(&fresh, cx);
    let (give_up, gave_up) = oneshot::channel::<()>();
    let (connection, read) = cx.update(|_, cx| {
        let connection = cx.new(|_| Connection);
        // The backend was ready, then died before the preferences state, until the launcher gave up.
        let gave_up = async { _ = gave_up.await };
        let read = read_saved_layout(std::future::pending(), gave_up, &connection, cx);
        (connection, read)
    });
    let (frame, cx) = open_reading(read, Box::new(|_, _| Task::ready(())), &mut cx.cx);
    let connected = ConnectionState::Connected {
        machine: "mac".into(),
    };
    cx.update(|_, cx| connection.update(cx, |_, cx| cx.emit(connected)));
    cx.run_until_parked();
    assert_ne!(
        dump(&frame, cx),
        default,
        "the window waits for the saved layout"
    );
    drop(give_up);
    cx.run_until_parked();
    assert_eq!(dump(&frame, cx), default);
}
