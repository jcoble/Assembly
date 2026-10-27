//! The pane's actions on the fake client: opening a tool call and its detail, a fold, answering an ask, stopping a
//! task.

use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};
use serde_json::{Value, json};

use super::SessionPane;
use super::tests::{jump, live, open, page, row, show, user};
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::model::entries::Entry;
use crate::features::session::rows::tool::ToolState;
use crate::features::session::view::session_action::SessionAction;

fn tool(seq: i64, state: &str) -> Value {
    row(
        seq,
        "tool",
        Some("t1"),
        json!({ "name": "Edit", "state": state, "summary": "src/main.rs", "hasDetail": true }),
    )
}

fn detail(diff: &str) -> Value {
    json!({ "diff": diff, "truncated": false, "outputBytes": 0, "diffBytes": diff.len() })
}

fn act(pane: &Entity<SessionPane>, action: SessionAction, cx: &mut VisualTestContext) {
    pane.update_in(cx, |pane, window, cx| pane.on_action(action, window, cx));
    cx.run_until_parked();
}

fn toggle(pane: &Entity<SessionPane>, key: &str, cx: &mut VisualTestContext) {
    act(pane, SessionAction::Toggle { key: key.into() }, cx);
}

/// A session open at its first page, with no calls left over.
fn opened(
    rows: Vec<Value>,
    cx: &mut TestAppContext,
) -> (Entity<SessionPane>, FakeClient, &mut VisualTestContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    let last = rows
        .iter()
        .map(|row| row["seq"].as_i64().unwrap())
        .max()
        .unwrap_or(0);
    client.queue_answer(Ok(page(rows, false, Some("r1"))));
    jump(&client, last, cx);
    (pane, client, cx)
}

fn calls_after(client: &FakeClient, skip: usize) -> Vec<(String, Value)> {
    client.calls().into_iter().skip(skip).collect()
}

// Pins: g0-session#14, g0-session#15, g0-session#34
#[gpui::test]
fn an_opened_tool_call_reads_its_detail_once_and_again_when_its_state_changes(
    cx: &mut TestAppContext,
) {
    let (pane, client, cx) = opened(vec![user(1, "go"), tool(2, "started")], cx);
    client.queue_answer(Ok(detail("")));
    toggle(&pane, "tool:t1", cx);
    assert_eq!(
        calls_after(&client, 1),
        [(
            "rows/tool".to_string(),
            json!({ "session": "s1", "item": "t1" })
        )]
    );
    let kept = |cx: &mut VisualTestContext| {
        cx.update(|_, cx| {
            pane.read(cx)
                .kept
                .get("tool:t1")
                .cloned()
                .unwrap_or_default()
        })
    };
    assert!(
        kept(cx).toggled_at.is_some(),
        "its chevron turns and its detail eases in"
    );
    assert!(kept(cx).open);
    assert_eq!(
        kept(cx).detail.map(|(state, _)| state),
        Some(ToolState::Started)
    );
    toggle(&pane, "tool:t1", cx);
    toggle(&pane, "tool:t1", cx);
    assert_eq!(
        client.calls().len(),
        2,
        "opening it again at the same state reads nothing"
    );
    client.queue_answer(Ok(detail("+fn main() {}")));
    live(&client, tool(3, "completed"), cx);
    assert_eq!(
        client.calls().len(),
        3,
        "the finished edit's diff is read while it's open"
    );
    let (state, read) = kept(cx).detail.unwrap();
    assert_eq!(
        (state, read.diff.as_deref()),
        (ToolState::Completed, Some("+fn main() {}"))
    );
}

// Pins: g0-session#14
#[gpui::test]
fn a_closed_tool_call_reads_nothing_when_its_state_changes(cx: &mut TestAppContext) {
    let (_pane, client, cx) = opened(vec![user(1, "go"), tool(2, "started")], cx);
    live(&client, tool(3, "completed"), cx);
    assert_eq!(client.calls().len(), 1);
}

fn finished_turn() -> Vec<Value> {
    vec![
        user(1, "go"),
        row(2, "turn", None, json!({ "state": "started" })),
        tool(3, "completed"),
        row(4, "message", Some("m1"), json!({ "blocks": [] })),
        row(
            5,
            "stats",
            None,
            json!({ "runId": "r1", "status": "completed", "durationMs": 4200, "toolCalls": 1 }),
        ),
        row(6, "turn", None, json!({ "state": "finished" })),
    ]
}

