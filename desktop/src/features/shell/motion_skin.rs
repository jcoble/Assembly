//! `MotionSkin` owns the docks' opening motion: it wraps the kit's DockSkin and changes only how an open dock's content
//! is drawn, fading it in and sliding it in from the dock's outer edge. Every other piece of the skin passes through,
//! the drag edge and its save on release included. The dock's width still appears at once, and closing is instant: a
//! width slide would need a change to the kit. With Reduce Motion on, the kit's animations jump to the end. New: the
//! old app had no dock motion.

use std::rc::Rc;
use std::sync::Arc;

use gpui_kit::base::ResizeHandleContext;
use gpui_kit::component::ActiveTheme as _;
use gpui_kit::component::dock::{
    BasePanelView, DockAreaRenderer, DockContext, DockPlacement, DockSkin, NodeId, PanelState,
    TabGroupRenderer,
};
use gpui_kit::*;

pub struct MotionSkin(pub Rc<DockSkin>);

impl DockAreaRenderer for MotionSkin {
    fn frame(&self, window: &mut Window, cx: &mut App) -> Stateful<Div> {
        self.0.frame(window, cx)
    }

    fn split_frame(
        &self,
        node: NodeId,
        axis: Axis,
        window: &mut Window,
        cx: &mut App,
    ) -> Stateful<Div> {
        self.0.split_frame(node, axis, window, cx)
    }

    fn center_frame(&self, window: &mut Window, cx: &mut App) -> Stateful<Div> {
        self.0.center_frame(window, cx)
    }

    fn render_split_handle(
        &self,
        handle: &ResizeHandleContext,
        window: &mut Window,
        cx: &mut App,
    ) -> Option<AnyElement> {
        self.0.render_split_handle(handle, window, cx)
    }

    fn render_dock(
        &self,
        dock: &DockContext,
        content: AnyElement,
        window: &mut Window,
        cx: &mut App,
    ) -> AnyElement {
        // A closed bottom dock still shows its tab bar, so only the side docks slide in: a closed side dock draws nothing.
        let content = match dock.placement() {
            DockPlacement::Left | DockPlacement::Right if dock.is_open() => {
                entering(dock.placement(), content, window, cx)
            }
            _ => content,
        };
        self.0.render_dock(dock, content, window, cx)
    }

    fn build_placeholder(
        &self,
        state: &PanelState,
        window: &mut Window,
        cx: &mut App,
    ) -> Option<Arc<dyn BasePanelView>> {
        self.0.build_placeholder(state, window, cx)
    }

    fn tab_group_renderer(&self) -> Rc<dyn TabGroupRenderer> {
        self.0.tab_group_renderer()
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
