//! The faces Settings offers, from the ones this Mac has installed, read once when the page opens.

use gpui_kit::{App, SharedString};

use crate::core::state::appearance::{DEFAULT_CODE_FONT, SYSTEM_FONT};

/// The installed faces twice: for the interface with the system's first, and for code with the kit's default first.
pub fn installed(cx: &App) -> (Vec<SharedString>, Vec<SharedString>) {
    let mut names = cx.text_system().all_font_names();
    names.retain(|name| !name.starts_with('.'));
    names.sort_by_key(|name| name.to_lowercase());
    names.dedup();
    let list = |first: &str| {
        std::iter::once(first)
            .chain(
                names
                    .iter()
                    .map(String::as_str)
                    .filter(|name| *name != first),
            )
            .map(|name| SharedString::from(name.to_string()))
            .collect()
    };
    (list(SYSTEM_FONT), list(DEFAULT_CODE_FONT))
}

/// What a face is called in the list: the system's own is "System".
pub fn label(name: &str) -> SharedString {
    if name == SYSTEM_FONT {
        "System".into()
    } else {
        SharedString::from(name.to_string())
    }
}
