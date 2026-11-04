//! What the Changes tab saves and brings back, including what an older layout saved.
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use gpui_kit::Entity;
use gpui_kit::component::dock::{BasePanel as _, PanelInfo, PanelState};
use serde_json::{Value, json};

use super::{CHANGES, ChangesPane};
use crate::core::state::selected_session::SelectedSession;

fn saved(info: Value) -> PanelState {
    PanelState {
        panel_name: CHANGES.into(),
        children: Vec::new(),
        info: PanelInfo::panel(info),
    }
}
#[test]
fn a_saved_scope_must_be_one_of_the_two() {
    assert!(
        ChangesPane::workspace_key(&saved(json!({"session":"s1","scope":"branch"})), "s1").is_ok()
    );
    assert!(
        ChangesPane::workspace_key(&saved(json!({"session":"s1","scope":"everything"})), "s1")
            .is_err()
    );
    assert!(ChangesPane::workspace_key(&saved(json!({"session":"s1","scope":7})), "s1").is_err());
}
#[gpui::test]
fn the_saved_target_comes_back_and_an_old_layouts_means_what_it_meant(cx: &mut TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
    });
    let restored = |info: Value, cx: &mut TestAppContext| {
        let pane = cx.new(|cx| ChangesPane::new(None, cx));
        pane.update(cx, |pane, cx| pane.restore(&info, cx));
        pane
    };
    let info = |pane: &Entity<ChangesPane>, cx: &mut TestAppContext| -> Value {
        cx.update(|cx| match pane.read(cx).dump(cx).info {
            PanelInfo::Panel(info) => info,
            _ => panic!("leaf metadata"),
        })
    };
    let branch = restored(json!({"session":"s1","scope":"branch","split":true}), cx);
    let saved = info(&branch, cx);
    assert_eq!(saved["scope"], "branch");
    assert_eq!(saved["split"], true);

    assert_eq!(
        info(&restored(json!({"session":"s1"}), cx), cx)["scope"],
        "branch"
    );
    let file = restored(
        json!({"session":"s1","path":"a.rs","originalPath":"old.rs"}),
        cx,
    );
    let saved = info(&file, cx);
    assert_eq!(saved["scope"], "uncommitted");
    assert_eq!(saved["path"], "a.rs");
    assert!(saved.get("originalPath").is_none());

    let sha = "a".repeat(40);
    let commit = restored(
        json!({"session":"s1","sha":sha,"path":"a.rs","title":"Fix"}),
        cx,
    );
    let saved = info(&commit, cx);
    assert_eq!(saved["sha"], sha.as_str());
    assert!(saved.get("scope").is_none());
    assert!(info(&restored(Value::Null, cx), cx).is_null());
}