fn entries(pane: &Entity<SessionPane>, cx: &mut VisualTestContext) -> Vec<Entry> {
    cx.update(|_, cx| pane.read(cx).entries(cx))
}

// Pins: g0-session#9, g0-session#34
#[gpui::test]
fn a_finished_turns_fold_opens_and_closes_by_its_run(cx: &mut TestAppContext) {
    let (pane, _client, cx) = open(cx);
    show(&pane, "s1", cx);
    _client.queue_answer(Ok(page(finished_turn(), false, None)));
    jump(&_client, 6, cx);
    let tool_shown = |entries: &[Entry], pane: &Entity<SessionPane>, cx: &mut VisualTestContext| {
        cx.update(|_, cx| {
            let items = &pane.read(cx).view.items;
            entries
                .iter()
                .any(|entry| matches!(entry, Entry::Item(at) if items[*at].key == "tool:t1"))
        })
    };
    let closed = entries(&pane, cx);
    assert!(
        closed
            .iter()
            .any(|entry| matches!(entry, Entry::FoldHeading { run_id, .. } if run_id == "r1"))
    );
    assert!(!tool_shown(&closed, &pane, cx));
    act(
        &pane,
        SessionAction::ToggleFold {
            run_id: "r1".into(),
        },
        cx,
    );
    assert!(tool_shown(&entries(&pane, cx), &pane, cx));
    // Opened, its chevron turns and the work it shows fades in, as a new row does (g0-session#34).
    cx.update(|_, cx| {
        let pane = pane.read(cx);
        assert!(pane.kept["fold:r1"].toggled_at.is_some());
        let fading = pane
            .entries(cx)
            .iter()
            .filter_map(|entry| pane.fresh_key(entry))
            .collect::<Vec<_>>();
        assert_eq!(fading, ["tool:t1"]);
    });
    act(
        &pane,
        SessionAction::ToggleFold {
            run_id: "r1".into(),
        },
        cx,
    );
    assert!(!tool_shown(&entries(&pane, cx), &pane, cx));
}

// Pins: g0-session#16
#[gpui::test]
fn answering_an_ask_sends_its_answer(cx: &mut TestAppContext) {
    let (pane, client, cx) = opened(vec![user(1, "go")], cx);
    client.queue_answer(Ok(json!({ "ok": true })));
    let answer = json!({ "optionId": "allow" });
    act(
        &pane,
        SessionAction::Answer {
            ask_id: "a1".into(),
            answer: answer.clone(),
        },
        cx,
    );
    assert_eq!(
        calls_after(&client, 1),
        [(
            "send/answer".to_string(),
            json!({ "session": "s1", "ask": "a1", "answer": answer })
        )]
    );
}

// Pins: g0-session#17
#[gpui::test]
fn stop_stops_the_task(cx: &mut TestAppContext) {
    let (pane, client, cx) = opened(vec![user(1, "go")], cx);
    client.queue_answer(Ok(json!({ "ok": true })));
    act(
        &pane,
        SessionAction::StopTask {
            task: "task-1".into(),
        },
        cx,
    );
    assert_eq!(
        calls_after(&client, 1),
        [(
            "send/stopTask".to_string(),
            json!({ "session": "s1", "task": "task-1" })
        )]
    );
}

// Pins: g0-session#15
#[gpui::test]
fn an_opened_prompt_stays_open_until_closed(cx: &mut TestAppContext) {
    let (pane, _client, cx) = opened(vec![user(1, "go")], cx);
    toggle(&pane, "seq:1", cx);
    assert!(cx.update(|_, cx| pane.read(cx).kept["seq:1"].open));
    toggle(&pane, "seq:1", cx);
    assert!(!cx.update(|_, cx| pane.read(cx).kept["seq:1"].open));
}

// Pins: g0-session#12
#[gpui::test]
fn the_working_line_counts_by_the_backend_clock_only_while_a_turn_runs(cx: &mut TestAppContext) {
    let (pane, client, cx) = opened(vec![user(1, "go")], cx);
    assert!(client.ticking());
    cx.update(|_, cx| client.tick(42_000, cx));
    assert_eq!(cx.update(|_, cx| pane.read(cx).now), Some(42_000));
    live(
        &client,
        row(
            2,
            "stats",
            None,
            json!({ "runId": "r1", "status": "completed", "toolCalls": 0 }),
        ),
        cx,
    );
    live(
        &client,
        row(3, "turn", None, json!({ "state": "finished" })),
        cx,
    );
    assert!(!client.ticking(), "nothing ticks once the turn is over");
}

