//! `CommandRegistry` owns the palette's command list: each command's label and action, in the order features added
//! them. A second command with a label already listed is left out, with a line on stderr, so the first owner keeps
//! it. The palette builds the kit's command items from it each time it draws (the kit's items don't expose their label
//! or action, so the registry keeps both), and the kit shows each action's key binding beside it. Replaces the old
//! app's palette/commandRegistry.ts.
use gpui_kit::component::command::CommandItem;
use gpui_kit::*;

#[derive(Default)]
pub struct CommandRegistry {
    commands: Vec<(SharedString, Box<dyn Action>)>,
}

impl Global for CommandRegistry {}

impl CommandRegistry {
    pub fn add(label: impl Into<SharedString>, action: Box<dyn Action>, cx: &mut App) {
        cx.default_global::<Self>().push(label.into(), action);
    }

    pub fn items(cx: &App) -> Vec<CommandItem> {
        cx.try_global::<Self>()
            .map_or_else(Vec::new, Self::to_items)
    }

    fn push(&mut self, label: SharedString, action: Box<dyn Action>) {
        if self.commands.iter().any(|(listed, _)| *listed == label) {
            eprintln!(
                "the command \"{label}\" is already in the palette, so the second one is left out"
            );
        } else {
            self.commands.push((label, action));
        }
    }

    fn to_items(&self) -> Vec<CommandItem> {
        self.commands
            .iter()
            .map(|(label, action)| {
                CommandItem::new()
                    .label(label.clone())
                    .action(action.boxed_clone())
            })
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use gpui_kit::{Action, actions};

    use super::CommandRegistry;

    actions!(registry_test, [First, Second]);

    // Pins: g0-host#12
    #[test]
    fn commands_keep_their_order_and_the_first_owner_of_a_label() {
        let mut registry = CommandRegistry::default();
        registry.push("Restart backend".into(), Box::new(First));
        registry.push("Open settings".into(), Box::new(Second));
        registry.push("Restart backend".into(), Box::new(Second));
        let listed: Vec<_> = registry
            .commands
            .iter()
            .map(|(label, action)| (label.to_string(), action.name()))
            .collect();
        assert_eq!(
            listed,
            [
                ("Restart backend".to_string(), First.name()),
                ("Open settings".to_string(), Second.name())
            ]
        );
    }
}
