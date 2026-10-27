//! `PaletteView` owns the open palette: the kit's `Command` (search field and list) over the registry's commands.
//! `TogglePalette` (Cmd-K on the Mac, Ctrl-K elsewhere) opens it as a kit dialog, or closes it when it's up.
//! Choosing a command runs it and closes the palette. Escape clears the query first and then closes it, and a click
//! outside closes it; the dialog handles both.

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
        cx.bind_keys([KeyBinding::new("secondary-k", TogglePalette, None)]);
        cx.on_action(|_: &TogglePalette, cx| {
            if let Some(window) = cx.active_window() {
                _ = window.update(cx, |_, window, cx| Self::toggle(window, cx));
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
        let view = cx.new(|cx| Self {
            state: cx.new(|cx| CommandState::new(window, cx)),
        });
        let state = view.read(cx).state.clone();
        state.update(cx, |state, cx| state.focus(window, cx));
        cx.set_global(OpenPalette(view.downgrade()));
        window.open_dialog(cx, move |dialog, _, _| {
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
