//! `PaletteView` owns the open palette: the kit's `Command` (search field and list) over the registry's commands,
//! opened as a kit dialog, so native views step aside while it's open. Choosing a command runs it and closes the
//! palette; Escape or a click outside closes it.

use gpui_kit::component::command::CommandState;
use gpui_kit::*;

pub struct PaletteView {
    /// The query and the highlighted command, kept across renders.
    state: Entity<CommandState>,
}

impl PaletteView {
    /// Binds `secondary-k` (Cmd-K on the Mac, Ctrl-K elsewhere) to `TogglePalette` and adds its handler, which opens
    /// or closes the dialog.
    pub fn init(_cx: &mut App) {
        todo!()
    }

    pub fn new(_window: &mut Window, _cx: &mut Context<Self>) -> Self {
        todo!()
    }
}

impl Render for PaletteView {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
