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
use crate::core::ui::center_panes::CenterPanes;

/// The work panel's distance from the window's edges (the design system's `panel-gap`).
const PANEL_GAP: Pixels = px(8.);
/// The work panel's corners (the design system's `radius-lg`).
const PANEL_RADIUS: Pixels = px(16.);
/// How narrow and how wide the sessions column and the drawer can be dragged (the approved look's layout). The kit
/// has no per-dock limits, so Frame holds them.
const LIMITS: [(DockPlacement, Pixels, Pixels); 2] = [
    (DockPlacement::Left, px(220.), px(560.)),
    (DockPlacement::Right, px(240.), px(640.)),
];

pub struct Frame {
    top_bar: Entity<TopBar>,
    dock: Entity<DockArea>,
    pub(super) layout: LayoutStore,
    /// How many of Frame's holds to a dock's limits still have their own layout event to come. Those events don't
    /// save, so a drag still saves once, when it ends.
    held: usize,
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
                area: cx.weak_entity(),
            };
            DockArea::new("assembly-shell", Some(LAYOUT_VERSION), window, cx)
                .with_renderer(Rc::new(skin))
        });
        CenterPanes::init(&dock, cx);
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
                // A layout saved past the limits opens held to them; like the load, that isn't saved.
                frame.dock.update(cx, |dock, cx| {
                    _ = dock.load(state, window, cx);
                    hold_to_limits(dock, window, cx);
                });
                let changes =
                    cx.subscribe_in(&frame.dock, window, |frame, _, event: &DockEvent, _, cx| {
                        if !matches!(event, DockEvent::LayoutChanged) {
                            return;
                        }
                        if frame.held > 0 {
                            frame.held -= 1;
                        } else {
                            frame.layout.save_soon(cx);
                        }
                    });
                // After the subscription, so every hold's layout event reaches it and clears `held`. A drag moves
                // the edge on each pointer move, and this puts it back at the limit before it's drawn.
                let limits = cx.observe_in(&frame.dock, window, |frame, dock, window, cx| {
                    frame.held += dock.update(cx, |dock, cx| hold_to_limits(dock, window, cx));
                });
                frame._subscriptions.extend([changes, limits]);
            });
        });
        Self {
            top_bar,
            dock,
            layout: LayoutStore::new(write),
            held: 0,
            _load,
            _subscriptions: vec![docked],
        }
    }

    pub fn dock(&self) -> &Entity<DockArea> {
        &self.dock
    }
}

/// Puts a side dock sized past its limits back at the nearest one, and says how many it put back.
fn hold_to_limits(dock: &mut DockArea, window: &mut Window, cx: &mut Context<DockArea>) -> usize {
    let mut held = 0;
    for (placement, min, max) in LIMITS {
        let Some(size) = dock.dock_size(placement) else {
            continue;
        };
        if size < min || size > max {
            held += 1;
            dock.set_dock_size(placement, size.clamp(min, max), window, cx);
        }
    }
    held
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
