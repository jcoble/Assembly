//! A running turn's tool calls on the fake client: they draw in its activity card, not as rows, and fold under
//! "Worked for…" when the turn ends.

use gpui::TestAppContext;
use serde_json::{Value, json};

use super::tests::{jump, live, open, page, row, show, user};
use crate::features::session::model::entries::Entry;

fn tool(seq: i64, item: &str, state: &str) -> Value {
    row(
        seq,
        "tool",
        Some(item),
        json!({ "name": "Bash", "state": state, "summary": "cargo test", "hasDetail": false }),
    )
}

// Pins: g0-session#9, g0-session#12
#[gpui::test]
fn a_running_turns_tool_calls_sit_in_its_card_then_fold_when_it_ends(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    let rows = vec![
        user(1, "go"),
        row(2, "turn", None, json!({ "state": "started" })),
        tool(3, "t1", "completed"),
    ];
    client.queue_answer(Ok(page(rows, false, Some("r1"))));
    jump(&client, 3, cx);
    let entries =
        |cx: &mut gpui_kit::VisualTestContext| cx.update(|_, cx| pane.read(cx).entries(cx));
    assert_eq!(entries(cx), [Entry::Item(0), Entry::Working]);
    live(&client, tool(4, "t2", "started"), cx);
    assert_eq!(
        entries(cx),
        [Entry::Item(0), Entry::Working],
        "a new step goes in the card"
    );
    live(
        &client,
        row(
            5,
            "stats",
            None,
            json!({ "runId": "r1", "status": "completed", "durationMs": 4200, "toolCalls": 2 }),
        ),
        cx,
    );
    live(
        &client,
        row(6, "turn", None, json!({ "state": "finished" })),
        cx,
    );
    let ended = entries(cx);
    assert!(!ended.contains(&Entry::Working), "the card goes");
    assert!(
        ended
            .iter()
            .any(|entry| matches!(entry, Entry::FoldHeading { run_id, .. } if run_id == "r1")),
        "its work folds under the turn's heading: {ended:?}"
    );
}
