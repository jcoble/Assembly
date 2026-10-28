//! Opening a pane in the center puts it beside Chat once per key, and closing its tab drops it.

use std::cell::Cell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::component::dock::{
    BasePanel, DockArea, DockPlacement, Panel, PanelEvent, PanelId, PanelState, panel_handle,
};
use gpui_kit::{
    App, AppContext as _, Context, Entity, EventEmitter, FocusHandle, Focusable, IntoElement,
    Render, Task, VisualTestContext, WeakEntity, Window, div,
};

use super::CenterPanes;

/// A pane holding a task, standing in for Chat or a file or diff view.
struct Probe {
    name: &'static str,
    focus: FocusHandle,
    _work: Task<()>,
}

impl Probe {
    fn new(name: &'static str, cx: &mut App) -> Entity<Probe> {
        cx.new(|cx| Probe {
            name,
            focus: cx.focus_handle(),
            _work: Task::ready(()),
        })
    }
}

/// Holds the window's dock, as the shell's frame does.
struct Host;

impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}

/// A dock whose center holds Chat, set up for center panes.
fn open(cx: &mut TestAppContext) -> (Entity<DockArea>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let dock = cx.update(|window, cx| {
        let dock = cx.new(|cx| DockArea::new("center-panes", None, window, cx));
        let chat = Probe::new("Chat", cx);
        dock.update(cx, |dock, cx| {
            dock.add_panel_view(panel_handle(chat), DockPlacement::Center, None, window, cx)
        });
        CenterPanes::init(&dock, cx);
        dock
    });
    cx.run_until_parked();
    (dock, cx)
}

impl EventEmitter<PanelEvent> for Probe {}

impl Focusable for Probe {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl Render for Probe {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}

impl BasePanel for Probe {
    fn panel_name(&self) -> &'static str {
        self.name
    }
}

impl Panel for Probe {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        "probe.rs"
    }
}

/// The names in the center's first tab row, and which one is in front.
fn center(dock: &Entity<DockArea>, cx: &mut VisualTestContext) -> (Vec<String>, usize) {
    fn tabs(state: &PanelState) -> Option<&PanelState> {
        if state.panel_name == "TabPanel" {
            return Some(state);
        }
        state.children.iter().find_map(tabs)
    }
    cx.update(|_, cx| {
        let layout = dock.read(cx).dump(cx);
        let row = tabs(&layout.center).expect("a tab row in the center");
        let names = row
            .children
            .iter()
            .map(|pane| pane.panel_name.clone())
            .collect();
        let active = serde_json::to_value(&row.info).unwrap()["tabs"]["active_index"]
            .as_u64()
            .unwrap() as usize;
        (names, active)
    })
}

fn open_probe(key: &str, cx: &mut VisualTestContext) -> Option<PanelId> {
    let id =
        cx.update(|window, cx| CenterPanes::open(key, window, cx, |_, cx| Probe::new("Probe", cx)));
    cx.run_until_parked();
    id
}

#[gpui::test]
fn a_key_opens_one_pane_beside_chat_and_opening_it_again_brings_it_forward(
    cx: &mut TestAppContext,
) {
    let (dock, cx) = open(cx);
    let first = open_probe("file:a", cx).expect("the dock is set up");
    assert_eq!(center(&dock, cx), (vec!["Chat".into(), "Probe".into()], 1));
    let other = open_probe("file:b", cx).unwrap();
    assert_ne!(first, other);
    assert_eq!(center(&dock, cx).1, 2);
    assert_eq!(open_probe("file:a", cx), Some(first));
    assert_eq!(
        center(&dock, cx),
        (vec!["Chat".into(), "Probe".into(), "Probe".into()], 1)
    );
}

#[gpui::test]
fn closing_a_pane_s_tab_drops_it_and_its_key_opens_a_new_one(cx: &mut TestAppContext) {
    let (dock, cx) = open(cx);
    let built = Rc::new(Cell::new(None));
    let keep = built.clone();
    let first = cx.update(|window, cx| {
        CenterPanes::open("file:a", window, cx, |_, cx| {
            let pane = Probe::new("Probe", cx);
            keep.set(Some(pane.downgrade()));
            pane
        })
    });
    cx.run_until_parked();
    let held: WeakEntity<Probe> = built.take().unwrap();
    cx.update(|window, cx| {
        let pane = held.upgrade().unwrap();
        dock.update(cx, |dock, cx| dock.remove_panel(pane, window, cx))
    });
    cx.run_until_parked();
    assert!(
        held.upgrade().is_none(),
        "the dock held the pane's only handle"
    );
    assert_eq!(center(&dock, cx).0, vec!["Chat".to_string()]);
    assert_ne!(open_probe("file:a", cx), first);
}
