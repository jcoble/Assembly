//! The Terminal's button in the top row and its item in the "..." menu, in a real frame's dock.

use std::cell::RefCell;
use std::rc::Rc;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::component::dock::{DockPlacement, PaneRef};
use gpui_kit::{App, Entity, Modifiers, Task, VisualTestContext, Window, px, size};
use serde_json::{Value, json};

use super::default_layout::{LAYOUT_VERSION, PROBLEMS, TERMINAL};
use super::frame::Frame;
use super::frame_tests::{dump, open};
use super::layout_store::WriteLayout;
use super::placeholder_pane::PlaceholderPane;
use super::terminal_toggle::{TerminalPress, press_terminal, terminal_showing};
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::workspace_panes::{PaneCodec, WorkspaceKey, WorkspacePanes};
use crate::features::terminal::terminal_pane::TerminalPane;
use crate::features::workspaces::workspace_coordinator::{
    WorkspaceActivated, WorkspaceCoordinator, WorkspaceSaved, WorkspaceTarget,
};
use DockPlacement::{Bottom, Center, Right};

/// The pane a dock shows, by name, from its saved form.
fn shown_in(panel: &Value) -> Value {
    let tabs = if panel["children"][0]["panel_name"] == "TabPanel" {
        &panel["children"][0]
    } else {
        panel
    };
    let shown = tabs["info"]["tabs"]["active_index"].as_u64().unwrap_or(0) as usize;
    tabs["children"][shown]["panel_name"].clone()
}

fn bottom(frame: &Entity<Frame>, cx: &mut VisualTestContext) -> (bool, Value) {
    let layout = dump(frame, cx);
    (
        layout["bottom_dock"]["open"].as_bool().unwrap(),
        shown_in(&layout["bottom_dock"]["panel"]),
    )
}

fn showing(frame: &Entity<Frame>, cx: &mut VisualTestContext) -> bool {
    cx.update(|_, cx| terminal_showing(frame.read(cx).dock(), cx))
}