// Pins: g0-session#12
#[gpui::test]
fn a_run_whose_start_is_not_loaded_counts_from_when_it_was_first_seen(cx: &mut TestAppContext) {
    let (pane, client, cx) = opened(vec![user(1, "go")], cx);
    let since = cx.update(|_, cx| pane.read(cx).live_since.clone());
    let (run, at) = since.expect("the page's active run is going");
    assert_eq!(run, "r1");
    live(&client, user(2, "more"), cx);
    assert_eq!(
        cx.update(|_, cx| pane.read(cx).live_since.clone()),
        Some(("r1".to_string(), at)),
        "it doesn't restart on each refresh"
    );
}

// Pins: g0-session#29
#[gpui::test]
fn a_backend_restart_mid_turn_does_not_leave_working_on(cx: &mut TestAppContext) {
    let (pane, client, cx) = opened(vec![user(1, "go")], cx);
    assert!(client.ticking());
    client.queue_answer(Ok(page(vec![user(1, "go")], false, None)));
    jump(&client, 1, cx);
    assert!(!client.ticking(), "the new page says nothing runs");
    assert!(
        !cx.update(|_, cx| pane.read(cx).entries(cx))
            .contains(&Entry::Working)
    );
}

// Pins: g0-session#1
#[gpui::test]
fn a_failed_first_page_keeps_live_rows_waiting_until_try_again_loads_it(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Err(
        crate::features::session::client::session_client::CallError::Failed("boom".into()),
    ));
    jump(&client, 1, cx);
    live(&client, user(2, "later"), cx);
    assert!(
        cx.update(|_, cx| pane.read(cx).view.items.is_empty()),
        "nothing shows without its history"
    );
    client.queue_answer(Ok(page(vec![user(1, "first")], false, None)));
    act(&pane, SessionAction::RetryPage, cx);
    assert_eq!(
        client
            .calls()
            .last()
            .map(|(call, args)| (call.clone(), args["before"].clone())),
        Some(("rows/page".to_string(), json!(2)))
    );
    live(&client, user(3, "last"), cx);
    let texts: Vec<String> = cx.update(|_, cx| {
        pane.read(cx)
            .view
            .items
            .iter()
            .map(|item| item.key.clone())
            .collect()
    });
    assert_eq!(texts, ["seq:1", "seq:2", "seq:3"]);
}

// Pins: g0-session#16
#[gpui::test]
fn a_questions_form_sends_the_choices_picked(cx: &mut TestAppContext) {
    let schema = json!({ "type": "object", "properties": {
        "zone": { "type": "string", "enum": ["east", "west"] },
        "tags": { "type": "array", "items": { "enum": ["a", "b"] } },
        "note": { "type": "string" },
    } });
    let ask = row(
        2,
        "ask",
        Some("ask:a1"),
        json!({ "kind": "question", "state": "open", "schema": schema }),
    );
    let (pane, client, cx) = opened(vec![user(1, "go"), ask], cx);
    pane.update_in(cx, |pane, window, cx| {
        pane.make_form(&Entry::Item(1), window, cx)
    });
    let pick = |field: &str, value: &str, many: bool| SessionAction::Pick {
        ask_id: "a1".into(),
        field: field.into(),
        value: value.into(),
        many,
    };
    for action in [
        pick("zone", "east", false),
        pick("zone", "west", false),
        pick("tags", "a", true),
        pick("tags", "b", true),
        pick("tags", "a", true),
    ] {
        act(&pane, action, cx);
    }
    client.queue_answer(Ok(json!({ "ok": true })));
    act(
        &pane,
        SessionAction::SubmitAsk {
            ask_id: "a1".into(),
        },
        cx,
    );
    assert_eq!(
        calls_after(&client, 1),
        [(
            "send/answer".to_string(),
            json!({ "session": "s1", "ask": "a1", "answer": { "action": "accept", "content": { "zone": "west", "tags": ["b"] } } })
        )]
    );
}
