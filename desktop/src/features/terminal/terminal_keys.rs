//! What a key or a paste sends to the program, as xterm sends it: the escapes for named keys, with their modifiers,
//! control characters, and pastes.

use gpui_kit::Keystroke;

/// A paste as xterm sends it: line breaks as Return, and between the bracketed-paste marks when the program asked for
/// them, so a shell or an agent takes several lines as one paste rather than running each.
pub fn pasted(text: &str, bracketed: bool) -> Vec<u8> {
    let text = text.replace("\r\n", "\r").replace('\n', "\r");
    match bracketed {
        true => format!("\x1b[200~{text}\x1b[201~").into_bytes(),
        false => text.into_bytes(),
    }
}

/// The bytes a key sends to the program, as xterm sends them; none for a key the window keeps (a Cmd shortcut).
pub fn key_bytes(keystroke: &Keystroke, app_cursor: bool) -> Option<Vec<u8>> {
    let modifiers = keystroke.modifiers;
    if modifiers.platform {
        return None;
    }
    // xterm's modifier number: 1, plus 1 for Shift, 2 for Alt and 4 for Control.
    let code = 1
        + u8::from(modifiers.shift)
        + 2 * u8::from(modifiers.alt)
        + 4 * u8::from(modifiers.control);
    let key = keystroke.key.as_str();
    let escape = |text: &str| match modifiers.alt {
        true => format!("\x1b{text}").into_bytes(),
        false => text.as_bytes().to_vec(),
    };
    // Keys whose escape ends in a letter: the arrows, Home, End and F1 to F4.
    let letter = match key {
        "up" => Some('A'),
        "down" => Some('B'),
        "right" => Some('C'),
        "left" => Some('D'),
        "home" => Some('H'),
        "end" => Some('F'),
        "f1" => Some('P'),
        "f2" => Some('Q'),
        "f3" => Some('R'),
        "f4" => Some('S'),
        _ => None,
    };
    if let Some(letter) = letter {
        let function = key.starts_with('f') && key.len() == 2;
        return Some(
            match code {
                1 if function || app_cursor => format!("\x1bO{letter}"),
                1 => format!("\x1b[{letter}"),
                _ => format!("\x1b[1;{code}{letter}"),
            }
            .into_bytes(),
        );
    }
    // Keys whose escape is a number and a tilde.
    let number = match key {
        "insert" => Some(2),
        "delete" => Some(3),
        "pageup" => Some(5),
        "pagedown" => Some(6),
        "f5" => Some(15),
        "f6" => Some(17),
        "f7" => Some(18),
        "f8" => Some(19),
        "f9" => Some(20),
        "f10" => Some(21),
        "f11" => Some(23),
        "f12" => Some(24),
        _ => None,
    };
    if let Some(number) = number {
        return Some(
            match code {
                1 => format!("\x1b[{number}~"),
                _ => format!("\x1b[{number};{code}~"),
            }
            .into_bytes(),
        );
    }
    match key {
        "enter" => return Some(escape("\r")),
        "tab" if modifiers.shift => return Some(b"\x1b[Z".to_vec()),
        "tab" => return Some(escape("\t")),
        "escape" => return Some(escape("\x1b")),
        "backspace" if modifiers.control => return Some(escape("\x08")),
        "backspace" => return Some(escape("\x7f")),
        "space" if modifiers.control => return Some(escape("\0")),
        "space" => return Some(escape(" ")),
        _ => {}
    }
    if modifiers.control {
        let control = match key.as_bytes() {
            [letter @ b'a'..=b'z'] => Some(letter - b'a' + 1),
            [b'@'] => Some(0),
            [b'['] => Some(0x1b),
            [b'\\'] => Some(0x1c),
            [b']'] => Some(0x1d),
            [b'^'] => Some(0x1e),
            [b'_' | b'/'] => Some(0x1f),
            _ => None,
        };
        if let Some(control) = control {
            return Some(escape(&char::from(control).to_string()));
        }
    }
    Some(escape(keystroke.key_char.as_deref()?))
}

#[cfg(test)]
#[path = "terminal_keys_tests.rs"]
mod tests;
