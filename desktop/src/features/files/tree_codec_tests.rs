use crate::core::state::workspace_panes::PaneCodec;
use super::{parse, TreeCodec, WorkspaceKey};
use gpui_kit::{AppContext as _, Context, IntoElement, Render, Window, div};
use serde_json::json;
fn key() -> WorkspaceKey {
    WorkspaceKey {
        machine: "host".into(),
        session_id: "s".into(),
        checkout_root: "/repo".into(),
    }
}
#[test]
fn tree_metadata_refuses_outside_paths_duplicates_and_malformed_scroll() {
    for value in [
        json!({"expandedPaths":["../outside"],"scroll":0}),
        json!({"expandedPaths":["a","a"],"scroll":0}),
        json!({"expandedPaths":[],"selectedPath":"/absolute","scroll":0}),
        json!({"expandedPaths":[],"scroll":"bad"}),
    ] {
        assert!(parse(&value, &key()).is_err());
    }
    let saved = parse(
        &json!({"expandedPaths":["src","src/λ"],"selectedPath":"src/λ/a.rs","scroll":-92.5}),
        &key(),
    )
    .unwrap();
    assert_eq!(saved.expanded_paths, vec!["src", "src/λ"]);
    assert_eq!(saved.scroll, -92.5);
    assert_eq!(saved.selected_path.as_deref(), Some("src/λ/a.rs"));
}

struct CaptureHost;
impl Render for CaptureHost {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
#[gpui::test]
fn a_missing_workspace_identity_cannot_be_captured_as_an_empty_snapshot(
    cx: &mut gpui::TestAppContext,
) {
    cx.update(gpui_kit::init);
    let (_, cx) = cx.add_window_view(|_, _| CaptureHost);
    cx.update(|window, cx| {
        assert!(
            TreeCodec
                .capture(window, cx)
                .unwrap_err()
                .contains("identity")
        )
    });
}
