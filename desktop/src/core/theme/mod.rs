//! The window's theme: which kit theme file is in force, the glass window background, and the fade between themes.
//! Replaces mac-command-bar's settings/themes/themeService.ts and the roster half of themeRegistry.ts. The colours
//! live in desktop/themes/.

pub mod apply;
pub mod fade;
#[cfg(test)]
mod redraw_tests;
pub mod theme_id;

pub use apply::{THEMES_DIR, init, set};
