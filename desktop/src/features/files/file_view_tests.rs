//! A file's tab is saved with the session and path it shows, and rebuilt from them.

use gpui::TestAppContext;
use gpui_kit::component::dock::{BasePanel, PanelBuildContext, PanelRegistry};
use gpui_kit::{WeakEntity, px};
use serde_json::json;

use super::{FILE, FileView};

// Pins: files#16
#[gpui::test]
fn a_file_tab_is_saved_with_its_session_and_path_and_rebuilt_from_them(cx: &mut TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        FileView::register(cx);
    });
    let (view, cx) =
        cx.add_window_view(|_, cx| FileView::new("s1".into(), "src/main.rs".into(), cx));
    let state = cx.update(|_, cx| BasePanel::dump(view.read(cx), cx));
    assert_eq!(state.panel_name, FILE);
    assert_eq!(
        serde_json::to_value(&state.info).unwrap(),
        json!({ "panel": { "session": "s1", "path": "src/main.rs" } })
    );
    let rebuilt = cx.update(|window, cx| {
        let context = PanelBuildContext::new(WeakEntity::new_invalid(), &state, &state.info);
        PanelRegistry::build_panel(FILE, context, window, cx).expect("File is registered")
    });
    assert_eq!(cx.update(|_, cx| rebuilt.dump(cx)), state);
}

// Pins: files#11
#[gpui::test]
fn the_list_scrolls_far_enough_sideways_to_show_the_end_of_the_widest_line(
    cx: &mut TestAppContext,
) {
    cx.update(gpui_kit::init);
    let (view, cx) = cx.add_window_view(|_, cx| FileView::new("s1".into(), "min.js".into(), cx));
    view.update(cx, |view, cx| {
        view.text.lines = vec!["short".into(), "x".repeat(400), "end".into()];
        view.text.widest = 1;
        view.text.line_count = 3;
        cx.notify();
    });
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let line = cx
        .debug_bounds("file-line-1")
        .expect("the widest line is drawn");
    let (reach, origin) = view.read_with(cx, |view, _| {
        let handle = &view.list.0.borrow().base_handle;
        (
            handle.max_offset().x + handle.bounds().size.width,
            handle.bounds().origin.x,
        )
    });
    assert!(
        origin + reach >= line.right() - px(1.),
        "the list scrolls to {:?} but the line ends at {:?}",
        origin + reach,
        line.right()
    );
}
