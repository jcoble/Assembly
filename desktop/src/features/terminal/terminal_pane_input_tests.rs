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
