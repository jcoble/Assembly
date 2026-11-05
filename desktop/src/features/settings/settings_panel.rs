//! `SettingsPanel` is Settings' own tab in the center, beside Chat and the files: one tab, which `OpenSettings` opens
//! or brings forward. The dock holds the tab's only handle, so closing it drops the page and everything it started.

use gpui_kit::component::dock::{
    BasePanel, Panel, PanelEvent, PanelId, panel_handle, register_panel,
};
use gpui_kit::*;

use super::settings_page::SettingsPage;
use crate::core::ui::center_panes::CenterPanes;

/// The tab's key in the center and its name in a saved layout.
const PANEL: &str = "Settings";

pub struct SettingsPanel {
    page: Entity<SettingsPage>,
}

impl SettingsPanel {
    fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        Self {
            page: cx.new(|cx| SettingsPage::new(window, cx)),
        }
    }
}

/// Lets a saved layout that had Settings open bring its tab back.
pub fn register(cx: &mut App) {
    register_panel(cx, PANEL, |_, window, cx| {
        let pane = cx.new(|cx| SettingsPanel::new(window, cx));
        CenterPanes::remember(PANEL, PanelId::from(pane.entity_id()), cx);
        panel_handle(pane)
    });
}

/// Brings the Settings tab forward, or opens it.
pub fn open(window: &mut Window, cx: &mut App) {
    CenterPanes::open(PANEL, window, cx, |window, cx| {
        cx.new(|cx| SettingsPanel::new(window, cx))
    });
}

impl Render for SettingsPanel {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        self.page.clone()
    }
}

impl EventEmitter<PanelEvent> for SettingsPanel {}

impl Focusable for SettingsPanel {
    fn focus_handle(&self, cx: &App) -> FocusHandle {
        self.page.read(cx).focus_handle(cx)
    }
}

impl BasePanel for SettingsPanel {
    fn panel_name(&self) -> &'static str {
        PANEL
    }
}

impl Panel for SettingsPanel {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        PANEL
    }
}

#[cfg(test)]
#[path = "settings_panel_tests.rs"]
mod tests;
