//! `MotionSkin` owns how the docks sit on the window's grounds and how they open. It wraps the kit's DockSkin and
//! changes three things:
//! - an open dock's content fades in and slides in from the dock's outer edge. The dock's width still appears at once,
//!   and closing is instant: a width slide would need a change to the kit. With Reduce Motion on, the kit's animations
//!   jump to the end. New: the old app had no dock motion.
//! - splits and tab groups paint no fill of their own, so the sessions column shows the rail and the rest shows the
//!   work panel that Frame draws behind them (the design system's "a ground and one panel").
//! - the sessions column carries the utility strip at its foot, as the approved look has it
//!   (the design system's look-final/README.md:66); there's no bottom bar.
//! - the sessions column's tabs are the old app's (side_tabs.rs); the drawer draws none, its pane buttons sitting in
//!   the title row (drawer_buttons.rs); the chat's title and an open bottom dock's tabs are the kit's.
//! - a closed bottom dock draws nothing along the foot; Terminal in the title row opens it.
//!
//! Every other piece of the skin passes through, the drag edge and its save on release included.

use std::rc::Rc;
use std::sync::Arc;

use gpui_kit::base::ResizeHandleContext;
use gpui_kit::component::ActiveTheme as _;
use gpui_kit::component::dock::{
    BasePanelView, DockArea, DockAreaRenderer, DockContext, DockPlacement, DockSkin, DropIndicator,
    NodeId, PanelState, TabGroupContext, TabGroupRenderer,
};
use gpui_kit::component::v_flex;
use gpui_kit::*;

use super::side_tabs;

pub struct MotionSkin {
    pub inner: Rc<DockSkin>,
    /// Drawn at the foot of the sessions column.
    pub foot: AnyView,
    /// The area this skin draws, to tell which dock a tab group is in.
    pub area: WeakEntity<DockArea>,
}

impl DockAreaRenderer for MotionSkin {
    fn frame(&self, window: &mut Window, cx: &mut App) -> Stateful<Div> {
        self.inner.frame(window, cx)
    }

    fn split_frame(
        &self,
        node: NodeId,
        axis: Axis,
        window: &mut Window,
        cx: &mut App,
    ) -> Stateful<Div> {
        self.inner
            .split_frame(node, axis, window, cx)
            .bg(transparent_black())
    }

    fn center_frame(&self, window: &mut Window, cx: &mut App) -> Stateful<Div> {
        self.inner.center_frame(window, cx)
    }

    fn render_split_handle(
        &self,
        handle: &ResizeHandleContext,
        window: &mut Window,
        cx: &mut App,
    ) -> Option<AnyElement> {
        self.inner.render_split_handle(handle, window, cx)
    }

    fn render_dock(
        &self,
        dock: &DockContext,
        content: AnyElement,
        window: &mut Window,
        cx: &mut App,
    ) -> AnyElement {
        let content = if dock.placement() == DockPlacement::Left {
            v_flex()
                .size_full()
                .child(div().flex_1().min_h_0().child(content))
                .child(self.foot.clone())
                .into_any_element()
        } else {
            content
        };
        // A closed bottom dock draws nothing in the strip the kit keeps for it; Terminal in the title row opens it.
        if dock.placement() == DockPlacement::Bottom && !dock.is_open() {
            return Empty.into_any_element();
        }
        // Only the side docks slide in: a closed side dock draws nothing.
        let content = match dock.placement() {
            DockPlacement::Left | DockPlacement::Right if dock.is_open() => {
                entering(dock.placement(), content, window, cx)
            }
            _ => content,
        };
        self.inner.render_dock(dock, content, window, cx)
    }

    fn build_placeholder(
        &self,
        state: &PanelState,
        window: &mut Window,
        cx: &mut App,
    ) -> Option<Arc<dyn BasePanelView>> {
        self.inner.build_placeholder(state, window, cx)
    }

    fn tab_group_renderer(&self) -> Rc<dyn TabGroupRenderer> {
        Rc::new(Unfilled(self.inner.tab_group_renderer(), self.area.clone()))
    }
}

/// The kit's tab groups with their fill taken out (TabGroupSkin::frame paints `background`), so the ground behind
/// shows, and the side docks' own tabs. Everything else passes through.
struct Unfilled(Rc<dyn TabGroupRenderer>, WeakEntity<DockArea>);

impl TabGroupRenderer for Unfilled {
    fn frame(&self, group: &TabGroupContext, window: &mut Window, cx: &mut App) -> Stateful<Div> {
        self.0.frame(group, window, cx).bg(transparent_black())
    }

    fn content_frame(
        &self,
        group: &TabGroupContext,
        window: &mut Window,
        cx: &mut App,
    ) -> Stateful<Div> {
        self.0.content_frame(group, window, cx)
    }

    fn render_tab_bar(
        &self,
        group: &TabGroupContext,
        window: &mut Window,
        cx: &mut App,
    ) -> AnyElement {
        match side(&self.1, group.node(), cx) {
            Some(DockPlacement::Left) => side_tabs::chips(group, window, cx),
            Some(DockPlacement::Right) => Empty.into_any_element(),
            _ => self.0.render_tab_bar(group, window, cx),
        }
    }

    fn render_active_panel(
        &self,
        panel: AnyView,
        group: &TabGroupContext,
        window: &mut Window,
        cx: &mut App,
    ) -> AnyElement {
        self.0.render_active_panel(panel, group, window, cx)
    }

    fn render_drop_indicator(
        &self,
        indicator: DropIndicator,
        window: &mut Window,
        cx: &mut App,
    ) -> Option<AnyElement> {
        self.0.render_drop_indicator(indicator, window, cx)
    }

    fn render_empty(
        &self,
        group: &TabGroupContext,
        window: &mut Window,
        cx: &mut App,
    ) -> Option<AnyElement> {
        self.0.render_empty(group, window, cx)
    }
}

/// An open side dock's content, fading in and sliding in from its outer edge. A closed side dock draws no content, so
/// its animation state goes and the next opening plays it again.
fn entering(
    placement: DockPlacement,
    content: AnyElement,
    window: &Window,
    cx: &App,
) -> AnyElement {
    let motion = cx.theme().motion_tokens();
    let travel = f32::from(motion.distance_medium.to_pixels(window.rem_size()));
    let easing = motion.easing_enter.clone();
    let (id, dx) = match placement {
        DockPlacement::Left => ("sessions-enter", -travel),
        _ => ("drawer-enter", travel),
    };
    div()
        .relative()
        .size_full()
        .child(content)
        .with_animation(
            id,
            Animation::new(motion.duration_normal)
                .with_easing(move |t| easing.sample(t).clamp(0., 1.)),
            move |content, t| content.left(px(dx * (1. - t))).opacity(t),
        )
        .into_any_element()
}

/// Which side dock holds the group, if one does. The area isn't being drawn while a group draws its tabs (the group is
/// its own view), so it can be read; the kit's own tab bar reads it the same way.
fn side(area: &WeakEntity<DockArea>, node: NodeId, cx: &App) -> Option<DockPlacement> {
    let area = area.upgrade()?;
    let area = area.read(cx);
    [DockPlacement::Left, DockPlacement::Right]
        .into_iter()
        .find(|dock| {
            area.layout(*dock)
                .is_some_and(|tree| tree.find_node(node).is_some())
        })
}
