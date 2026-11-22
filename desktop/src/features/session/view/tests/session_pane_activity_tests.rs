//! A running turn's tool calls on the fake client: they draw in its activity card, not as rows, and fold under
//! "Worked for…" when the turn ends.

use gpui::TestAppContext;
use serde_json::{Value, json};

use super::tests::{jump, live, open, page, row, show, user};
use crate::core::socket::row_source::ExpectedRowSource;
use crate::features::session::client::session_client::RowUpdate;
use crate::features::session::model::entries::Entry;
use crate::features::session::view::session_action::SessionAction;

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

// Pins: g0-session#12
#[gpui::test]
fn the_card_counts_from_the_turn_row_once_it_arrives(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![user(1, "go")], false, Some("r1"))));
    jump(&client, 1, cx);
    live(
        &client,
        row(2, "turn", None, json!({ "state": "started" })),
        cx,
    );
    assert_eq!(
        cx.update(|_, cx| pane.read(cx).live_since.clone()),
        Some(("r1".to_string(), 2)),
        "the backend's start, not when the pane first saw the run"
    );
}

#[gpui::test]
fn finished_fold_keeps_tools_after_backend_recovers_the_turn(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    let mut rows = vec![
        user(1, "go"),
        row(2, "turn", None, json!({ "state": "started" })),
        tool(3, "t1", "completed"),
    ];
    client.queue_answer(Ok(page(rows.clone(), false, Some("r1"))));
    jump(&client, 3, cx);

    // Recovery closes the run with an error and a failed turn, without stats.
    for next in [
        row(
            4,
            "error",
            None,
            json!({ "message": "The backend stopped", "code": "backend_stopped" }),
        ),
        row(5, "turn", None, json!({ "state": "failed" })),
    ] {
        live(&client, next.clone(), cx);
        rows.push(next);
    }
    assert_fold_opens_to(&pane, "tool:t1", cx);

    // Reopening from a saved page still has the terminal row as evidence.
    show(&pane, "s2", cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(rows, false, None)));
    jump(&client, 5, cx);
    assert_fold_opens_to(&pane, "tool:t1", cx);
}

#[gpui::test]
fn finished_fold_appears_when_its_only_background_work_settles(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    let helper = |seq, state| {
        row(
            seq,
            "subagent",
            Some("subagent:child"),
            json!({
                "sessionId": "child", "title": "Helper", "state": state, "canStop": state != "idle", "helper": true,
            }),
        )
    };
    let rows = vec![
        user(1, "go"),
        row(2, "turn", None, json!({ "state": "started" })),
        helper(3, "running"),
        row(
            4,
            "stats",
            None,
            json!({ "runId": "r1", "status": "completed", "toolCalls": 0 }),
        ),
        row(5, "turn", None, json!({ "state": "finished" })),
    ];
    client.queue_answer(Ok(page(rows, false, None)));
    jump(&client, 5, cx);

    live(&client, helper(6, "idle"), cx);
    assert_fold_opens_to(&pane, "subagent:subagent:child", cx);
}

#[gpui::test]
fn finished_fold_opens_to_loaded_tools_after_remote_source_is_refused(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    pane.update(cx, |pane, _| {
        pane.row_source = Some(ExpectedRowSource {
            machine: "owner".into(),
            generation: 3,
            route_machine: Some("remote".into()),
        });
    });
    let rows = vec![
        user(1, "go"),
        row(2, "turn", None, json!({ "state": "started" })),
        tool(3, "t1", "completed"),
        row(
            4,
            "stats",
            None,
            json!({ "runId": "r1", "status": "completed", "toolCalls": 1 }),
        ),
        row(5, "turn", None, json!({ "state": "finished" })),
    ];
    client.queue_answer(Ok(page(rows, false, None)));
    jump(&client, 5, cx);

    cx.update(|_, cx| {
        client.push(
            RowUpdate::Refused {
                code: "source_changed".into(),
                message: "The selected row source changed.".into(),
            },
            cx,
        )
    });
    cx.run_until_parked();
    let calls = client.calls();
    assert_fold_opens_to(&pane, "tool:t1", cx);
    assert_eq!(
        client.calls(),
        calls,
        "opening loaded rows needs no source call"
    );
}

/// The closed turn has a heading, and opening it makes the expected work reachable.
fn assert_fold_opens_to(
    pane: &gpui_kit::Entity<super::SessionPane>,
    work_key: &str,
    cx: &mut gpui_kit::VisualTestContext,
) {
    let keys = |cx: &mut gpui_kit::VisualTestContext| {
        cx.update(|_, cx| {
            let pane = pane.read(cx);
            pane.entries(cx)
                .iter()
                .map(|entry| entry.key(&pane.view.items))
                .collect::<Vec<_>>()
        })
    };
    let closed = keys(cx);
    assert!(
        closed.iter().any(|key| key == "fold:r1"),
        "missing fold: {closed:?}"
    );
    assert!(!closed.iter().any(|key| key == work_key));

    pane.update_in(cx, |pane, window, cx| {
        pane.on_action(
            SessionAction::ToggleFold {
                run_id: "r1".into(),
            },
            window,
            cx,
        );
    });
    let open = keys(cx);
    assert!(open.iter().any(|key| key == "fold:r1"));
    assert!(
        open.iter().any(|key| key == work_key),
        "missing work: {open:?}"
    );
}