fn draw(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

fn press_button(cx: &mut VisualTestContext) {
    draw(cx);
    let button = cx
        .debug_bounds("terminal-toggle")
        .expect("the top row has a Terminal button");
    cx.simulate_click(button.center(), Modifiers::none());
    draw(cx);
}

/// Picks the "..." menu's last item, the Terminal, with the drawer open.
fn press_menu_item(cx: &mut VisualTestContext) {
    draw(cx);
    let menu = cx
        .debug_bounds("drawer-overflow")
        .expect("the drawer has its menu");
    cx.simulate_click(menu.center(), Modifiers::none());
    draw(cx);
    // The menu has no item chosen yet: `down` takes the first, then `up` wraps to the last.
    cx.simulate_keystrokes("down up enter");
    draw(cx);
}

fn open_drawer(cx: &mut VisualTestContext) {
    draw(cx);
    let toggle = cx.debug_bounds("drawer-toggle").expect("the drawer toggle");
    cx.simulate_click(toggle.center(), Modifiers::none());
    draw(cx);
}

fn toggle_bottom(frame: &Entity<Frame>, cx: &mut VisualTestContext) {
    cx.update(|window, cx| {
        frame.read(cx).dock().clone().update(cx, |dock, cx| {
            dock.toggle_dock(Bottom, window, cx);
        })
    });
    draw(cx);
}

fn select(frame: &Entity<Frame>, placement: DockPlacement, name: &str, cx: &mut VisualTestContext) {
    let id = cx.update(|_, cx| {
        let area = frame.read(cx).dock().read(cx);
        let mut found = None;
        let tree = area.layout(placement).expect("the dock");
        tree.root().walk(&mut |node| {
            if let PaneRef::Tabs { panels, .. } = node.kind() {
                for id in panels.iter() {
                    if area
                        .panel(*id)
                        .is_some_and(|panel| panel.panel_name(cx) == name)
                    {
                        found = Some(*id);
                    }
                }
            }
        });
        found.expect("the pane is in the dock")
    });
    frame.update_in(cx, |frame, window, cx| {
        (frame.dock()).update(cx, |dock, cx| dock.select_panel(id, window, cx))
    });
    draw(cx);
}

/// The saved layout with the Terminal dragged from the bottom dock to `to`'s tabs, shown there, with the right dock open.
fn moved_to(layout: &Value, to: &str) -> Value {
    let mut layout = layout.clone();
    let children = layout["bottom_dock"]["panel"]["children"]
        .as_array_mut()
        .unwrap();
    let terminal = children.pop().unwrap();
    let tabs = match to {
        "right_dock" => {
            layout["right_dock"]["open"] = json!(true);
            &mut layout["right_dock"]["panel"]
        }
        _ => &mut layout["center"]["children"][0],
    };
    let children = tabs["children"].as_array_mut().unwrap();
    children.push(terminal);
    let last = children.len() - 1;
    tabs["info"]["tabs"]["active_index"] = json!(last);
    layout
}

#[test]
fn pressing_from_each_state() {
    // The bottom dock shut, on Problems, on the Terminal.
    assert_eq!(
        press_terminal(Bottom, false, false),
        TerminalPress::Open(Bottom)
    );
    assert_eq!(
        press_terminal(Bottom, true, false),
        TerminalPress::Show(Bottom)
    );
    assert_eq!(
        press_terminal(Bottom, true, true),
        TerminalPress::Shut(Bottom)
    );
    // Dragged to the right dock: the same there.
    assert_eq!(
        press_terminal(Right, false, false),
        TerminalPress::Open(Right)
    );
    assert_eq!(
        press_terminal(Right, true, false),
        TerminalPress::Show(Right)
    );
    assert_eq!(
        press_terminal(Right, true, true),
        TerminalPress::Shut(Right)
    );
    // Dragged to the center: there's no dock to shut.
    assert_eq!(
        press_terminal(Center, true, false),
        TerminalPress::Show(Center)
    );
    assert_eq!(press_terminal(Center, true, true), TerminalPress::Nothing);
}

#[gpui::test]
fn the_button_opens_the_dock_on_the_terminal(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    assert_eq!(bottom(&frame, cx), (false, json!(PROBLEMS)));
    assert!(!showing(&frame, cx));
    press_button(cx);
    assert_eq!(bottom(&frame, cx), (true, json!(TERMINAL)));
    assert!(showing(&frame, cx));
}

#[gpui::test]
fn the_button_moves_from_problems_to_the_terminal(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    toggle_bottom(&frame, cx);
    assert_eq!(bottom(&frame, cx), (true, json!(PROBLEMS)));
    assert!(!showing(&frame, cx), "the dock is open, but on Problems");
    press_button(cx);
    assert_eq!(bottom(&frame, cx), (true, json!(TERMINAL)));
    assert!(showing(&frame, cx));
}

#[gpui::test]
fn the_button_shuts_the_dock_when_the_terminal_shows(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    toggle_bottom(&frame, cx);
    select(&frame, Bottom, TERMINAL, cx);
    assert_eq!(bottom(&frame, cx), (true, json!(TERMINAL)));
    assert!(showing(&frame, cx));
    press_button(cx);
    assert_eq!(bottom(&frame, cx).0, false);
    assert!(!showing(&frame, cx));
}

#[gpui::test]
fn the_button_follows_the_terminal_to_the_right_dock(cx: &mut TestAppContext) {
    let (first, _, visual) = open(None, cx);
    let saved = moved_to(&dump(&first, visual), "right_dock");
    let (frame, _, cx) = open(Some(saved), &mut visual.cx);
    let layout = dump(&frame, cx);
    assert_eq!(layout["right_dock"]["open"], json!(true));
    assert_eq!(shown_in(&layout["right_dock"]["panel"]), json!(TERMINAL));
    assert!(showing(&frame, cx), "it shows in the right dock");
    press_button(cx);
    let layout = dump(&frame, cx);
    assert_eq!(
        layout["right_dock"]["open"],
        json!(false),
        "it shuts that dock"
    );
    assert_eq!(
        layout["bottom_dock"]["open"],
        json!(false),
        "and leaves this one"
    );
    assert!(!showing(&frame, cx));
    press_button(cx);
    let layout = dump(&frame, cx);
    assert_eq!(layout["right_dock"]["open"], json!(true));
    assert_eq!(shown_in(&layout["right_dock"]["panel"]), json!(TERMINAL));
    assert!(showing(&frame, cx));
}

#[gpui::test]
fn the_button_does_nothing_in_the_center(cx: &mut TestAppContext) {
    let (first, _, visual) = open(None, cx);
    let saved = moved_to(&dump(&first, visual), "center");
    let (frame, _, cx) = open(Some(saved), &mut visual.cx);
    assert_eq!(shown_in(&dump(&frame, cx)["center"]), json!(TERMINAL));
    assert!(showing(&frame, cx), "it shows in the center");
    let before = dump(&frame, cx);
    press_button(cx);
    let after = dump(&frame, cx);
    assert_eq!(after["bottom_dock"]["open"], before["bottom_dock"]["open"]);
    assert_eq!(after["center"]["children"], before["center"]["children"]);
    assert!(showing(&frame, cx));
}

#[gpui::test]
fn the_menu_item_does_the_same_and_its_check_means_showing(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    open_drawer(cx);
    // The check mark is `terminal_showing`, so its meaning is pinned through that: not merely that the dock is open.
    assert!(!showing(&frame, cx));
    press_menu_item(cx);
    assert_eq!(
        bottom(&frame, cx),
        (true, json!(TERMINAL)),
        "shut: opens on the Terminal"
    );
    assert!(showing(&frame, cx));
    select(&frame, Bottom, PROBLEMS, cx);
    assert!(!showing(&frame, cx), "open on Problems isn't showing");
    press_menu_item(cx);
    assert_eq!(
        bottom(&frame, cx),
        (true, json!(TERMINAL)),
        "on Problems: switches"
    );
    assert!(showing(&frame, cx));
    press_menu_item(cx);
    assert_eq!(bottom(&frame, cx).0, false, "showing: shuts the dock");
    assert!(!showing(&frame, cx));
}

#[gpui::test]
fn the_highlight_clears_when_problems_is_clicked(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    toggle_bottom(&frame, cx);
    select(&frame, Bottom, TERMINAL, cx);
    // The button's highlight is `terminal_showing`, so it's pinned through that, as the menu's check mark is.
    assert!(showing(&frame, cx), "the Terminal is showing");
    let problems = cx
        .debug_bounds("dock-tab-0")
        .expect("the bottom dock's first tab");
    cx.simulate_click(problems.center(), Modifiers::none());
    draw(cx);
    assert_eq!(bottom(&frame, cx), (true, json!(PROBLEMS)));
    assert!(!showing(&frame, cx), "Problems took the dock");
}

/// The workspace's panes, which capture a value the test sets.
struct Captured(Rc<RefCell<Value>>);

impl PaneCodec for Captured {
    fn name(&self) -> &'static str {
        "files"
    }
    fn capture(&self, _: &Window, _: &App) -> Result<Value, String> {
        Ok(self.0.borrow().clone())
    }
    fn validate(&self, _: &Value, _: &WorkspaceKey, _: &Value, _: &App) -> Result<(), String> {
        Ok(())
    }
    fn restore(&self, _: &Value, _: &mut Window, _: &mut App) {}
}

