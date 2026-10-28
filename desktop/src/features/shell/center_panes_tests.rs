//! Opening a pane in the center puts it beside Chat once per key, and closing its tab drops it.

use std::cell::Cell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, PanelId};
use gpui_kit::{
    App, AppContext as _, Context, Entity, EventEmitter, FocusHandle, Focusable, IntoElement,
    Render, Task, VisualTestContext, WeakEntity, Window, div,
};

use super::CenterPanes;
use crate::features::shell::frame::Frame;
use crate::features::shell::frame_tests::{dump, open};

/// A pane holding a task, standing in for a file or diff view.
struct Probe {
    focus: FocusHandle,
    _work: Task<()>,
}

impl Probe {
    fn new(cx: &mut App) -> Entity<Probe> {
        cx.new(|cx| Probe {
            focus: cx.focus_handle(),
            _work: Task::ready(()),
        })
    }
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
        "Probe"
    }
}

impl Panel for Probe {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        "probe.rs"
    }
}

/// The names in the center's first tab row, and which one is in front.
fn center(frame: &Entity<Frame>, cx: &mut VisualTestContext) -> (Vec<String>, u64) {
    let layout = dump(frame, cx);
    let tabs = &layout["center"]["children"][0];
    let names = tabs["children"]
        .as_array()
        .unwrap()
        .iter()
        .map(|pane| pane["panel_name"].as_str().unwrap().to_string())
        .collect();
    (
        names,
        tabs["info"]["tabs"]["active_index"].as_u64().unwrap(),
    )
}

fn open_probe(key: &str, cx: &mut VisualTestContext) -> Option<PanelId> {
    let id = cx.update(|window, cx| CenterPanes::open(key, window, cx, |_, cx| Probe::new(cx)));
    cx.run_until_parked();
    id
}

#[gpui::test]
fn a_key_opens_one_pane_beside_chat_and_opening_it_again_brings_it_forward(
    cx: &mut TestAppContext,
) {
    let (frame, _, cx) = open(None, cx);
    let first = open_probe("file:a", cx).expect("the dock is set up");
    assert_eq!(center(&frame, cx), (vec!["Chat".into(), "Probe".into()], 1));
    let other = open_probe("file:b", cx).unwrap();
    assert_ne!(first, other);
    assert_eq!(center(&frame, cx).1, 2);
    assert_eq!(open_probe("file:a", cx), Some(first));
    assert_eq!(
        center(&frame, cx),
        (vec!["Chat".into(), "Probe".into(), "Probe".into()], 1)
    );
}

#[gpui::test]
fn closing_a_pane_s_tab_drops_it_and_its_key_opens_a_new_one(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    let built = Rc::new(Cell::new(None));
    let keep = built.clone();
    let first = cx.update(|window, cx| {
        CenterPanes::open("file:a", window, cx, |_, cx| {
            let pane = Probe::new(cx);
            keep.set(Some(pane.downgrade()));
            pane
        })
    });
    cx.run_until_parked();
    let held: WeakEntity<Probe> = built.take().unwrap();
    cx.update(|window, cx| {
        let pane = held.upgrade().unwrap();
        frame
            .read(cx)
            .dock()
            .clone()
            .update(cx, |dock, cx| dock.remove_panel(pane, window, cx))
    });
    cx.run_until_parked();
    assert!(
        held.upgrade().is_none(),
        "the dock held the pane's only handle"
    );
    assert_eq!(center(&frame, cx).0, vec!["Chat".to_string()]);
    assert_ne!(open_probe("file:a", cx), first);
}
