//! Recorded source is display identity; activation still requires live discovery and descriptor admission.
use crate::core::socket::row_source::ExpectedRowSource;
use crate::features::sessions::rail_page::{RailEntry, test_entry};
use serde_json::json;
#[test]
fn rail_decodes_new_metadata_without_legacy_question_columns_and_keeps_actual_source_identity() {
    let mut row = json!({"sessionId":"same","kind":"session","section":"working","agent":"provider","repoUrl":"repo","group":"repo","count":0,"machines":[],"title":null,"machine":"saved-route","updatedAt":1,"completedAt":null,"running":false,"failed":false,"unread":false,"ordering":"same","branch":null,"icon":"icon.svg","sectionIndex":0,"header":2,"source":{"machine":"actual-engine","generation":4,"routeMachine":"saved-route"},"parentSessionId":"parent","depth":1,"position":23,"worktree":"/recorded-checkout"});
    let entry: RailEntry = serde_json::from_value(row.clone()).unwrap();
    assert_eq!(entry.source.as_ref().unwrap().machine, "actual-engine");
    assert_eq!(entry.machine.as_deref(), Some("saved-route"));
    assert_eq!(entry.depth, 1);
    assert_eq!(entry.position, Some(23));
    assert_eq!(entry.worktree.as_deref(), Some("/recorded-checkout"));
    assert!(!entry.native_activity);
    row["nativeActivity"] = json!(true);
    assert!(
        serde_json::from_value::<RailEntry>(row.clone())
            .unwrap()
            .native_activity
    );
    row.as_object_mut().unwrap().remove("source");
    let missing: RailEntry = serde_json::from_value(row).unwrap();
    assert!(missing.source.is_none());
    assert!(test_entry("legacy", "repo").source.is_none());
}
#[gpui::test]
fn a_row_with_native_activity_keeps_its_arrow_and_needs_a_source_to_open(
    cx: &mut gpui::TestAppContext,
) {
    let source = ExpectedRowSource {
        machine: "here".into(),
        generation: 1,
        route_machine: None,
    };
    let mut entry = test_entry("parent", "repo");
    entry.source = Some(source);
    assert!(
        !cx.update(|cx| super::has_details(&entry, cx)),
        "nothing to open"
    );
    entry.native_activity = true;
    assert!(
        cx.update(|cx| super::has_details(&entry, cx)),
        "native activity is something to open"
    );
    entry.source = None;
    assert!(
        !cx.update(|cx| super::has_details(&entry, cx)),
        "opening needs a source"
    );
}
