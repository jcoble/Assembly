use super::{session_view::SessionView, test_rows::row};
use serde_json::json;

#[test]
fn native_open_references_preserve_the_actual_parent_row_identity_and_latest_seq() {
    let mut view = SessionView::default();
    let first = row(
        7,
        "run",
        Some("subagent:native"),
        "subagent",
        json!({"sessionId":"t","title":"child","state":"running","canStop":false}),
    );
    view.apply(&first);
    let mut updated = first.clone();
    updated.seq = 12;
    updated.payload["state"] = json!("idle");
    view.apply(&updated);
    view.prepend_page(vec![first], false);
    let reference = view.row_reference("subagent:subagent:native").unwrap();
    assert_eq!(reference.session_id, "t");
    assert_eq!(reference.item_id.as_deref(), Some("subagent:native"));
    assert_eq!(reference.seq, 12);
    assert!(view.row_reference("native").is_none());
}
