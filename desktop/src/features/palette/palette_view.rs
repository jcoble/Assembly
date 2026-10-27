//! `PaletteView` owns the open palette: the kit's `Command` (search field and list) over the registry's commands.
//! `TogglePalette` (Cmd-K on the Mac, Ctrl-K elsewhere) opens it as a kit dialog, or closes it when it's up.
//! Choosing a command runs it and closes the palette. Escape clears the query first and then closes it, and a click
//! outside closes it; the dialog handles both.

use std::cell::Cell;

use gpui_kit::component::command::{Command, CommandState};
use gpui_kit::component::{ActiveTheme as _, WindowExt};
use gpui_kit::*;

use super::TogglePalette;
use crate::core::commands::command_registry::CommandRegistry;

/// The palette's corners, the design system's `radius-md` for floating menus.
const EDGE_RADIUS: Pixels = px(12.);
/// The search box's corners inside it: a card inside a card takes the next radius down (`radius-sm`).
const INNER_RADIUS: Pixels = px(8.);

pub struct PaletteView {
    /// The query and the highlighted command, kept across renders.
    state: Entity<CommandState>,
}

/// The palette that's up. Cleared as its dialog starts closing, not when the fade ends, so a Cmd-K during the fade
/// opens a new palette instead of closing whatever dialog is under it.
struct OpenPalette(WeakEntity<PaletteView>);

impl Global for OpenPalette {}

impl PaletteView {
    /// Binds `secondary-k` to `TogglePalette` and handles it in whichever window is active.
    pub fn init(cx: &mut App) {
        let toggle = KeyBinding::load(
            "secondary-k",
            Box::new(TogglePalette),
            None,
            false,
            None,
            cx,
        );
        cx.bind_keys([toggle.expect("the palette's key binding parses")]);
        cx.on_action(|_: &TogglePalette, cx| {
            // A key press runs this while its window is being updated, so the window is updated once that's done.
            if let Some(window) = cx.active_window() {
                cx.defer(move |cx| _ = window.update(cx, |_, window, cx| Self::toggle(window, cx)));
            }
        });
    }

    fn toggle(window: &mut Window, cx: &mut App) {
        if cx
            .try_global::<OpenPalette>()
            .is_some_and(|open| open.0.upgrade().is_some())
        {
            Self::close(window, cx);
            return;
        }
        let state = cx.new(|cx| CommandState::new(window, cx));
        let view = cx.new(|_| Self {
            state: state.clone(),
        });
        cx.set_global(OpenPalette(view.downgrade()));
        // The dialog takes focus when it opens, so the search field takes it on the dialog's first draw.
        let first_draw = Cell::new(true);
        window.open_dialog(cx, move |dialog, window, cx| {
            if first_draw.replace(false) {
                state.update(cx, |state, cx| state.focus(window, cx));
            }
            // Escape and a click outside close the dialog without asking the palette.
            dialog
                .close_button(false)
                // An edge that reads against the dimmed window: the theme's text colour at 20%, about #3a3a3a on
                // Assembly.
                .rounded(EDGE_RADIUS)
                .border_color(cx.theme().foreground.alpha(0.2))
                .child(view.clone())
                .on_close(|_, _, cx| Self::closing(cx))
        });
    }

    fn close(window: &mut Window, cx: &mut App) {
        Self::closing(cx);
        window.close_dialog(cx);
    }

    fn closing(cx: &mut App) {
        cx.set_global(OpenPalette(WeakEntity::new_invalid()));
    }
}

impl Render for PaletteView {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        Command::new(&self.state)
            .rounded(INNER_RADIUS)
            .placeholder("Type a command")
            .items(CommandRegistry::items(cx))
            .on_confirm(|_, window, cx| Self::close(window, cx))
    }
}

#[cfg(test)]
#[path = "palette_view_tests.rs"]
mod tests;
