//! `Frame` owns the window's root view: the top bar, the DockArea (built with the kit's DockSkin, which is what emits
//! a layout change when a dock edge drag ends) and the utility strip. Replaces ShellFrame.svelte and the `next-shell`
//! markup in routes/+page.svelte (lines 817-958).

use gpui_kit::component::dock::DockArea;
use gpui_kit::*;

use super::layout_store::LayoutStore;
use super::top_bar::TopBar;
use super::utility_strip::UtilityStrip;

pub struct Frame {
    top_bar: Entity<TopBar>,
    dock: Entity<DockArea>,
    utility_strip: Entity<UtilityStrip>,
    layout: LayoutStore,
    _subscriptions: Vec<Subscription>,
}

impl Frame {
    /// Registers the placeholder panes, builds the DockArea, loads the saved layout (or the default one), and only
    /// then subscribes to layout changes, so a launch doesn't write the layout straight back.
    pub fn new(_window: &mut Window, _cx: &mut Context<Self>) -> Self {
        todo!()
    }
}

impl Render for Frame {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
