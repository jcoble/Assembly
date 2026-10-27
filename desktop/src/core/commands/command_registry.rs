//! `CommandRegistry` owns the palette's command list: each command's label and action. Features add theirs at start;
//! the palette builds the kit's command items from them each time it opens (the kit's items don't expose their label
//! or action, so the registry keeps both). The kit shows each action's key binding beside it. Replaces the old app's
//! palette/commandRegistry.ts.

use gpui_kit::*;

pub struct CommandRegistry {
    commands: Vec<(SharedString, Box<dyn Action>)>,
}

impl Global for CommandRegistry {}

impl CommandRegistry {
    pub fn add(_label: impl Into<SharedString>, _action: Box<dyn Action>, _cx: &mut App) {
        todo!()
    }

    pub fn commands(_cx: &App) -> &[(SharedString, Box<dyn Action>)] {
        todo!()
    }
}
