//! The bytes keys send, as xterm sends them.

use gpui_kit::Keystroke;

use super::key_bytes;

fn sent(keys: &'static str) -> Vec<u8> {
    let keystroke = Keystroke::parse(keys).unwrap();
    key_bytes(&keystroke, false).unwrap_or_default()
}

#[test]
fn function_keys_and_insert_send_their_escapes() {
    assert_eq!(sent("f1"), b"\x1bOP");
    assert_eq!(sent("f4"), b"\x1bOS");
    assert_eq!(sent("f5"), b"\x1b[15~");
    assert_eq!(sent("f12"), b"\x1b[24~");
    assert_eq!(sent("insert"), b"\x1b[2~");
}

#[test]
fn modifiers_go_into_the_escape() {
    assert_eq!(sent("ctrl-left"), b"\x1b[1;5D");
    assert_eq!(sent("alt-right"), b"\x1b[1;3C");
    assert_eq!(sent("shift-up"), b"\x1b[1;2A");
    assert_eq!(sent("ctrl-shift-home"), b"\x1b[1;6H");
    assert_eq!(sent("shift-f5"), b"\x1b[15;2~");
    assert_eq!(sent("ctrl-delete"), b"\x1b[3;5~");
    assert_eq!(sent("ctrl-f1"), b"\x1b[1;5P");
}

#[test]
fn control_with_a_symbol_sends_its_control_character() {
    assert_eq!(sent("ctrl-space"), b"\x00");
    assert_eq!(sent("ctrl-["), b"\x1b");
    assert_eq!(sent("ctrl-\\"), b"\x1c");
    assert_eq!(sent("ctrl-]"), b"\x1d");
    assert_eq!(sent("ctrl-/"), b"\x1f");
    assert_eq!(sent("ctrl-alt-c"), b"\x1b\x03");
    assert_eq!(sent("ctrl-backspace"), b"\x08");
    assert_eq!(sent("alt-backspace"), b"\x1b\x7f");
}
