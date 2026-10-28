//! `key_bytes` owns what a key press sends the program, as xterm sends it: typed text as is, Ctrl with a letter as
//! its control code (Ctrl-C included, terminal#2), and the arrows, Home, End, the page keys and F1 to F12 as their
//! escape sequences, in cursor-key application mode when the program asked for it. Cmd's keys stay the window's.
//! Replaces the key handling the old app left to its terminal view.

use gpui_kit::Keystroke;

pub fn key_bytes(keystroke: &Keystroke, app_cursor: bool) -> Option<Vec<u8>> {
    let m = &keystroke.modifiers;
    if m.platform {
        return None;
    }
    // xterm's modifier parameter: 1, plus 1 for Shift, 2 for Alt, 4 for Ctrl.
    let modifier = 1 + m.shift as u8 + 2 * m.alt as u8 + 4 * m.control as u8;
    let cursor = |end: char| -> Vec<u8> {
        if modifier > 1 {
            format!("\x1b[1;{modifier}{end}").into_bytes()
        } else if app_cursor {
            format!("\x1bO{end}").into_bytes()
        } else {
            format!("\x1b[{end}").into_bytes()
        }
    };
    let tilde = |code: u8| -> Vec<u8> {
        if modifier > 1 {
            format!("\x1b[{code};{modifier}~").into_bytes()
        } else {
            format!("\x1b[{code}~").into_bytes()
        }
    };
    let key = keystroke.key.as_str();
    let bytes = match key {
        "enter" => b"\r".to_vec(),
        "backspace" if m.alt => b"\x1b\x7f".to_vec(),
        "backspace" if m.control => b"\x08".to_vec(),
        "backspace" => b"\x7f".to_vec(),
        "tab" if m.shift => b"\x1b[Z".to_vec(),
        "tab" => b"\t".to_vec(),
        "escape" => b"\x1b".to_vec(),
        "space" if m.control => vec![0],
        "up" => cursor('A'),
        "down" => cursor('B'),
        "right" => cursor('C'),
        "left" => cursor('D'),
        "home" => cursor('H'),
        "end" => cursor('F'),
        "insert" => tilde(2),
        "delete" => tilde(3),
        "pageup" => tilde(5),
        "pagedown" => tilde(6),
        "f1" => b"\x1bOP".to_vec(),
        "f2" => b"\x1bOQ".to_vec(),
        "f3" => b"\x1bOR".to_vec(),
        "f4" => b"\x1bOS".to_vec(),
        "f5" => tilde(15),
        "f6" => tilde(17),
        "f7" => tilde(18),
        "f8" => tilde(19),
        "f9" => tilde(20),
        "f10" => tilde(21),
        "f11" => tilde(23),
        "f12" => tilde(24),
        _ if m.control => control(key)?,
        _ => {
            let text = keystroke
                .key_char
                .as_deref()
                .or((key == "space").then_some(" "))?;
            // Alt with a plain key is Meta (Esc first), as xterm's metaSendsEscape; a key Alt composes into another
            // character (Option on a Mac) is typed as that character.
            if m.alt && text == key {
                [b"\x1b", text.as_bytes()].concat()
            } else {
                text.as_bytes().to_vec()
            }
        }
    };
    Some(bytes)
}

/// Ctrl with a letter or one of `@[\]^_` (and `2`, `/` as xterm reads them) is that control code.
fn control(key: &str) -> Option<Vec<u8>> {
    let &[c] = key.as_bytes() else {
        return None;
    };
    let code = match c {
        b'a'..=b'z' => c - b'a' + 1,
        b'@' | b'2' => 0,
        b'[' => 0x1b,
        b'\\' => 0x1c,
        b']' => 0x1d,
        b'^' | b'6' => 0x1e,
        b'_' | b'-' | b'/' => 0x1f,
        _ => return None,
    };
    Some(vec![code])
}

#[cfg(test)]
#[path = "keys_tests.rs"]
mod tests;
