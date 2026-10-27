//! `Frame` owns the window's root view: the top bar, the DockArea (drawn by MotionSkin over the kit's DockSkin, which is
//! what emits a layout change when a dock edge drag ends) and the utility strip. Replaces ShellFrame.svelte and the `next-shell`
//! markup in routes/+page.svelte (lines 817-958).

use std::rc::Rc;

use gpui_kit::component::dock::{DockArea, DockEvent, DockSkin};
use gpui_kit::component::v_flex;
use gpui_kit::*;
use serde_json::Value;

use super::default_layout::LAYOUT_VERSION;
use super::layout_store::{LayoutStore, WriteLayout};
use super::motion_skin::MotionSkin;
use super::top_bar::TopBar;
use super::utility_strip::UtilityStrip;

pub struct Frame {
    top_bar: Entity<TopBar>,
    dock: Entity<DockArea>,
    utility_strip: Entity<UtilityStrip>,
    pub(super) layout: LayoutStore,
    /// Reads the saved layout, then applies it.
    _load: Task<()>,
    _subscriptions: Vec<Subscription>,
}

impl Frame {
    /// Builds the DockArea and loads the saved layout, or the default one when there's none, through
    /// `DockArea::load` either way. Only then does it subscribe to layout changes, so a launch doesn't write the
    /// layout straight back.
    pub fn new(
        read: Task<Option<Value>>,
        write: WriteLayout,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let dock = cx.new(|cx| {
            let skin = MotionSkin(DockSkin::new(cx));
            DockArea::new("assembly-shell", Some(LAYOUT_VERSION), window, cx)
                .with_renderer(Rc::new(skin))
        });
        let top_bar = cx.new(|cx| TopBar::new(dock.clone(), cx));
        let utility_strip = cx.new(|_| UtilityStrip);
        let _load = cx.spawn_in(window, async move |this, cx| {
            let state = LayoutStore::layout_to_open(read.await);
            _ = this.update_in(cx, |frame, window, cx| {
                // The kit's load always succeeds: a pane it doesn't know loads as a placeholder that keeps its state.
                frame
                    .dock
                    .update(cx, |dock, cx| _ = dock.load(state, window, cx));
                let changes =
                    cx.subscribe_in(&frame.dock, window, |frame, _, event: &DockEvent, _, cx| {
                        if matches!(event, DockEvent::LayoutChanged) {
                            frame.layout.save_soon(cx);
                        }
                    });
                frame._subscriptions.push(changes);
            });
        });
        Self {
            top_bar,
            dock,
            utility_strip,
            layout: LayoutStore::new(write),
            _load,
            _subscriptions: Vec::new(),
        }
    }

    pub fn dock(&self) -> &Entity<DockArea> {
        &self.dock
    }
}

impl Render for Frame {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        v_flex()
            .size_full()
            .child(self.top_bar.clone())
            .child(div().flex_1().min_h_0().child(self.dock.clone()))
            .child(self.utility_strip.clone())
    }
}
