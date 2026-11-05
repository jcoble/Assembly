//! Settings opens in its own tab beside Chat, once: opening it again brings that tab forward, and closing the tab drops
//! the page.

use gpui::TestAppContext;
use gpui_kit::component::dock::{
    BasePanel, DockArea, DockPlacement, Panel, PanelEvent, PanelState, panel_handle,
};
use gpui_kit::{
    App, AppContext as _, Context, Entity, EventEmitter, FocusHandle, Focusable, IntoElement,
    Render, VisualTestContext, Window, div,
};

use super::{SettingsPanel, open};
use crate::core::ui::center_panes::CenterPanes;

/// Chat, standing in the center before Settings opens.
struct Chat(FocusHandle);

impl Render for Chat {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
impl EventEmitter<PanelEvent> for Chat {}
impl Focusable for Chat {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.0.clone()
    }
}
impl BasePanel for Chat {
    fn panel_name(&self) -> &'static str {
        "Chat"
    }
}
impl Panel for Chat {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        "Chat"
    }
}

/// Holds the window's dock, as the shell's frame does.
struct Host;

impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}

/// A window whose dock's center holds Chat, set up for center panes.
fn window(cx: &mut TestAppContext) -> (Entity<DockArea>, Entity<Chat>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let (dock, chat) = cx.update(|window, cx| {
        let dock = cx.new(|cx| DockArea::new("settings-tab", None, window, cx));
        let chat = cx.new(|cx| Chat(cx.focus_handle()));
        dock.update(cx, |dock, cx| {
            dock.add_panel_view(
                panel_handle(chat.clone()),
                DockPlacement::Center,
                None,
                window,
                cx,
            )
        });
        CenterPanes::init(&dock, cx);
        (dock, chat)
    });
    cx.run_until_parked();
    (dock, chat, cx)
}

/// The names in the center's tab row, and which one is in front.
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

fn open_settings(cx: &mut VisualTestContext) {
    cx.update(open);
    cx.run_until_parked();
}

#[gpui::test]
fn opening_settings_again_brings_its_one_tab_forward(cx: &mut TestAppContext) {
    let (dock, chat, cx) = window(cx);
    open_settings(cx);
    assert_eq!(
        center(&dock, cx),
        (vec!["Chat".into(), "Settings".into()], 1)
    );
    cx.update(|window, cx| {
        let chat = panel_handle(chat.clone());
        dock.update(cx, |dock, cx| {
            dock.select_panel(chat.panel_id(cx), window, cx)
        })
    });
    cx.run_until_parked();
    assert_eq!(center(&dock, cx).1, 0);
    open_settings(cx);
    assert_eq!(
        center(&dock, cx),
        (vec!["Chat".into(), "Settings".into()], 1)
    );
}

#[gpui::test]
fn closing_the_settings_tab_drops_the_page(cx: &mut TestAppContext) {
    let (dock, _chat, cx) = window(cx);
    open_settings(cx);
    let (tab, page) = cx.update(|_, cx| {
        let tab = CenterPanes::find::<SettingsPanel>("Settings", cx).expect("the Settings tab");
        let page = tab.read(cx).page.downgrade();
        (tab.downgrade(), page)
    });
    cx.update(|window, cx| {
        let tab = tab.upgrade().expect("the tab while it's open");
        dock.update(cx, |dock, cx| dock.remove_panel(tab, window, cx))
    });
    cx.run_until_parked();
    assert!(
        tab.upgrade().is_none(),
        "the dock held the tab's only handle"
    );
    assert!(
        page.upgrade().is_none(),
        "the tab held the page's only handle"
    );
    assert_eq!(center(&dock, cx).0, vec!["Chat".to_string()]);
}
