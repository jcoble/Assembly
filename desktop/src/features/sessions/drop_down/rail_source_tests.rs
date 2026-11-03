//! Recorded source is display identity; activation still requires live discovery and descriptor admission.
use serde_json::json;
use crate::features::sessions::rail_page::{RailEntry,test_entry};
#[test]
fn rail_decodes_new_metadata_without_legacy_question_columns_and_keeps_actual_source_identity() {
    let mut row=json!({"sessionId":"same","kind":"session","section":"working","agent":"provider","repoUrl":"repo","group":"repo","count":0,"machines":[],"title":null,"machine":"saved-route","updatedAt":1,"completedAt":null,"running":false,"failed":false,"unread":false,"ordering":"same","branch":null,"branchAt":null,"icon":"icon.svg","sectionIndex":0,"header":2,"source":{"machine":"actual-engine","generation":4,"routeMachine":"saved-route"},"parentSessionId":"parent","depth":1,"position":23,"worktree":"/recorded-checkout"});
    let entry:RailEntry=serde_json::from_value(row.clone()).unwrap();
    assert_eq!(entry.source.as_ref().unwrap().machine,"actual-engine");assert_eq!(entry.machine.as_deref(),Some("saved-route"));
    assert_eq!(entry.depth,1);assert_eq!(entry.position,Some(23));assert_eq!(entry.worktree.as_deref(),Some("/recorded-checkout"));
    row.as_object_mut().unwrap().remove("source");let missing:RailEntry=serde_json::from_value(row).unwrap();assert!(missing.source.is_none());
    assert!(test_entry("legacy","repo").source.is_none());
}
