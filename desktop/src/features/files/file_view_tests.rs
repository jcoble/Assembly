//! A file's tab is saved with the session and path it shows, and rebuilt from them.

use gpui::TestAppContext;
use gpui_kit::WeakEntity;
use gpui_kit::component::dock::{BasePanel, PanelBuildContext, PanelRegistry};
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

// Pins: files#11. The retained editor replaces the custom read-only line list.
#[gpui::test]
fn a_coherent_file_becomes_an_editable_shared_draft_with_unicode_navigation(
    cx: &mut TestAppContext,
) {
    use super::super::document_store::DocumentStore;
    use super::super::entry::{FileChunk, FileOwnership};
    cx.update(gpui_kit::init);
    let (view, cx) = cx.add_window_view(|_, cx| FileView::new("s1".into(), "a.rs".into(), cx));
    view.update_in(cx, |view, window, cx| {
        view.active = true;
        view.owner = Some(FileOwnership {
            machine: "actual-engine".into(),
            checkout_root: "/checkout".into(),
            remote_session_id: "s1".into(),
        });
        view.text.start();
        view.text.loaded(
            0,
            Ok(FileChunk {
                content: "é\r\nlast\n".into(),
                size: 9,
                revision: "r1".into(),
                line_count: 2,
                binary: false,
                next_offset: None,
            }),
        );
        view.bind_editor(window, cx);
        let editor = view.editor.as_mut().expect("coherent source editor");
        assert_eq!(editor.state.read(cx).value().as_ref(), "é\r\nlast\n");
        editor.jump(2, 3, cx);
        assert_eq!(editor.state.read(cx).selected_range(), 6..6);
        editor
            .state
            .update(cx, |state, cx| state.set_value("newé\r\n", window, cx));
        view.editor_changed(window, cx);
        let key = view.document.as_ref().unwrap();
        let docs = cx.global::<DocumentStore>();
        assert_eq!(docs.snapshot(key).unwrap().text, "newé\r\n");
        assert!(docs.is_dirty(key));
        assert!(
            !BasePanel::closable(view, cx),
            "unsaved work cannot be silently removed"
        );
    });
}
