//! Puts the faces and the interface size Settings saved on the kit's theme: the interface face and size for everything
//! the kit draws, and the code face for paths, commands and code outside the editor. A theme file sets its own sizes,
//! so this runs again after every theme switch.

use gpui_kit::component::Theme;
use gpui_kit::{App, px};

use crate::core::state::appearance::UI_FONT_SIZE;
use crate::core::state::preferences::Preferences;

pub fn apply_text(cx: &mut App) {
    let (ui, size, code) = Preferences::read(cx, |appearance| {
        let size = px(appearance.number(&UI_FONT_SIZE));
        (appearance.ui_font(), size, appearance.code_font())
    });
    let theme = Theme::global(cx);
    // An update redraws every window, so it's made only when something changed.
    if theme.font_family != ui || theme.font_size != size || theme.mono_font_family != code {
        Theme::update(cx, |theme| {
            (theme.font_family, theme.font_size, theme.mono_font_family) = (ui, size, code);
        });
    }
}
