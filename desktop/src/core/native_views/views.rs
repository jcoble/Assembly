//! `NativeViews` owns every native view in the window and keeps each one hidden or placed, from the window's state
//! once a frame.
//!
//! - Each view's pane draws `host(id)`, an empty element that records its bounds, clipped to what's visible, when it
//!   lays out. The dock reuses an unchanged pane's last layout without running its host, so a view keeps its last
//!   bounds until the dock changes (the shell's Frame observes it). Then `layout_changed` forgets every view's bounds and has the window
//!   draw every pane afresh: a view whose host doesn't lay out (its tab isn't in front, its dock is closed, another
//!   group is zoomed, its pane is gone) is hidden (821ec040).
//! - The window draws `placer()` once; after every element has laid out, it places or hides every view. All views
//!   hide while anything covers the window: a kit dialog or sheet (the palette and settings are dialogs) and its
//!   fade-out, a popover or menu (the kit's deferred layer), or a drag (`cx.has_active_drag()`: tab, dock and divider
//!   drags). Otherwise each view goes to its host's bounds, so it stays inside its pane and moves with it (b0845e27,
//!   c65922f6, 893cc7dd, ba5a652f). A surface is called only when its place changes.
//! - A pane's `Panel::on_removed` calls `close`, which hides and releases the view (4f61e404). The terminal also
//!   closes its view on every session switch; the backend's terminal keeps everything until the session comes back.
//!
//! Kit tooltips, hover cards and context menus have no state to read; the Mac check lists any that still sit under
//! a native view. Replaces the old app's terminal view visibility (terminalService.ts) and its popup observer
//! (ded79246).

use std::collections::HashMap;
use std::time::Instant;

use gpui_kit::base::{ElementExt, GlobalState};
use gpui_kit::component::WindowExt;
use gpui_kit::component::dialog::ANIMATION_DURATION;
use gpui_kit::*;

use super::native_surface::NativeSurface;
use super::native_view_id::NativeViewId;
use super::view_state::ViewState;

#[derive(Default)]
pub struct NativeViews {
    views: HashMap<NativeViewId, (ViewState, Box<dyn NativeSurface>)>,
    next_id: u64,
    /// A dialog or sheet was up in the last frame.
    dialog_up: bool,
    /// Until when a closed dialog's exit animation still covers the window.
    dialog_leaving_until: Option<Instant>,
    /// Draws a frame once that exit animation ends, so the views come back.
    _after_dialog: Option<Task<()>>,
}

impl Global for NativeViews {}

impl NativeViews {
    /// Takes a new surface, hidden until its host lays out.
    #[allow(dead_code, reason = "the terminal and browser surfaces open views")]
    pub(super) fn open(&mut self, mut surface: Box<dyn NativeSurface>) -> NativeViewId {
        surface.place(None);
        self.next_id += 1;
        let id = NativeViewId(self.next_id);
        self.views.insert(id, (ViewState::default(), surface));
        id
    }

    /// The element a pane draws where its native view goes: empty, filling the pane, recording its bounds.
    #[allow(dead_code, reason = "the terminal and browser panes draw it")]
    pub fn host(id: NativeViewId) -> impl IntoElement {
        div().size_full().on_prepaint(move |bounds, window, cx| {
            let visible = bounds.intersect(&window.content_mask().bounds);
            if let Some((view, _)) = cx.default_global::<Self>().views.get_mut(&id) {
                view.laid_out(visible);
            }
        })
    }

    /// The dock's layout changed (a tab chosen, a pane moved, closed or zoomed, a dock opened or closed): every view
    /// hides unless its host lays out again, and the window draws every pane afresh so each shown host does. The shell
    /// calls it on the dock's `LayoutChanged`.
    pub fn layout_changed(window: &mut Window, cx: &mut App) {
        for (view, _) in cx.default_global::<Self>().views.values_mut() {
            view.forget();
        }
        window.refresh();
    }

    /// The element the window draws once, which places every view after layout.
    pub fn placer() -> impl IntoElement {
        canvas(
            |_, _, _| {},
            |_, _, window, cx| {
                let dialog = window.has_active_dialog(cx) || window.has_active_sheet(cx);
                let leaving = Self::dialog_leaving(dialog, window, cx);
                let covered = dialog
                    || leaving
                    || GlobalState::is_in_deferred_context(cx)
                    || cx.has_active_drag();
                cx.default_global::<Self>().end_frame(covered);
            },
        )
    }

    /// True while a dialog that just closed is still fading out (the kit skips closing dialogs in
    /// `has_active_dialog`). When one closes, a frame is drawn once its fade ends, to bring the views back.
    fn dialog_leaving(dialog: bool, window: &mut Window, cx: &mut App) -> bool {
        let this = cx.default_global::<Self>();
        let closed = this.dialog_up && !dialog;
        this.dialog_up = dialog;
        if closed {
            this.dialog_leaving_until = Some(Instant::now() + *ANIMATION_DURATION);
            let after = window.spawn(cx, async move |cx| {
                cx.background_executor().timer(*ANIMATION_DURATION).await;
                _ = cx.update(|window, _| window.refresh());
            });
            cx.default_global::<Self>()._after_dialog = Some(after);
        }
        let this = cx.default_global::<Self>();
        this.dialog_leaving_until
            .is_some_and(|until| Instant::now() < until)
    }

    /// The view's pane was removed: the view is hidden and released.
    #[allow(dead_code, reason = "the terminal and browser panes close their views")]
    pub fn close(id: NativeViewId, cx: &mut App) {
        cx.default_global::<Self>().remove(id);
    }

    fn remove(&mut self, id: NativeViewId) {
        if let Some((_, mut surface)) = self.views.remove(&id) {
            surface.place(None);
            surface.release();
        }
    }

    fn end_frame(&mut self, covered: bool) {
        for (view, surface) in self.views.values_mut() {
            if let Some(frame) = view.end_frame(covered) {
                surface.place(frame);
            }
        }
    }
}

#[cfg(test)]
#[path = "views_tests.rs"]
mod tests;
