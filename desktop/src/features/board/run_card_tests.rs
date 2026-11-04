//! Named for the recording frames each reproduces (run-card-recordings-frame-by-frame.md). Durations follow spec 6.11's one rule.
use serde_json::{Value, json};

use super::run_footer::{Footer, RunAction, footer, retryable};
use super::run_steps::{Pill, card_steps, pill, visible_steps};
use crate::activity::{StepMark, StepRight, format_elapsed};
use crate::core::engine::workflow_items::{RunItem, StepItem};

fn run(state: &str, extra: Value) -> RunItem {
    let mut value = json!({"entityKind":"run","machine":"m","id":"r","version":1,"state":state,"activeStepIds":[],"paused":false,"reference":false,"startedMs":0});
    value.as_object_mut().unwrap().extend(extra.as_object().unwrap().clone());
    RunItem::decode(value).unwrap()
}

fn step(id: &str, state: &str, extra: Value) -> StepItem {
    let mut value = json!({"entityKind":"step","machine":"m","id":id,"version":1,"runId":"r","name":id,"kind":"command","state":state,
        "attempt":1,"maxAttempts":3,"required":1,"ruleSkippable":0,"labelPresent":format!("Doing {id}"),"labelPast":format!("Did {id}"),
        "badges":[],"scopeGranted":false,"closed":false,"dependencies":[],"needsMeIds":[]});
    value.as_object_mut().unwrap().extend(extra.as_object().unwrap().clone());
    StepItem::decode(value).unwrap()
}

fn refs(steps: &[StepItem]) -> Vec<&StepItem> {
    steps.iter().collect()
}

#[test]
fn v3_f011_to_f012_active_row_flips_done_and_question_expands() {
    let run_item = run("running", json!({}));
    let steps = vec![
        step("reproduce", "succeeded", json!({"labelPast":"Reproduced the 500","startedMs":0,"endedMs":12_400})),
        step("ask", "waiting", json!({"startedMs":12_400})),
    ];
    let rows = card_steps(&run_item, &refs(&steps), 29_000);
    assert_eq!(rows[0].label.as_ref(), "Reproduced the 500");
    assert_eq!(rows[0].right, StepRight::Elapsed("12.4s".into()));
    assert_eq!(rows[1].mark, StepMark::Waiting);
    assert_eq!(pill(&run_item, &refs(&steps), true), Pill::NeedsYou);
    assert_eq!(footer(&run_item, &refs(&steps), None, true, false, 29_000), Footer { summary: "Step 2 of 2 · 29.0s".into(), actions: vec![] });
}

#[test]
fn v3_f051_to_f052_failure_inserts_retry_row() {
    let first = step("send-1", "failed", json!({"definitionKey":"send","labelPast":"Timed out on attempt 1","explanation":"No acknowledgement from the API after 30s"}));
    let before = vec![first.clone()];
    let after = vec![first, step("send-2", "running", json!({"definitionKey":"send","attempt":2,"labelPresent":"Retrying, attempt 2 of 3"}))];
    let run_item = run("running", json!({}));
    let rows = card_steps(&run_item, &refs(&after), 0);
    assert_eq!(rows.len(), card_steps(&run_item, &refs(&before), 0).len() + 1);
    assert_eq!(rows[0].label.as_ref(), "Timed out on attempt 1");
    assert_eq!(rows[0].explanation.as_ref().map(|text| text.as_ref()), Some("No acknowledgement from the API after 30s"));
    assert_eq!(rows[0].pills[0].0.as_ref(), "Failed");
    assert_eq!(rows[1].label.as_ref(), "Retrying, attempt 2 of 3");
}

#[test]
fn v3_f062_failed_becomes_retried_when_attempt_2_succeeds() {
    let steps = vec![
        step("send-1", "failed", json!({"definitionKey":"send"})),
        step("send-2", "succeeded", json!({"definitionKey":"send","attempt":2})),
    ];
    let rows = card_steps(&run("running", json!({})), &refs(&steps), 0);
    assert_eq!(rows[0].pills[0].0.as_ref(), "Retried");
}

#[test]
fn v3_f072_run_complete_row_appended_and_pill_complete() {
    let steps = vec![step("a", "succeeded", json!({}))];
    let run_item = run("succeeded", json!({"endedMs":1000}));
    let rows = card_steps(&run_item, &refs(&steps), 0);
    assert_eq!(rows.last().unwrap().label.as_ref(), "Run complete");
    assert_eq!(rows[0].pills[0].0.as_ref(), "Done");
    assert_eq!(pill(&run_item, &refs(&steps), false), Pill::Complete);
    assert_eq!(footer(&run_item, &refs(&steps), Some(4), false, false, 0), Footer { summary: "4 tools called".into(), actions: vec![RunAction::Rerun] });
}

#[test]
fn v2_rerun_dims_later_rows_with_now_wording_and_keeps_count() {
    let steps = vec![step("a", "succeeded", json!({})), step("b", "running", json!({})), step("c", "pending", json!({}))];
    let rows = card_steps(&run("running", json!({})), &refs(&steps), 0);
    assert_eq!(rows.len(), 3);
    assert_eq!(rows[2].label.as_ref(), "Doing c");
    assert_eq!(rows[2].mark, StepMark::Upcoming);
}

