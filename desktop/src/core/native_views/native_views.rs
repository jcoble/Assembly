//! `NativeViews` owns every native view in the window and keeps each one hidden or placed, with no timer and no
//! event bookkeeping: it reads the state once a frame.
//!
//! - Each view's pane draws `host(id)`, an empty element that records its bounds as it lays out. A view whose host
//!   didn't lay out this frame is hidden: its tab isn't in front, its dock is closed, another group is zoomed or its
//!   pane is gone (821ec040).
//! - The window's root runs `place_all` once a frame, after every element has laid out. It hides every view while
//!   anything covers the window: a kit dialog or sheet (the palette and settings are dialogs), a popover or menu
//!   (drawn in the kit's deferred layer), or a drag (`cx.has_active_drag()`: tab and dock drags). Otherwise each view
//!   goes to its host's bounds, so it stays inside its pane and moves with it (b0845e27, c65922f6, 893cc7dd,
//!   ba5a652f). A divider resize moves the view with its pane. A surface is called only when its place changes.
//! - A pane's `Panel::on_removed` calls `close`, which hides and releases the view (4f61e404).
//!
//! Kit tooltips, hover cards and context menus have no state to read; the Mac check lists any that still sit under
//! a native view. Replaces the old app's terminal view visibility (terminalService.ts) and its popup observer
//! (ded79246).

use std::collections::HashMap;

use gpui_kit::*;

use super::native_surface::NativeSurface;
use super::native_view_id::NativeViewId;
use super::view_state::ViewState;

pub struct NativeViews {
    views: HashMap<NativeViewId, (ViewState, Box<dyn NativeSurface>)>,
    next_id: u64,
}

impl Global for NativeViews {}

impl NativeViews {
    pub fn init(_cx: &mut App) {
        todo!()
    }

    /// Takes a new surface, hidden until its host lays out.
    pub(super) fn open(_surface: Box<dyn NativeSurface>, _cx: &mut App) -> NativeViewId {
        todo!()
    }

    /// The element a pane draws where its native view goes: empty, filling the pane, recording its bounds.
    pub fn host(_id: NativeViewId) -> impl IntoElement {
        div()
    }

    /// Places or hides every view for this frame; the window's root calls it once a frame, after layout.
    pub fn place_all(_window: &mut Window, _cx: &mut App) {
        todo!()
    }

    /// The view's pane was removed: the view is hidden and released.
    pub fn close(_id: NativeViewId, _window: &mut Window, _cx: &mut App) {
        todo!()
    }
}
