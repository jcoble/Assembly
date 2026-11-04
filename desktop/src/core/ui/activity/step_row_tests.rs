//! Presentation semantics over the canonical producer StepItem, independent of layout.
use super::step_row::{StepTone, view};
use crate::core::engine::workflow_items::StepItem;
use serde_json::{Value, json};
fn step() -> Value {
    json!({"entityKind":"step","machine":"row-owner","id":"step","version":1,"runId":"run","name":"check","kind":"command","state":"running","attempt":1,"maxAttempts":2,"required":1,"ruleSkippable":0,"labelPresent":"Checking","labelPast":"Checked","progress":null,"badges":[],"requiredScope":"files","scopeGranted":false,"closed":false,"startedMs":1000,"dependencies":[],"needsMeIds":[]})
}

#[test]
fn completed_labels_and_cancelled_state_never_claim_failure_or_success() {
    let mut value = step();
    value["state"] = json!("cancelled");
    value["endedMs"] = json!(1500);
    let item = StepItem::decode(value).unwrap();
    let row = view(&item, 9000);
    assert_eq!(row.label, "Checked");
    assert_eq!(row.tone, StepTone::Cancelled);
    assert_eq!(row.elapsed_ms, Some(500));
    assert!(row.progress.is_none());
    assert!(!row.running);
}
#[test]
fn running_row_uses_actual_present_label_attempt_progress_and_elapsed_time() {
    let mut value = step();
    value["progress"] = json!(42);
    value["attempt"] = json!(2);
    let item = StepItem::decode(value).unwrap();
    let row = view(&item, 1250);
    assert_eq!(row.label, "Checking");
    assert!(row.running);
    assert_eq!(row.attempt, 2);
    assert_eq!(row.progress, Some(42.));
    assert_eq!(row.elapsed_ms, Some(250));
    assert_eq!(row.required_scope.as_deref(), Some("files"));
    assert!(!row.scope_granted);
}
#[test]
fn failure_reason_and_skipped_state_remain_distinct() {
    let mut value = step();
    value["state"] = json!("failed");
    value["explanation"] = json!("Exact reason");
    let item = StepItem::decode(value.clone()).unwrap();
    assert_eq!(view(&item, 2000).tone, StepTone::Failed);
    assert_eq!(
        view(&item, 2000).explanation.as_deref(),
        Some("Exact reason")
    );
    value["state"] = json!("skipped");
    let item = StepItem::decode(value).unwrap();
    assert_eq!(view(&item, 2000).tone, StepTone::Skipped);
}
