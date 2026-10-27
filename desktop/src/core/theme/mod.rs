//! The window's theme: which kit theme file is in force, and the glass window background. Replaces mac-command-bar's
//! settings/themes/themeService.ts and the roster half of themeRegistry.ts. The colours live in desktop/themes/.

pub mod apply;
pub mod theme_id;

pub use apply::{init, set};
