//! Which controls a run and step offer, what each sends, and what the engine's refusals read as.
use super::step_controls::{StepControl, command, offered, refusal};
use crate::core::engine::{
    EngineRefusal,
    commands::CommandReceipt,
    workflow_items::{RunItem, StepItem},
};
use StepControl::{Continue, Retry, Skip};
use serde_json::{Value, json};

fn run(state: &str) -> RunItem {
    RunItem::decode(json!({"entityKind":"run","machine":"owner","id":"run","version":4,"state":state,"ownerEpoch":9,"definitionHash":"hash","activeStepIds":[],"paused":false,"reference":true})).unwrap()
}

fn step(fields: Value) -> StepItem {
    let mut step = json!({"entityKind":"step","machine":"owner","id":"step","version":2,"runId":"run","name":"check","kind":"agent","state":"pending","attempt":1,"maxAttempts":2,"required":1,"ruleSkippable":0,"labelPresent":"Checking","labelPast":"Checked","progress":null,"badges":[],"scopeGranted":false,"closed":false,"dependencies":[],"needsMeIds":[]});
    for (key, value) in fields.as_object().unwrap() {
        step[key] = value.clone();
    }
    StepItem::decode(step).unwrap()
}

#[test]
fn a_step_offers_only_what_its_state_leaves_open() {
    let cases = [
        (json!({"state":"failed"}), vec![Retry]),
        (json!({"state":"failed","kind":"gate"}), vec![Retry]),
        (json!({"state":"running"}), vec![]),
        (json!({"state":"succeeded"}), vec![]),
        (json!({"state":"waiting"}), vec![]),
        (
            json!({"state":"waiting","outcomeReason":"session_lost"}),
            vec![Retry],
        ),
        (
            json!({"state":"waiting","outcomeReason":"timed_out"}),
            vec![Retry],
        ),
        (
            json!({"state":"waiting","outcomeReason":"interrupted"}),
            vec![Retry],
        ),
        (
            json!({"state":"waiting","outcomeReason":"command_uncertain"}),
            vec![Retry],
        ),
        (
            json!({"state":"waiting","outcomeReason":"review_required"}),
            vec![],
        ),
        (json!({"state":"pending"}), vec![]),
        (json!({"state":"pending","required":0}), vec![Skip]),
        (
            json!({"state":"pending","required":0,"kind":"gate"}),
            vec![],
        ),
        (json!({"state":"pending","ruleSkippable":1}), vec![]),
        (
            json!({"state":"pending","ruleSkippable":1,"outcomeReason":"condition_false"}),
            vec![Skip],
        ),
        (
            json!({"state":"waiting","outcomeReason":"scope_required"}),
            vec![],
        ),
        (
            json!({"state":"waiting","outcomeReason":"scope_required","scopeGranted":true}),
            vec![Continue],
        ),
        (
            json!({"state":"waiting","outcomeReason":"scope_required","scopeGranted":true,"needsMeIds":["need"]}),
            vec![],
        ),
        (
            json!({"state":"waiting","outcomeReason":"scope_required","scopeGranted":true,"kind":"gate"}),
            vec![],
        ),
    ];
    for (fields, expected) in cases {
        assert_eq!(
            offered(&run("running"), &step(fields.clone())),
            expected,
            "{fields}"
        );
    }
}

#[test]
fn a_finished_or_cancelling_run_offers_nothing() {
    for state in ["succeeded", "failed", "cancelled", "cancelling"] {
        assert!(
            offered(&run(state), &step(json!({"state":"failed"}))).is_empty(),
            "{state}"
        );
    }
}

#[test]
fn each_control_addresses_the_run_with_the_step_in_its_command() {
    let cases = [
        (Retry, json!({"kind":"retry_workflow_step","stepId":"step"})),
        (
            Skip,
            json!({"kind":"decide_workflow_step","stepId":"step","decision":"skip"}),
        ),
        (
            Continue,
            json!({"kind":"decide_workflow_step","stepId":"step","decision":"continue"}),
        ),
    ];
    for (control, wire) in cases {
        let sent =
            serde_json::to_value(command(&run("running"), "step", control).unwrap()).unwrap();
        assert_eq!(sent["command"], wire);
        assert_eq!(sent["entityId"], "run");
        assert_eq!(sent["expectedVersion"], 4);
        assert_eq!(sent["epoch"], 9);
        assert_eq!(sent["machine"], "owner");
        assert_eq!(sent["surface"], "rail");
    }
}

#[test]
fn a_run_with_no_owner_epoch_is_refused_before_anything_is_sent() {
    let mut value = json!({"entityKind":"run","machine":"owner","id":"run","version":4,"state":"running","definitionHash":"hash","activeStepIds":[],"paused":false,"reference":true});
    for epoch in [Value::Null, json!(-1)] {
        value["ownerEpoch"] = epoch;
        let refused = command(&RunItem::decode(value.clone()).unwrap(), "step", Retry).unwrap_err();
        assert_eq!(refused.code, "session_unavailable");
    }
}

fn receipt(applied: bool, error: Option<&str>) -> Result<CommandReceipt, EngineRefusal> {
    Ok(CommandReceipt {
        applied,
        error: error.map(str::to_owned),
        version: 5,
    })
}

#[test]
fn refusals_read_as_sentences_and_never_as_a_bare_code() {
    assert_eq!(refusal(receipt(true, None)), None);
    assert_eq!(
        refusal(receipt(false, Some("attempts_exhausted"))).as_deref(),
        Some("This step has used all its attempts.")
    );
    for code in [
        "executor_quiescence_required",
        "required_step",
        "invalid_transition",
        "already_resolved",
        "stale_epoch",
        "forbidden",
        "wrong_machine",
        "version_conflict",
    ] {
        let said = refusal(receipt(false, Some(code))).unwrap();
        assert!(!said.contains(code), "{code} reads as: {said}");
    }
    assert!(
        refusal(receipt(false, Some("brand_new")))
            .unwrap()
            .contains("brand_new")
    );
    assert!(refusal(receipt(false, None)).is_some());
    let unknown = EngineRefusal {
        code: "outcome_unknown".into(),
        message: "Check the current state.".into(),
    };
    assert_eq!(
        refusal(Err(unknown)).as_deref(),
        Some("Check the current state.")
    );
}
