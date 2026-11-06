use crate::core::ui::center_panes::CenterPanes;
use crate::features::preview_screen::Preview;
use gpui_kit::component::dock::{
    BasePanel, Panel, PanelEvent, PanelId, panel_handle, register_panel,
};
use gpui_kit::*;

const PANEL: &str = "Projects and Settings preview";
struct OpenPreview(WeakEntity<PreviewPanel>);
impl Global for OpenPreview {}

pub struct PreviewPanel {
    content: Entity<Preview>,
}

impl PreviewPanel {
    fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        Self {
            content: cx.new(|cx| Preview::new(window, cx)),
        }
    }

    fn show_settings(&self, settings: bool, cx: &mut Context<Self>) {
        self.content
            .update(cx, |content, cx| content.show_settings(settings, cx));
    }
}

pub fn register(cx: &mut App) {
    register_panel(cx, PANEL, |_, window, cx| {
        let pane = cx.new(|cx| PreviewPanel::new(window, cx));
        cx.set_global(OpenPreview(pane.downgrade()));
        CenterPanes::remember(PANEL, PanelId::from(pane.entity_id()), cx);
        panel_handle(pane)
    });
}

/// One tab hosts the sample screens; reopening a menu entry preserves its edits.
pub fn open(settings: bool, window: &mut Window, cx: &mut App) {
    let existing = cx
        .try_global::<OpenPreview>()
        .and_then(|pane| pane.0.upgrade());
    if let Some(pane) = existing {
        pane.update(cx, |pane, cx| pane.show_settings(settings, cx));
    }
    CenterPanes::open(PANEL, window, cx, |window, cx| {
        let pane = cx.new(|cx| PreviewPanel::new(window, cx));
        pane.update(cx, |pane, cx| pane.show_settings(settings, cx));
        cx.set_global(OpenPreview(pane.downgrade()));
        pane
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
        PANEL
    }
}
