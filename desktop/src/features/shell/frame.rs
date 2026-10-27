//! `Frame` owns the window's root view: the title row and the DockArea (drawn by MotionSkin over the kit's DockSkin,
//! which is what emits a layout change when a dock edge drag ends) on the rail ground, and the one rounded work panel
//! behind the center, the drawer and the bottom dock. The utility strip sits at the foot of the sessions column
//! (MotionSkin). Replaces ShellFrame.svelte and the `next-shell` markup in routes/+page.svelte (lines 817-958).

use std::rc::Rc;

use gpui_kit::component::dock::{DockArea, DockEvent, DockPlacement, DockSkin};
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::Value;

use super::default_layout::LAYOUT_VERSION;
use super::layout_store::{LayoutStore, WriteLayout};
use super::motion_skin::MotionSkin;
use super::top_bar::TopBar;
use super::utility_strip::UtilityStrip;
use crate::core::native_views::views::NativeViews;

/// The work panel's distance from the window's edges (the design system's `panel-gap`).
const PANEL_GAP: Pixels = px(8.);
/// The work panel's corners (the design system's `radius-lg`).
const PANEL_RADIUS: Pixels = px(16.);

pub struct Frame {
    top_bar: Entity<TopBar>,
    dock: Entity<DockArea>,
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
        let utility_strip = cx.new(|_| UtilityStrip);
        let dock = cx.new(|cx| {
            let skin = MotionSkin {
                inner: DockSkin::new(cx),
                foot: utility_strip.into(),
            };
            DockArea::new("assembly-shell", Some(LAYOUT_VERSION), window, cx)
                .with_renderer(Rc::new(skin))
        });
        let top_bar = cx.new(|cx| TopBar::new(dock.clone(), cx));
        // Any dock change (a layout change, zoom, a live edge drag, a resize) moves panes, so the terminal and
        // browser views are placed afresh, and the work panel follows the sessions column's edge.
        let docked = cx.observe_in(&dock, window, |_, _, window, cx| {
            NativeViews::layout_changed(window, cx);
            cx.notify();
        });
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
            layout: LayoutStore::new(write),
            _load,
            _subscriptions: vec![docked],
        }
    }

    pub fn dock(&self) -> &Entity<DockArea> {
        &self.dock
    }
}

impl Render for Frame {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let dock = self.dock.read(cx);
        // A zoomed pane fills the area without the sessions column, so the panel starts at the window's edge then too.
        let sessions = (!dock.is_zoomed() && dock.is_dock_open(DockPlacement::Left))
            .then(|| dock.dock_size(DockPlacement::Left))
            .flatten();
        let theme = cx.theme();
        // The panel's box: from the sessions column's edge to `PANEL_GAP` short of the window's.
        let panel = || {
            div()
                .absolute()
                .top_0()
                .left(sessions.unwrap_or(PANEL_GAP))
                .right(PANEL_GAP)
                .bottom(PANEL_GAP)
                .rounded(PANEL_RADIUS)
        };
        v_flex()
            .size_full()
            .bg(theme.sidebar)
            .child(self.top_bar.clone())
            .child(
                div()
                    .flex_1()
                    .min_h_0()
                    .relative()
                    .child(panel().bg(theme.background))
                    .child(
                        div()
                            .size_full()
                            .pr(PANEL_GAP)
                            .pb(PANEL_GAP)
                            .when(sessions.is_none(), |area| area.pl(PANEL_GAP))
                            .child(self.dock.clone()),
                    )
                    // The ring goes over the panes, so a tab bar or a pane with its own fill doesn't hide it.
                    .child(panel().border_1().border_color(theme.border)),
            )
    }
}
