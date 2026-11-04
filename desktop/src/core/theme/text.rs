//! Puts the faces Settings saved on the kit's theme, and holds the window's rem: the interface face for everything the
//! kit draws, and the code face for paths, commands and code outside the editor. A theme file sets its own sizes, so
//! this runs again after every change to the theme. Text sizes come from `TextRole`, not from the rem.

use gpui_kit::component::Theme;
use gpui_kit::{App, Global, Pixels, SharedString, Styled, px};

use crate::core::state::appearance::UI_FONT_SIZE;
use crate::core::state::preferences::Preferences;

/// The kit draws its controls, and its spacing helpers resolve, against the window's rem, which the kit's root takes from
/// the theme. It's held here, whatever a theme file or a saved size says, so the kit's geometry is always what it was
/// designed at and only the roles below follow the interface size.
pub const WINDOW_REM: Pixels = px(16.);

/// A face asked for, and what the kit named on the theme for it. The kit swaps a generic name such as the system font
/// for the installed family it resolves to, where it can. That isn't a change to put right: writing the generic name
/// back makes the kit swap it again, and each write wakes this again, forever.
type NamedFace = (SharedString, SharedString);

#[derive(Default)]
struct NamedFaces {
    ui: Option<NamedFace>,
    code: Option<NamedFace>,
}

impl Global for NamedFaces {}

fn in_force(on_theme: &SharedString, asked: &SharedString, named: &Option<NamedFace>) -> bool {
    on_theme == asked || named.as_ref().is_some_and(|named| named.0 == *asked && named.1 == *on_theme)
}

/// The interface size text was last drawn at.
struct DrawnSize(f32);

impl Global for DrawnSize {}

/// Text sizes aren't on the theme, and a view that doesn't observe the preferences, or that the dock caches, keeps the
/// old ones until it's invalidated.
fn redraw_for_a_new_size(cx: &mut App) {
    let size = Preferences::read(cx, |appearance| appearance.number(&UI_FONT_SIZE));
    let drawn = cx.try_global::<DrawnSize>().map(|drawn| drawn.0);
    if drawn == Some(size) {
        return;
    }
    cx.set_global(DrawnSize(size));
    if drawn.is_some() {
        cx.refresh_windows();
    }
}

pub fn apply_text(cx: &mut App) {
    redraw_for_a_new_size(cx);
    let (ui, code) = Preferences::read(cx, |appearance| (appearance.ui_font(), appearance.code_font()));
    let theme = Theme::global(cx);
    let named = cx.try_global::<NamedFaces>();
    let (named_ui, named_code) = named.map_or((&None, &None), |named| (&named.ui, &named.code));
    // An update redraws every window, so it's made only when something changed.
    if in_force(&theme.font_family, &ui, named_ui)
        && in_force(&theme.mono_font_family, &code, named_code)
        && theme.font_size == WINDOW_REM
    {
        return;
    }
    Theme::update(cx, |theme| {
        (theme.font_family, theme.font_size, theme.mono_font_family) =
            (ui.clone(), WINDOW_REM, code.clone());
    });
    let theme = Theme::global(cx);
    let faces = NamedFaces {
        ui: Some((ui, theme.font_family.clone())),
        code: Some((code, theme.mono_font_family.clone())),
    };
    cx.set_global(faces);
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
