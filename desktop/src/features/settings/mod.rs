//! Settings: the page that sets how the window looks and how code reads, and lists the machines. It opens from the
//! Home button's menu, the palette and Cmd-, (Ctrl-, elsewhere), all through the `OpenSettings` intent. Replaces
//! mac-command-bar's shell/settings/.

mod connections;
mod fonts;
mod machine_edits;
mod page_content;
mod row;
mod search;
mod section;
mod settings_page;

use gpui_kit::*;

use crate::core::commands::command_registry::CommandRegistry;
use crate::core::intents::open_settings::OpenSettings;
use settings_page::SettingsPage;

/// Binds `secondary-,` to `OpenSettings`, lists it in the palette, and opens the page in the active window.
pub fn init(cx: &mut App) {
    let open = KeyBinding::load("secondary-,", Box::new(OpenSettings), None, false, None, cx);
    cx.bind_keys([open.expect("the settings key binding parses")]);
    cx.on_action(|_: &OpenSettings, cx| {
        // A key press or a menu click runs this while its window is being updated, so the window is updated once
        // that's done.
        if let Some(window) = cx.active_window() {
            cx.defer(move |cx| {
                _ = window.update(cx, |_, window, cx| SettingsPage::open(window, cx))
            });
        }
    });
    CommandRegistry::add("Open settings", Box::new(OpenSettings), cx);
}
