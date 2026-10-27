//! The one module that owns native views: the terminal's Ghostty view and the browser's WKWebView. They sit above
//! everything GPUI draws, so a menu, the palette, a dialog or another tab can't cover them. This module is the only
//! code that makes, shows, hides, places or releases one; the guard (desktop/scripts/guard.sh) fails on a native
//! view API anywhere else. The terminal and browser lanes add their surfaces in this folder, and their features hold
//! only a `NativeViewId`. The rules are g0-host#5 to g0-host#10 in docs/behavior/g0-host.md.

mod native_surface;
pub mod native_view_id;
pub mod native_views;
pub mod view_state;
