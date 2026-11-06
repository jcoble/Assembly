//! Tab and Shift-Tab in the app's window: the kit's Root binds them to move the focus, but a focused terminal sends
//! them to its shell and keeps the focus.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::TestAppContext;

use super::{Terminals, following, open_in_root};

/// Presses `keys` in the focused pane and returns what the shell was sent, and whether the pane still has the focus.
async fn pressed(cx: &mut TestAppContext, keys: &str) -> (Vec<Vec<u8>>, bool) {
    let mut open = open_in_root(cx, Terminals::default()).await;
    open.until("the shell started", |_, pane| following(pane))
        .await;
    let focus = open.pane.read_with(open.cx, |pane, _| pane.focus.clone());
    open.cx.update(|window, cx| window.focus(&focus, cx));
    open.cx.run_until_parked();
    open.cx.simulate_keystrokes(keys);
    open.cx.simulate_keystrokes("a");
    open.until("written", |terminals, pane| {
        let written: Vec<u8> = (terminals.of("write").iter())
            .flat_map(|args| STANDARD.decode(args["data"].as_str().unwrap()).unwrap())
            .collect();
        written.ends_with(b"a") && !pane.writing()
    })
    .await;
    let writes = (open.terminals.lock().unwrap().of("write").iter())
        .map(|args| STANDARD.decode(args["data"].as_str().unwrap()).unwrap())
        .collect::<Vec<_>>();
    let kept = open.cx.update(|window, _| focus.is_focused(window));
    (writes, kept)
}

fn joined(writes: &[Vec<u8>]) -> Vec<u8> {
    writes.concat()
}

#[gpui::test]
async fn tab_reaches_the_shell_and_the_pane_keeps_the_focus(cx: &mut TestAppContext) {
    let (writes, kept) = pressed(cx, "tab").await;
    assert_eq!(joined(&writes), b"\ta".to_vec());
    assert!(kept, "Tab moved the focus out of the terminal");
}

#[gpui::test]
async fn shift_tab_reaches_the_shell_and_the_pane_keeps_the_focus(cx: &mut TestAppContext) {
    let (writes, kept) = pressed(cx, "shift-tab").await;
    assert_eq!(joined(&writes), b"\x1b[Za".to_vec());
    assert!(kept, "Shift-Tab moved the focus out of the terminal");
}