#[test]
fn v1_report_footer_retry_and_keep() {
    let steps = vec![
        step("a", "succeeded", json!({})),
        step("b", "succeeded", json!({})),
        step("c", "failed", json!({})),
        step("d", "failed", json!({"requiredScope":"files"})),
        step("e", "skipped", json!({})),
    ];
    let run_item = run("failed", json!({"endedMs":283_000}));
    assert_eq!(pill(&run_item, &refs(&steps), false), Pill::CompletedWithIssues);
    assert_eq!(
        footer(&run_item, &refs(&steps), None, false, false, 0),
        Footer { summary: "5 settled, 2 succeeded · ran 4m 43".into(), actions: vec![RunAction::Retry(1), RunAction::KeepSucceeded] }
    );
}

#[test]
fn v1_grant_then_retry_count_becomes_2() {
    let ungranted = vec![step("c", "failed", json!({})), step("d", "failed", json!({"requiredScope":"files"}))];
    assert_eq!(retryable(&refs(&ungranted)), 1);
    let granted = vec![step("c", "failed", json!({})), step("d", "failed", json!({"requiredScope":"files","scopeGranted":true}))];
    assert_eq!(retryable(&refs(&granted)), 2);
}

#[test]
fn v1_keep_adds_closed_pills_and_clears_actions() {
    let steps = vec![step("a", "succeeded", json!({})), step("b", "failed", json!({"closed":true}))];
    let run_item = run("failed", json!({"endedMs":1000}));
    let rows = card_steps(&run_item, &refs(&steps), 0);
    assert!(rows[1].pills.iter().any(|(label, _)| label.as_ref() == "Failed"), "the outcome stays failed");
    assert!(rows[1].pills.iter().any(|(label, _)| label.as_ref() == "Closed"));
    assert_eq!(footer(&run_item, &refs(&steps), None, false, false, 0).actions, vec![]);
    assert_eq!(footer(&run_item, &refs(&steps), None, false, true, 0).actions, vec![RunAction::Undo]);
}

#[test]
fn more_after_these_counts_beyond_five_upcoming() {
    let steps = (0..8).map(|index| step(&format!("s{index}"), "pending", json!({}))).chain([step("done", "succeeded", json!({}))]).collect::<Vec<_>>();
    let (visible, more) = visible_steps(&refs(&steps));
    assert_eq!((visible.len(), more), (6, 3));
    let (all, none) = visible_steps(&refs(&steps[3..]));
    assert_eq!((all.len(), none), (6, 0));
}

#[test]
fn a_paused_run_offers_resume_and_null_progress_shows_no_percent() {
    let steps = vec![step("a", "running", json!({"startedMs":0}))];
    let paused = run("running", json!({"paused":true}));
    assert_eq!(pill(&paused, &refs(&steps), false), Pill::Paused);
    assert_eq!(footer(&paused, &refs(&steps), None, false, false, 0).actions, vec![RunAction::Resume, RunAction::Stop]);
    let rows = card_steps(&run("running", json!({})), &refs(&steps), 8000);
    assert_eq!(rows[0].right, StepRight::Elapsed("8.0s".into()));
    let with_progress = vec![step("a", "running", json!({"progress":40.0}))];
    assert_eq!(card_steps(&run("running", json!({})), &refs(&with_progress), 0)[0].right, StepRight::Percent(40));
}

#[test]
fn elapsed_formats() {
    assert_eq!(format_elapsed(320).as_ref(), "320ms");
    assert_eq!(format_elapsed(12_400).as_ref(), "12.4s");
    assert_eq!(format_elapsed(148_000).as_ref(), "2m 28");
}

#[test]
fn started_line_reads_owner_and_time() {
    use crate::core::engine::{EngineItem, EngineKey};
    use crate::features::board::board_queries::BoardData;
    use crate::features::board::run_card::RunCard;
    let owner = EngineItem {
        key: EngineKey { machine: "m".into(), kind: "session".into(), id: "s".into() },
        version: 1,
        value: json!({"entityKind":"session","machine":"m","id":"s","version":1,"provider":"claude","folder":"/w","state":"running","activityState":"working","epoch":1,"title":"Mute lane"}),
    };
    let data = BoardData::of(&[], &[owner], &[], &[]);
    let read = |value: serde_json::Value| {
        let mut run = json!({"entityKind":"run","id":"r","machine":"m","version":1,"state":"running","paused":false,"reference":false,"activeStepIds":[]});
        run.as_object_mut().unwrap().extend(value.as_object().unwrap().clone());
        let run = crate::core::engine::workflow_items::RunItem::decode(run).unwrap();
        RunCard::started_line(&run, &data)
    };
    assert_eq!(read(json!({"ownerSessionId":"s"})).as_deref(), Some("Mute lane"));
    assert_eq!(read(json!({})), None, "nothing absent is made up");
    let started = read(json!({"ownerSessionId":"s","startedMs":0})).unwrap();
    assert!(started.starts_with("Mute lane · Started "), "{started}");
}
