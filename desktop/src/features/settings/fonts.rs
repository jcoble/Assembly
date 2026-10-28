//! The faces Settings offers, from the ones this Mac has installed, read once when the page opens. Code faces are the
//! installed families whose names say they're for code; GPUI doesn't say which faces are monospaced.

use gpui_kit::{App, SharedString};

use crate::core::state::appearance::{DEFAULT_CODE_FONT, SYSTEM_FONT};

/// Words in the name of a face made for code.
const CODE_WORDS: [&str; 6] = ["mono", "code", "consol", "courier", "menlo", "monaco"];

/// The interface faces, the system's first, and the code faces, the kit's default first.
pub fn installed(cx: &App) -> (Vec<SharedString>, Vec<SharedString>) {
    let mut names = cx.text_system().all_font_names();
    names.retain(|name| !name.starts_with('.'));
    names.sort_by_key(|name| name.to_lowercase());
    names.dedup();
    let code = names.iter().filter(|name| {
        let lower = name.to_lowercase();
        name.as_str() != DEFAULT_CODE_FONT && CODE_WORDS.iter().any(|word| lower.contains(word))
    });
    let code = std::iter::once(DEFAULT_CODE_FONT.to_string())
        .chain(code.cloned())
        .map(SharedString::from)
        .collect();
    let ui = std::iter::once(SYSTEM_FONT.to_string())
        .chain(names)
        .map(SharedString::from)
        .collect();
    (ui, code)
}

/// What a face is called in the list: the system's own is "System".
pub fn label(name: &str) -> SharedString {
    if name == SYSTEM_FONT {
        "System".into()
    } else {
        SharedString::from(name.to_string())
    }
}
