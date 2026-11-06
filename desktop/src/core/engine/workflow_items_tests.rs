//! Source-derived WorkflowQueries fixtures; no provider execution proof.
use super::workflow_items::{RunItem, RunView, StepItem};
use serde_json::{Value, json};
pub(super) fn step() -> Value {
    json!({"entityKind":"step","machine":"row-owner","id":"step-z","version":2,"runId":"run",
      "name":"check","definitionKey":"z-first","kind":"command","state":"running","attempt":1,"maxAttempts":2,
      "required":1,"ruleSkippable":0,"labelPresent":"Checking","labelPast":"Checked","stage":"check",
      "detail":"Recorded detail","explanation":null,"progress":null,"badges":[],"requiredScope":"files",
      "scopeGranted":false,"closed":false,"startedMs":1000,"endedMs":null,"authorityMachine":null,
      "authoritySessionId":null,"authorityEpoch":null,"dependencies":[],"needsMeIds":[]})
}
pub(super) fn run() -> Value {
    json!({"entityKind":"run","machine":"run-owner","id":"run","version":4,"state":"running",
      "title":"Actual workflow","definitionHash":"hash","activeStepIds":["step-z"],"paused":false,"reference":true})
}
#[test]
fn display_owner_and_nullable_execution_authority_are_distinct() {
    let step = StepItem::decode(step()).unwrap();
    assert_eq!(step.key().machine, "row-owner");
    assert!(step.authority_machine.is_none());
    assert!(step.authority_session_id.is_none());
    assert!(step.authority_epoch.is_none());
    assert!(step.required);
    assert!(!step.rule_skippable);
    let run = RunItem::decode(run()).unwrap();
    assert!(run.reference);
    assert!(run.owner_epoch.is_none());
    assert_eq!(run.key().machine, "run-owner");
}
#[test]
fn sparse_invalidations_never_decode_as_complete_workflow_rows() {
    assert!(
        StepItem::decode(
            json!({"entityKind":"step","machine":"owner","id":"s","version":1,"state":"running"})
        )
        .is_err()
    );
    let mut value = step();
    value["required"] = json!(2);
    assert!(StepItem::decode(value).is_err());
    let mut value = step();
    value["entityKind"] = json!("run");
    assert!(StepItem::decode(value).is_err());
}
#[test]
fn run_pages_preserve_producer_order_and_actual_item_owners() {
    let mut second = step();
    second["id"] = json!("step-a");
    second["definitionKey"] = json!("a-second");
    let page = RunView::decode(
        json!({"run":run(),"steps":[step(),second],"next":"opaque:cut"}),
        "run",
    )
    .unwrap();
    assert_eq!(
        page.steps.iter().map(|s| s.id.as_str()).collect::<Vec<_>>(),
        ["step-z", "step-a"]
    );
    assert_eq!(page.next.as_deref(), Some("opaque:cut"));
    assert!(RunView::decode(json!({"run":run(),"steps":[step(),step()]}), "run").is_err());
    assert!(RunView::decode(json!({"run":run(),"steps":[step()]}), "another").is_err());
}

#[test]
fn pagination_refuses_repeated_tokens_definition_replacement_and_duplicate_steps() {
    use super::workflow_items::RunPages;
    let first =
        || RunView::decode(json!({"run":run(),"steps":[step()],"next":"opaque"}), "run").unwrap();
    let mut pages = RunPages::new(first()).unwrap();
    let mut second = step();
    second["id"] = json!("another");
    let repeated = RunView::decode(
        json!({"run":run(),"steps":[second.clone()],"next":"opaque"}),
        "run",
    )
    .unwrap();
    assert!(pages.append(repeated).is_err());
    assert_eq!(pages.view().steps.len(), 1);
    let mut changed = run();
    changed["definitionHash"] = json!("replaced");
    let changed = RunView::decode(json!({"run":changed,"steps":[second.clone()]}), "run").unwrap();
    assert!(pages.append(changed).is_err());
    assert_eq!(pages.view().steps.len(), 1);
    let duplicate = RunView::decode(json!({"run":run(),"steps":[step()]}), "run").unwrap();
    assert!(pages.append(duplicate).is_err());
    assert_eq!(pages.view().steps.len(), 1);
    let final_page = RunView::decode(json!({"run":run(),"steps":[second]}), "run").unwrap();
    pages.append(final_page).unwrap();
    assert!(pages.view().next.is_none());
    assert_eq!(pages.view().steps.len(), 2);
}
