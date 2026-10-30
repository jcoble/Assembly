//! The run rows: their order and states, an update replacing them in place, and the footer's count.

use serde_json::{Value, json};

use super::super::board_payload::{BoardStep, BoardTaskArgs, BoardTaskResult};
use super::super::board_state::BoardState;
use super::{Mark, Row, footer, rows};

fn step(index: i32, text: &str, state: &str) -> Value {
    json!({"index": index, "text": text, "tool": null, "detail": null, "state": state, "badge": null,
        "explanation": null, "progress": null, "startedAt": null, "endedAt": null, "attempt": null,
        "maxAttempts": null, "questionKey": null, "requiredScope": null, "scopeGranted": null})
}

fn steps(values: Vec<Value>) -> Vec<BoardStep> {
    serde_json::from_value(Value::Array(values)).expect("steps")
}

fn result(values: Vec<Value>) -> BoardTaskResult {
    serde_json::from_value(json!({"steps": values, "questions": [], "feed": [],
        "nextBeforeSeq": null, "nextQuestionCursor": null}))
    .expect("a task result")
}

fn row(text: &str, mark: Mark, progress: Option<i32>, value: Option<&str>) -> Row {
    Row {
        text: text.into(),
        tool: None,
        detail: None,
        mark,
        progress,
        value: value.map(Into::into),
    }
}

/// 2026-10-29T12:00:00Z in Unix ms.
const NOON: i64 = 1_793_275_200_000;

#[test]
fn rows_render_in_order_with_their_states() {
    let mut done = step(0, "Traced the failing event", "done");
    (done["startedAt"], done["endedAt"]) = (
        json!("2026-10-29T12:00:00+00:00"),
        json!("2026-10-29T12:00:12.5+00:00"),
    );
    let mut running = step(1, "Running the failing case", "running");
    (running["tool"], running["detail"], running["progress"]) =
        (json!("pnpm"), json!("stripe-webhook.test.ts"), json!(40));
    running["startedAt"] = json!("2026-10-29T12:00:12+00:00");
    let mut ticking = step(2, "Reading the logs", "running");
    ticking["startedAt"] = json!("2026-10-29T11:58:55+00:00");
    let future = step(3, "Editing the refund branch", "future");
    let failed = step(4, "Timed out on attempt 1", "failed");
    let mut badged = step(5, "Needs your answer", "waiting");
    badged["badge"] = json!("needs-you");

    let shown = rows(
        &steps(vec![done, running, ticking, future, failed, badged]),
        Some(NOON + 65_000),
    );
    let mut tool_row = row(
        "Running the failing case",
        Mark::Running,
        Some(40),
        Some("40%"),
    );
    (tool_row.tool, tool_row.detail) = (Some("pnpm".into()), Some("stripe-webhook.test.ts".into()));
    assert_eq!(
        shown,
        vec![
            row("Traced the failing event", Mark::Done, None, Some("0:12")),
            tool_row,
            row("Reading the logs", Mark::Running, None, Some("2:10")),
            row("Editing the refund branch", Mark::Future, None, None),
            row("Timed out on attempt 1", Mark::Word("Failed"), None, None),
            row("Needs your answer", Mark::Word("Waiting"), None, None),
        ]
    );
}

#[test]
fn rows_update_in_place_when_a_new_result_arrives() {
    let mut state = BoardState::default();
    let read: BoardTaskArgs = state.select("local", "t1").expect("a detail read");
    let mut first = step(0, "Running the failing case", "running");
    first["progress"] = json!(10);
    state.apply_detail(
        &read,
        result(vec![first, step(1, "Editing the refund branch", "future")]),
    );
    let before = rows(&state.detail.as_ref().unwrap().steps, None);
    assert_eq!(before[0].value.as_deref(), Some("10%"));

    let mut done = step(0, "Reproduced the 500", "done");
    (done["startedAt"], done["endedAt"]) = (
        json!("2026-10-29T12:00:00+00:00"),
        json!("2026-10-29T12:00:09+00:00"),
    );
    let mut next = step(1, "Editing the refund branch", "running");
    next["progress"] = json!(0);
    state.apply_detail(&read, result(vec![done, next]));
    let after = rows(&state.detail.as_ref().unwrap().steps, None);
    assert_eq!(
        after,
        vec![
            row("Reproduced the 500", Mark::Done, None, Some("0:09")),
            row(
                "Editing the refund branch",
                Mark::Running,
                Some(0),
                Some("0%")
            ),
        ]
    );
}

#[test]
fn footer_counts_done_over_total() {
    let shown = steps(vec![
        step(0, "Traced the failing event", "done"),
        step(1, "Read 4 pages", "done"),
        step(2, "Reproduced the 500", "done"),
        step(3, "Running the failing case", "running"),
        step(4, "Timed out", "failed"),
        step(5, "Editing the refund branch", "future"),
    ]);
    assert_eq!(footer(&shown).as_deref(), Some("3 of 6 done"));
}

#[test]
fn a_task_without_steps_draws_as_today() {
    let detail = result(vec![]);
    assert!(rows(&detail.steps, Some(NOON)).is_empty());
    assert_eq!(footer(&detail.steps), None);
}
