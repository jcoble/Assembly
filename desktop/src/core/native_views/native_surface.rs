//! `NativeSurface` is a native view as this module drives it. Only surfaces in this folder implement it, and only
//! `NativeViews` calls it. Remaking the terminal in a new theme's colors is the terminal surface's own job.

use gpui_kit::*;

pub(super) trait NativeSurface {
    /// Shows the view at `frame` in window coordinates, or hides it and frees its drawing surface when `None`.
    fn place(&mut self, frame: Option<Bounds<Pixels>>, window: &mut Window);

    /// Destroys the view; nothing is drawn or kept after.
    fn release(self: Box<Self>, window: &mut Window);
}
