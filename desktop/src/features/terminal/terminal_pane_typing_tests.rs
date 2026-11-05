//! Which keys the Terminal tab writes itself and which it leaves for the window's input handler (spec 8): a typed
//! character goes through the input handler, so a dead key or an input method composes it first, and is written once.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::TestAppContext;

use super::{Terminals, following, open};

fn writes(open: &super::Open<'_>) -> Vec<Vec<u8>> {
    (open.terminals.lock().unwrap().of("write").iter())
        .map(|args| STANDARD.decode(args["data"].as_str().unwrap()).unwrap())
        .collect()
}

/// A write went out, or is on its way.
fn sent(open: &mut super::Open<'_>) -> bool {
    !writes(open).is_empty() || open.pane.read_with(open.cx, |pane, _| pane.writing())
}

/// The key goes to the pane's key listener only, as a platform delivers it before any input method; whether the
/// pane let it go on.
fn key_down_propagates(
    open: &mut super::Open<'_>,
    keystroke: &'static str,
    key_char: Option<&str>,
) -> bool {
    let mut keystroke = gpui_kit::Keystroke::parse(keystroke).unwrap();
    keystroke.key_char = key_char.map(String::from);
    let event = gpui_kit::PlatformInput::KeyDown(gpui_kit::KeyDownEvent {
        keystroke,
        is_held: false,
        prefer_character_input: false,
    });
    let result = open
        .cx
        .update(|window, cx| window.dispatch_event(event, cx));
    open.cx.run_until_parked();
    result.propagate
}

/// A composition in progress, from the input method.
fn composing(open: &mut super::Open<'_>, text: &str) {
    (open.pane).update_in(open.cx, |pane, window, cx| {
        gpui_kit::EntityInputHandler::replace_and_mark_text_in_range(
            pane, None, text, None, window, cx,
        )
    });
    open.cx.run_until_parked();
}

/// A finished composition, from the input method.
fn composed(open: &mut super::Open<'_>, text: &str) {
    (open.pane).update_in(open.cx, |pane, window, cx| {
        gpui_kit::EntityInputHandler::replace_text_in_range(pane, None, text, window, cx)
    });
    open.cx.run_until_parked();
}

fn marked(open: &mut super::Open<'_>) -> Option<String> {
    open.pane.read_with(open.cx, |pane, _| pane.marked.clone())
}

#[gpui::test]
async fn a_typed_character_is_left_for_the_input_method_and_written_once(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    // A Mac dead key comes as its spacing accent: the pane leaves it, so the input method can compose it.
    assert!(key_down_propagates(&mut open, "´", Some("´")));
    assert!(key_down_propagates(&mut open, "shift-a", Some("A")));
    let written = sent(&mut open);
    assert!(
        !written,
        "the pane wrote a character the input handler gets"
    );
    composing(&mut open, "´");
    composed(&mut open, "é");
    open.cx.simulate_keystrokes("a");
    open.until("written", |terminals, pane| {
        terminals.of("write").len() == 2 && !pane.writing()
    })
    .await;
    let expected = vec!["é".as_bytes().to_vec(), b"a".to_vec()];
    assert_eq!(writes(&open), expected);
}

#[gpui::test]
async fn control_alt_and_named_keys_stay_with_the_pane_and_end_a_composition(
    cx: &mut TestAppContext,
) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    composing(&mut open, "´");
    assert!(!key_down_propagates(&mut open, "enter", Some("\n")));
    assert!(!key_down_propagates(&mut open, "alt-f", Some("ƒ")));
    assert!(!key_down_propagates(&mut open, "ctrl-c", None));
    open.until("written", |terminals, pane| {
        writes_len(terminals) > 0 && !pane.writing()
    })
    .await;
    open.cx.run_until_parked();
    let all = writes(&open).concat();
    assert_eq!(all, b"\r\x1b\xc6\x92\x03".to_vec());
    assert_eq!(marked(&mut open), None);
}

fn writes_len(terminals: &Terminals) -> usize {
    terminals.of("write").len()
}