#[gpui::test]
async fn the_button_tells_the_workspace_the_layout_changed(cx: &mut TestAppContext) {
    let saves = Arc::new(AtomicUsize::new(0));
    let counted = saves.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        let result = match frame["method"] == "save" {
            true => json!({"revision": counted.fetch_add(1, Ordering::SeqCst) + 1}),
            false => json!({"revision": 0, "snapshot": null}),
        };
        vec![Reply::Frame(
            json!({"t": "result", "id": frame["id"], "result": result}),
        )]
    });
    cx.executor().allow_parking();
    let (_frame, _, cx) = open(None, cx);
    let live = Rc::new(RefCell::new(json!({"files": []})));
    let coordinator = cx.update(|_, cx| {
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start_bulk(endpoint, cx);
        WorkspacePanes::register(Box::new(Captured(live.clone())), cx).unwrap();
        WorkspaceCoordinator::init(cx)
    });
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _activated = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, _: &WorkspaceActivated, _| {
            _ = tell.unbounded_send(());
        })
    });
    let key = WorkspaceKey {
        machine: "this-machine".into(),
        session_id: "s".into(),
        checkout_root: "/a".into(),
    };
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::Checkout(key),
                Box::new(|_, _, _| Ok(())),
                window,
                cx,
            );
        })
    });
    changes.recv().await.unwrap();
    assert_eq!(saves.load(Ordering::SeqCst), 0);
    let tell = changed.clone();
    let _saved = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, _: &WorkspaceSaved, _| {
            _ = tell.unbounded_send(());
        })
    });
    *live.borrow_mut() = json!({"files": [], "activePath": "changed since the last save"});
    press_button(cx);
    until("the workspace saved", &mut changes, &changed, || {
        saves.load(Ordering::SeqCst) == 1
    })
    .await;
}

#[gpui::test]
fn the_real_pane_takes_the_focus_when_opened(cx: &mut TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        PlaceholderPane::register_all(cx);
        TerminalPane::register(cx);
    });
    let write: WriteLayout = Box::new(|_, _| Task::ready(()));
    let (frame, cx) =
        cx.add_window_view(|window, cx| Frame::new(Task::ready(None), write, window, cx));
    cx.simulate_resize(size(px(1400.), px(900.)));
    press_button(cx);
    let (real, focused) = cx.update(|window, cx| {
        let area = frame.read(cx).dock().read(cx);
        let mut pane = None;
        area.layout(Bottom)
            .expect("the bottom dock")
            .root()
            .walk(&mut |node| {
                if let PaneRef::Tabs { panels, .. } = node.kind() {
                    for id in panels.iter() {
                        let panel = area.panel(*id).filter(|panel| panel.panel_name(cx) == TERMINAL);
                        pane = pane.take().or(panel.cloned());
                    }
                }
            });
        let pane = pane.expect("the Terminal is in the bottom dock");
        (
            pane.view().downcast::<TerminalPane>().is_ok(),
            pane.focus_handle(cx).contains_focused(window, cx),
        )
    });
    assert!(real, "the dock built the real pane, not a placeholder");
    assert!(focused, "the pane is ready to type");
}

#[gpui::test]
fn the_layout_stays_as_it_was(cx: &mut TestAppContext) {
    assert_eq!(LAYOUT_VERSION, 2);
    let (frame, _, cx) = open(None, cx);
    let layout = dump(&frame, cx);
    let names: Vec<_> = (layout["bottom_dock"]["panel"]["children"]
        .as_array()
        .unwrap())
    .iter()
    .map(|pane| pane["panel_name"].clone())
    .collect();
    assert_eq!(names, [json!(PROBLEMS), json!(TERMINAL)]);
}
