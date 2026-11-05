use crate::core::ui::center_panes::CenterPanes;
use crate::features::preview_screen::Preview;
use gpui_kit::component::dock::{
    BasePanel, Panel, PanelEvent, PanelId, panel_handle, register_panel,
};
use gpui_kit::*;

/// The tab's name in a saved layout, kept from when it also held a Settings sample, so a saved tab still opens.
const PANEL: &str = "Projects and Settings preview";

pub struct PreviewPanel {
    content: Entity<Preview>,
}

impl PreviewPanel {
    fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        Self {
            content: cx.new(|cx| Preview::new(window, cx)),
        }
    }
}

pub fn register(cx: &mut App) {
    register_panel(cx, PANEL, |_, window, cx| {
        let pane = cx.new(|cx| PreviewPanel::new(window, cx));
        CenterPanes::remember(PANEL, PanelId::from(pane.entity_id()), cx);
        panel_handle(pane)
    });
}

/// One tab hosts the sample screens; reopening it keeps their edits.
pub fn open(window: &mut Window, cx: &mut App) {
    CenterPanes::open(PANEL, window, cx, |window, cx| {
        cx.new(|cx| PreviewPanel::new(window, cx))
    });
}

impl Render for PreviewPanel {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        self.content.clone()
    }
}
impl EventEmitter<PanelEvent> for PreviewPanel {}
impl Focusable for PreviewPanel {
    fn focus_handle(&self, cx: &App) -> FocusHandle {
        self.content.read(cx).focus_handle(cx)
    }
}
impl BasePanel for PreviewPanel {
    fn panel_name(&self) -> &'static str {
        PANEL
    }
}
impl Panel for PreviewPanel {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        "Projects preview"
    }
}
