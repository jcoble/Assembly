//! What the Terminal tab sends the program besides typed keys: pastes and focus changes, and taking the focus when its
//! shell opens.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::TestAppContext;
use gpui_kit::ClipboardItem;
use serde_json::json;

use super::{Terminals, following, open};

#[gpui::test]
async fn a_paste_is_bracketed_when_the_program_asks(cx: &mut TestAppContext) {
    let on = STANDARD.encode(b"\x1b[?2004h");
    let terminals = Terminals {
        rows: vec![json!({"seq": 8, "prevSeq": 0, "data": on})],
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    open.until("bracketed paste on", |_, pane| {
        following(pane) && pane.grid.bracketed_paste()
    })
    .await;
    let focus = open.pane.read_with(open.cx, |pane, _| pane.focus.clone());
    open.cx.update(|window, cx| {
        window.focus(&focus, cx);
        cx.write_to_clipboard(ClipboardItem::new_string("one\ntwo".into()));
    });
    open.cx.simulate_keystrokes("ctrl-shift-v");
    open.until("pasted", |terminals, pane| {
        terminals.of("write").len() == 1 && !pane.writing()
    })
    .await;
    let written = open.terminals.lock().unwrap().of("write")[0]["data"].clone();
    let written = STANDARD.decode(written.as_str().unwrap()).unwrap();
    assert_eq!(written, b"\x1b[200~one\rtwo\x1b[201~".to_vec());
}

#[gpui::test]
async fn the_pane_takes_the_focus_when_its_shell_opens(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    open.cx.run_until_parked();
    let focus = open.pane.read_with(open.cx, |pane, _| pane.focus.clone());
    assert!(open.cx.update(|window, _| focus.is_focused(window)));
}

#[gpui::test]
async fn focus_changes_reach_a_program_that_asks(cx: &mut TestAppContext) {
    let on = STANDARD.encode(b"\x1b[?1004h");
    let terminals = Terminals {
        rows: vec![json!({"seq": 8, "prevSeq": 0, "data": on})],
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    // GPUI reports focus changes in the active window only.
    open.cx.update(|window, _| window.activate_window());
    open.until("focus reports on", |_, pane| {
        following(pane) && pane.grid.focus_reporting() && !pane.writing()
    })
    .await;
    open.cx.run_until_parked();
    let before = open.terminals.lock().unwrap().of("write").len();
    let focus = open.pane.read_with(open.cx, |pane, _| pane.focus.clone());
    let elsewhere = open.cx.update(|_, cx| cx.focus_handle());
    open.cx.update(|window, cx| window.focus(&elsewhere, cx));
    open.cx.run_until_parked();
    open.cx.update(|window, cx| window.focus(&focus, cx));
    let sent = |terminals: &Terminals| -> Vec<u8> {
        (terminals.of("write")[before..].iter())
            .flat_map(|args| STANDARD.decode(args["data"].as_str().unwrap()).unwrap())
            .collect()
    };
    open.until("both reported", |terminals, pane| {
        sent(terminals).len() == 6 && !pane.writing()
    })
    .await;
    assert_eq!(
        sent(&open.terminals.lock().unwrap()),
        b"\x1b[O\x1b[I".to_vec()
    );
}

#[gpui::test]
async fn a_write_that_fails_says_why(cx: &mut TestAppContext) {
    let terminals = Terminals {
        refuse_writes: Some("the shell is gone"),
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    open.cx.simulate_keystrokes("a");
    open.until("the failure shown", |_, pane| pane.status().is_some())
        .await;
    open.pane.read_with(open.cx, |pane, _| {
        assert_eq!(
            pane.status().as_deref(),
            Some("The terminal didn't take the keys: the shell is gone")
        );
    });
}

#[gpui::test]
async fn reopening_a_program_that_asks_for_focus_reports_tells_it(cx: &mut TestAppContext) {
    let terminals = Terminals {
        kept: b"\x1b[?1004h".to_vec(),
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    open.cx.update(|window, _| window.activate_window());
    open.until("told it has the focus", |terminals, pane| {
        !terminals.of("write").is_empty() && !pane.writing()
    })
    .await;
    let first = open.terminals.lock().unwrap().of("write")[0]["data"].clone();
    assert_eq!(
        STANDARD.decode(first.as_str().unwrap()).unwrap(),
        b"\x1b[I".to_vec()
    );
}

#[gpui::test]
async fn the_cursor_blinks_only_while_the_pane_has_the_focus(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.cx.update(|window, _| window.activate_window());
    open.until("the shell started", |_, pane| following(pane))
        .await;
    open.cx.run_until_parked();
    let cursor =
        |open: &mut super::Open<'_>| open.pane.read_with(open.cx, |pane, _| pane.cursor_on);
    assert!(cursor(&mut open));
    open.cx.executor().advance_clock(super::super::BLINK);
    open.cx.run_until_parked();
    assert!(!cursor(&mut open));
    open.cx.executor().advance_clock(super::super::BLINK);
    open.cx.run_until_parked();
    assert!(cursor(&mut open));
    let elsewhere = open.cx.update(|_, cx| cx.focus_handle());
    open.cx.update(|window, cx| window.focus(&elsewhere, cx));
    open.cx.run_until_parked();
    open.cx.executor().advance_clock(super::super::BLINK * 3);
    open.cx.run_until_parked();
    assert!(cursor(&mut open));
}

fn writes(open: &super::Open<'_>) -> Vec<Vec<u8>> {
    (open.terminals.lock().unwrap().of("write").iter())
        .map(|args| STANDARD.decode(args["data"].as_str().unwrap()).unwrap())
        .collect()
}

/// A finished composition, from the input method, as the window's input handler hands it on.
fn composed(open: &mut super::Open<'_>, text: &str) {
    (open.pane).update_in(open.cx, |pane, window, cx| {
        gpui_kit::EntityInputHandler::replace_text_in_range(pane, None, text, window, cx)
    });
    open.cx.run_until_parked();
}

/// A write went out, or is on its way.
fn sent(open: &mut super::Open<'_>) -> bool {
    !writes(open).is_empty() || open.pane.read_with(open.cx, |pane, _| pane.writing())
}

/// A composition in progress.
fn composing(open: &mut super::Open<'_>, text: &str) {
    (open.pane).update_in(open.cx, |pane, window, cx| {
        gpui_kit::EntityInputHandler::replace_and_mark_text_in_range(
            pane, None, text, None, window, cx,
        )
    });
    open.cx.run_until_parked();
}

#[gpui::test]
async fn composed_text_reaches_the_shell_once(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    composed(&mut open, "é");
    assert!(sent(&mut open), "the composed text wasn't sent");
    open.until("written", |terminals, pane| {
        !terminals.of("write").is_empty() && !pane.writing()
    })
    .await;
    assert_eq!(writes(&open), vec!["é".as_bytes().to_vec()]);

    // Through the window: a key the pane leaves alone goes to the input handler it has registered.
    let keystroke = gpui_kit::Keystroke {
        modifiers: gpui_kit::Modifiers {
            platform: true,
            ..Default::default()
        },
        key: "e".into(),
        key_char: Some("ê".into()),
    };
    open.cx
        .update(|window, cx| window.dispatch_keystroke(keystroke, cx));
    open.until("written", |terminals, pane| {
        terminals.of("write").len() == 2 && !pane.writing()
    })
    .await;
    assert_eq!(writes(&open)[1], "ê".as_bytes());

    // A typed character the pane takes itself goes once, not again through the handler.
    open.cx.simulate_input("ü");
    open.until("written", |terminals, pane| {
        terminals.of("write").len() >= 3 && !pane.writing()
    })
    .await;
    assert_eq!(writes(&open)[2..], ["ü".as_bytes().to_vec()]);
}

#[gpui::test]
async fn a_plain_key_is_not_sent_twice(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    open.cx.simulate_keystrokes("a");
    open.until("written", |terminals, pane| {
        !terminals.of("write").is_empty() && !pane.writing()
    })
    .await;
    open.cx.run_until_parked();
    assert_eq!(writes(&open), vec![b"a".to_vec()]);
}

#[gpui::test]
async fn a_composition_in_progress_sends_nothing(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    composing(&mut open, "e");
    open.pane.read_with(open.cx, |pane, _| {
        assert_eq!(pane.marked.as_deref(), Some("e"));
    });
    assert!(writes(&open).is_empty());
    composed(&mut open, "é");
    open.until("written", |terminals, pane| {
        !terminals.of("write").is_empty() && !pane.writing()
    })
    .await;
    assert_eq!(writes(&open), vec!["é".as_bytes().to_vec()]);
    open.pane.read_with(open.cx, |pane, _| {
        assert_eq!(pane.marked, None);
    });
}

#[gpui::test]
async fn no_input_is_taken_unless_open(cx: &mut TestAppContext) {
    let mut open = open(cx, Terminals::default(), false).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    composed(&mut open, "é");
    assert!(sent(&mut open), "the composed text wasn't sent");
    open.until("written", |terminals, pane| {
        !terminals.of("write").is_empty() && !pane.writing()
    })
    .await;
    open.push_row(json!({"seq": 1, "prevSeq": 0, "exit": 7}));
    composed(&mut open, "é");
    assert_eq!(writes(&open).len(), 1);
}

#[gpui::test]
async fn the_input_method_sees_its_composition_and_the_cursor_cell(cx: &mut TestAppContext) {
    let terminals = Terminals {
        rows: vec![json!({"seq": 3, "prevSeq": 0, "data": STANDARD.encode(b"abc")})],
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    open.until("the output shown", |_, pane| {
        following(pane) && pane.grid.cursor() == Some((3, 0))
    })
    .await;
    composing(&mut open, "ka");
    (open.pane).update_in(open.cx, |pane, window, cx| {
        use gpui_kit::EntityInputHandler as _;
        assert_eq!(pane.marked_text_range(window, cx), Some(0..2));
        let mut adjusted = None;
        assert_eq!(
            pane.text_for_range(1..5, &mut adjusted, window, cx)
                .as_deref(),
            Some("a")
        );
        assert_eq!(adjusted, Some(1..2));
        let cell = pane.cell;
        let bounds = pane.bounds_for_range(0..0, pane.bounds, window, cx);
        let at = pane.origin + gpui_kit::point(cell.width * 3., gpui_kit::px(0.));
        assert_eq!(bounds, Some(gpui_kit::Bounds::new(at, cell)));
        pane.unmark_text(window, cx);
        assert_eq!(pane.marked_text_range(window, cx), None);
    });
    assert!(writes(&open).is_empty(), "a composition was sent");
}

/// Preferences before the pane, so it observes them, then the pane on a shell, in an active window.
async fn with_preferences(cx: &mut TestAppContext) -> super::Open<'_> {
    cx.update(crate::core::state::preferences::Preferences::init);
    let mut open = open(cx, Terminals::default(), false).await;
    open.cx.update(|window, _| window.activate_window());
    open.until("the shell started", |_, pane| following(pane))
        .await;
    open.cx.run_until_parked();
    open
}

fn blink_off(open: &mut super::Open<'_>) {
    use crate::core::state::appearance::TERMINAL_CURSOR_BLINK;
    use crate::core::state::preferences::Preferences;
    open.cx
        .update(|_, cx| Preferences::save(TERMINAL_CURSOR_BLINK, json!(false), cx));
    open.cx.run_until_parked();
}

fn cursor_on(open: &mut super::Open<'_>) -> bool {
    open.pane.read_with(open.cx, |pane, _| pane.cursor_on)
}

fn blinking(open: &mut super::Open<'_>) -> bool {
    open.pane
        .read_with(open.cx, |pane, _| pane._blink.is_some())
}

#[gpui::test]
async fn blink_off_keeps_the_cursor_solid_with_no_timer(cx: &mut TestAppContext) {
    let mut open = with_preferences(cx).await;
    blink_off(&mut open);
    assert!(!blinking(&mut open), "a timer ran with blink off");
    for _ in 0..3 {
        open.advance(530);
        assert!(cursor_on(&mut open));
    }
}

#[gpui::test]
async fn turning_it_off_applies_at_once(cx: &mut TestAppContext) {
    let mut open = with_preferences(cx).await;
    open.advance(530);
    assert!(!cursor_on(&mut open), "it blinks every 530 ms");
    blink_off(&mut open);
    assert!(cursor_on(&mut open), "solid at once");
    assert!(!blinking(&mut open));
}

#[gpui::test]
async fn no_timer_while_exited_opening_said_or_hidden(cx: &mut TestAppContext) {
    let mut open = with_preferences(cx).await;
    assert!(blinking(&mut open), "it blinks while open and focused");
    open.push_row(json!({"seq": 1, "prevSeq": 0, "exit": 7}));
    assert!(!blinking(&mut open), "a timer ran while exited");
    open.shown(false);
    assert!(!blinking(&mut open), "a timer ran while hidden");
    open.shown(true);
    assert_eq!(
        open.pane
            .read_with(open.cx, |pane, _| pane.status())
            .as_deref(),
        Some("Opening terminal…")
    );
    assert!(!blinking(&mut open), "a timer ran while opening");
    open.until("the shell again", |_, pane| following(pane))
        .await;
    open.cx.run_until_parked();
    assert!(blinking(&mut open), "it blinks again once open");
    open.terminals
        .lock()
        .unwrap()
        .refuse("list", "failed", "no");
    super::pick("b", open.cx);
    open.until("refused", |_, pane| pane.status().as_deref() == Some("no"))
        .await;
    assert!(!blinking(&mut open), "a timer ran while saying why");
}
