//! Loading the theme files and putting one in force. The kit's own folder watch re-reads a changed file and
//! re-applies it, so nothing here watches or re-reads. Replaces themeService.ts's apply().

use std::path::PathBuf;

use gpui_kit::{App, Window};

use super::theme_id::ThemeId;

/// Hands `dir` to the kit's theme folder watch and, once the files have loaded, puts `id` in force and calls
/// `on_ready`, where the window opens, so it never shows the kit's built-in light theme first.
pub fn init(dir: PathBuf, id: ThemeId, cx: &mut App, on_ready: impl FnOnce(&mut App) + 'static) {
    todo!()
}

/// Puts `id` in force: makes its file the kit's active theme and sets the window background to blurred for glass,
/// opaque for the rest.
pub fn set(id: ThemeId, window: &mut Window, cx: &mut App) {
    todo!()
}
