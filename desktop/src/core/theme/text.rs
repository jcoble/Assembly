//! Puts the faces Settings saved on the kit's theme, and holds the window's rem: the interface face for everything the
//! kit draws, and the code face for paths, commands and code outside the editor. A theme file sets its own sizes, so
//! this runs again after every change to the theme. Text sizes come from `TextRole`, not from the rem.

use gpui_kit::component::Theme;
use gpui_kit::{App, Pixels, Styled, px};

use crate::core::state::appearance::UI_FONT_SIZE;
use crate::core::state::preferences::Preferences;

/// The kit draws its controls, and its spacing helpers resolve, against the window's rem, which the kit's root takes from
/// the theme. It's held here, whatever a theme file or a saved size says, so the kit's geometry is always what it was
/// designed at and only the roles below follow the interface size.
pub const WINDOW_REM: Pixels = px(16.);

pub fn apply_text(cx: &mut App) {
    let (ui, code) = Preferences::read(cx, |appearance| (appearance.ui_font(), appearance.code_font()));
    let theme = Theme::global(cx);
    // An update redraws every window, so it's made only when something changed.
    if theme.font_family != ui || theme.font_size != WINDOW_REM || theme.mono_font_family != code {
        Theme::update(cx, |theme| {
            (theme.font_family, theme.font_size, theme.mono_font_family) = (ui, WINDOW_REM, code);
        });
    }
}

/// Every piece of interface text has one of these, sized from the interface size in Settings rather than from the
/// window's rem. The numbers are at an interface size of 16, which is the default, and scale with it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TextRole {
    /// Times, counts, captions, a rail row's second and third lines.
    Meta,
    /// Labels, controls, lists and most of the interface.
    Body,
    /// A rail row's title, a section's title.
    Title,
    /// A pane's or dialog's title.
    Heading,
    /// A page's title.
    Display,
    /// What the chat's messages read in.
    Chat,
}

/// Nothing in the interface reads smaller than this, at any interface size.
const SMALLEST: f32 = 11.;
const DEFAULT_SIZE: f32 = 16.;

impl TextRole {
    pub fn metrics(self, interface_size: f32, line_height: f32) -> (Pixels, Pixels) {
        let base = match self {
            Self::Meta => 13.,
            Self::Body => 14.,
            Self::Title => 15.,
            Self::Chat => 16.,
            Self::Heading => 18.,
            Self::Display => 20.,
        };
        let size = (interface_size * base / DEFAULT_SIZE).max(SMALLEST);
        let leading = if self == Self::Chat { 1.6 } else { line_height };
        (px(size), px(size * leading))
    }
}

/// Puts a role's size and leading on anything the kit or GPUI draws.
pub trait TextRoleExt: Styled + Sized {
    fn text_role(self, role: TextRole, cx: &App) -> Self {
        let (size, height) = Preferences::read(cx, |appearance| appearance.ui_text(role));
        self.text_size(size).line_height(height)
    }
}

impl<T: Styled + Sized> TextRoleExt for T {}
