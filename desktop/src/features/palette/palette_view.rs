//! `PaletteView` owns the open palette: the kit's `Command` (search field and list) over the registry's commands.
//! `TogglePalette` (Cmd-K on the Mac, Ctrl-K elsewhere) opens it as a kit dialog, or closes it when it's up.
//! Choosing a command runs it and closes the palette. Escape clears the query first and then closes it, and a click
//! outside closes it; the dialog handles both.

use std::cell::Cell;

use gpui_kit::component::WindowExt;
use gpui_kit::component::command::{Command, CommandState};
use gpui_kit::*;

use super::TogglePalette;
use crate::core::commands::command_registry::CommandRegistry;

pub struct PaletteView {
    /// The query and the highlighted command, kept across renders.
    state: Entity<CommandState>,
}

/// The palette that's up. Only its dialog holds the view, so this stops upgrading once the dialog is gone.
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
            window.close_dialog(cx);
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
            dialog.close_button(false).child(view.clone())
        });
    }
}

impl Render for PaletteView {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        Command::new(&self.state)
            .placeholder("Type a command")
            .items(CommandRegistry::items(cx))
            .on_confirm(|_, window, cx| window.close_dialog(cx))
    }
}

#[cfg(test)]
#[path = "palette_view_tests.rs"]
mod tests;
