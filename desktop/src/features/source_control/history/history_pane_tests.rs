use super::HistoryPane;
use crate::features::source_control::git_status::GitStatus;
use gpui::TestAppContext;
use gpui_kit::{MouseButton, Modifiers, MouseDownEvent, MouseUpEvent, px, size};
use serde_json::json;

fn status(branch: serde_json::Value, root: serde_json::Value) -> GitStatus {
    serde_json::from_value(json!({
        "branch":branch,"head":"abc","ahead":0,"behind":0,"hasUpstream":true,"files":[],"totalFiles":0,"fingerprint":"f","checkoutRoot":root
    }))
    .unwrap()
}
// Pins: g9#18
#[gpui::test]
fn the_file_chip_clears_the_path_filter(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    let (pane, visual) = cx.add_window_view(|window, cx| {
        HistoryPane::new("s1".into(), Some("src/a.rs".into()), None, window, cx)
    });
    visual.simulate_resize(size(px(1200.), px(400.)));
    visual.run_until_parked();
    visual.update(|window, cx| window.draw(cx).clear(cx));
    let clear = visual.debug_bounds("history-path-clear").expect("the chip's clear button").center();
    let modifiers = Modifiers::default();
    visual.simulate_mouse_move(clear, None, modifiers);
    visual.simulate_event(MouseDownEvent { position: clear, button: MouseButton::Left, modifiers, click_count: 1, first_mouse: false });
    visual.simulate_event(MouseUpEvent { position: clear, button: MouseButton::Left, modifiers, click_count: 1 });
    visual.run_until_parked();
    visual.update(|window, cx| {
        window.draw(cx).clear(cx);
        assert_eq!(pane.read(cx).path, None);
    });
    assert!(visual.debug_bounds("history-path-clear").is_none());
}
#[gpui::test]
fn the_folder_label_names_the_checkout_it_read_and_nothing_else(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    let (pane, visual) = cx.add_window_view(|window, cx| HistoryPane::new("s1".into(), None, None, window, cx));
    visual.update(|_, cx| {
        pane.update(cx, |pane, _| {
            assert_eq!(pane.folder_label(), "No repository");
            pane.status = Some(status(json!("main"), json!("/home/me/Assembly/")));
            assert_eq!(pane.folder_label(), "Assembly (main)");
            pane.status = Some(status(json!(null), json!("/home/me/Assembly")));
            assert_eq!(pane.folder_label(), "Assembly");
            pane.status = Some(status(json!("main"), json!(null)));
            assert_eq!(pane.folder_label(), "This folder (main)");
        })
    });
}
