//! Loading the theme files and putting one in force. The kit's own folder watch re-reads a changed file and
//! re-applies it, so nothing here watches or re-reads. Replaces themeService.ts's apply().

use std::path::PathBuf;

use gpui_kit::{App, Window};

use super::theme_id::ThemeId;

/// Hands `dir` to the kit's theme folder watch and calls `on_ready` once the files have loaded (the kit calls it once).
/// The window opens in `on_ready` and calls `set` inside its open call, so the first frame is already the theme in
/// force, never the kit's built-in light one.
pub fn init(dir: PathBuf, cx: &mut App, on_ready: impl Fn(&mut App) + 'static) {
    todo!()
}

/// Puts `id` in force: makes its file the kit's active theme, sets the window background to blurred for glass and
/// opaque for the rest, and brings the kit's 280 ms slow step and move spring down to 180 ms.
pub fn set(id: ThemeId, window: &mut Window, cx: &mut App) {
    todo!()
}
