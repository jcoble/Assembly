//! `NativeViewId` is how a feature names its native view to `NativeViews`; it never holds the view itself.

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub struct NativeViewId(pub(super) u64);
