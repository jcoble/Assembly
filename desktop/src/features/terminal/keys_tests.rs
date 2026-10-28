use gpui_kit::{Keystroke, Modifiers};

use super::key_bytes;

fn key(key: &str, key_char: Option<&str>, modifiers: Modifiers) -> Keystroke {
    Keystroke {
        modifiers,
        key: key.into(),
        key_char: key_char.map(Into::into),
    }
}

fn typed(text: &str) -> Keystroke {
    key(text, Some(text), Modifiers::none())
}

// Pins: terminal#2
#[test]
fn typed_text_and_ctrl_c_reach_the_program() {
    assert_eq!(key_bytes(&typed("a"), false), Some(b"a".to_vec()));
    assert_eq!(
        key_bytes(&typed("\u{e9}"), false),
        Some("\u{e9}".as_bytes().to_vec())
    );
    assert_eq!(
        key_bytes(&key("c", None, Modifiers::control()), false),
        Some(vec![3])
    );
    assert_eq!(
        key_bytes(&key("enter", None, Modifiers::none()), false),
        Some(b"\r".to_vec())
    );
    assert_eq!(
        key_bytes(&key("backspace", None, Modifiers::none()), false),
        Some(vec![0x7f])
    );
}

// Pins: terminal#2
#[test]
fn arrows_follow_the_programs_cursor_key_mode_and_carry_modifiers() {
    let up = key("up", None, Modifiers::none());
    assert_eq!(key_bytes(&up, false), Some(b"\x1b[A".to_vec()));
    assert_eq!(key_bytes(&up, true), Some(b"\x1bOA".to_vec()));
    let word = key("left", None, Modifiers::alt());
    assert_eq!(key_bytes(&word, true), Some(b"\x1b[1;3D".to_vec()));
    assert_eq!(
        key_bytes(&key("delete", None, Modifiers::none()), false),
        Some(b"\x1b[3~".to_vec())
    );
}

// Pins: terminal#2
#[test]
fn alt_is_meta_and_cmd_keys_stay_the_windows() {
    assert_eq!(
        key_bytes(&key("b", Some("b"), Modifiers::alt()), false),
        Some(b"\x1bb".to_vec())
    );
    assert_eq!(
        key_bytes(&key("s", Some("\u{df}"), Modifiers::alt()), false),
        Some("\u{df}".as_bytes().to_vec())
    );
    assert_eq!(
        key_bytes(&key("v", None, Modifiers::command()), false),
        None
    );
    assert_eq!(
        key_bytes(&key("shift", None, Modifiers::shift()), false),
        None
    );
}
