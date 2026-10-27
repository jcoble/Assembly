//! `SessionView`'s fold of rows into items, ported from app/test/rows.test.ts. The fixtures are the web view's.

use serde_json::json;

use super::change::Change;
use super::item::{Item, ItemKind};
use super::session_view::SessionView;
use super::test_rows::{all, fixture, keys, live, message, newest_form, row};
use crate::features::session::rows::plan::PlanStatus;
use crate::features::session::rows::row::{Row, TurnState};
use crate::features::session::rows::tool::ToolState;

// Pins: g0-session#4
#[test]
fn a_delta_keeps_the_first_from_blocks_and_replaces_the_rest_and_the_message_row_is_final() {
    let rows = fixture("rows-text.json");
    let mut view = live(SessionView::default(), &rows[..4]);
    assert_eq!(
        message(&view, "message:m1"),
        (vec!["paragraph", "table"], false)
    );
    let first = match &view.items[1].kind {
        ItemKind::Message { blocks, .. } => blocks[0].clone(),
        _ => panic!("not a message"),
    };
    view.apply(&rows[4]);
    assert_eq!(
        message(&view, "message:m1"),
        (vec!["paragraph", "table", "code"], false)
    );
    let ItemKind::Message { blocks, .. } = &view.items[1].kind else {
        panic!()
    };
    assert_eq!(
        blocks[0], first,
        "blocks before `from` are kept as they were"
    );
    view.apply(&rows[5]);
    view.apply(&rows[6]);
    assert_eq!(
        message(&view, "message:m1"),
        (vec!["paragraph", "table", "code", "list"], true)
    );
}

// Pins: g0-session#1, g0-session#3
#[test]
fn a_page_in_newest_form_then_live_rows_folds_to_the_same_items_as_every_row_live() {
    let all = all();
    let every_row = live(SessionView::default(), &all);
    let mut view = SessionView::default();
    view.prepend_page(
        newest_form(
            &all.iter()
                .filter(|r| r.seq <= 15)
                .cloned()
                .collect::<Vec<_>>(),
        ),
        false,
    );
    let view = live(
        view,
        &all.iter()
            .filter(|r| r.seq > 15)
            .cloned()
            .collect::<Vec<_>>(),
    );
    assert_eq!(view.items, every_row.items);
    assert_eq!(
        keys(&view),
        [
            "user:u1",
            "message:m1",
            "seq:9",
            "user:u2",
            "thinking:th1",
            "tool:tool1",
            "tool:tool2",
            "tool:tool3",
            "tool:tool4",
            "message:m2",
            "seq:25",
            "seq:27",
        ]
    );
    let mut whole = SessionView::default();
    whole.prepend_page(newest_form(&all), false);
    assert_eq!(whole.items, every_row.items);
}

// Pins: g0-session#4
#[test]
fn a_page_read_mid_message_holds_it_as_one_delta_from_0_and_live_deltas_continue_it() {
    let all = all();
    let every_row = live(SessionView::default(), &all);
    let page = newest_form(
        &all.iter()
            .filter(|r| r.seq <= 5)
            .cloned()
            .collect::<Vec<_>>(),
    );
    assert_eq!(
        page.iter()
            .filter(|r| r.item_id.as_deref() == Some("m1"))
            .count(),
        1
    );
    let mut view = SessionView::default();
    view.prepend_page(page, false);
    let view = live(
        view,
        &all.iter()
            .filter(|r| r.seq > 5)
            .cloned()
            .collect::<Vec<_>>(),
    );
    assert_eq!(view.items, every_row.items);
}

// Pins: g0-session#2
#[test]
fn a_page_places_items_by_their_first_seq_as_live_rows_do() {
    let tool = |seq, state: &str| {
        row(
            seq,
            "r",
            Some("A"),
            "tool",
            json!({ "name": "Bash", "state": state, "summary": "a", "hasDetail": false }),
        )
    };
    let rows = vec![
        tool(1, "started"),
        row(
            2,
            "r",
            Some("B"),
            "delta",
            json!({ "from": 0, "text": "b", "blocks": [] }),
        ),
        row(3, "r", Some("B"), "message", json!({ "blocks": [] })),
        tool(4, "completed"),
    ];
    assert_eq!(
        keys(&live(SessionView::default(), &rows)),
        ["tool:A", "message:B"]
    );
    let mut by_seq = newest_form(&rows);
    assert_eq!(
        by_seq
            .iter()
            .map(|r| (r.seq, r.first_seq.unwrap()))
            .collect::<Vec<_>>(),
        [(4, 1), (3, 2)]
    );
    by_seq.sort_by_key(|r| r.seq);
    let mut view = SessionView::default();
    view.prepend_page(by_seq, true);
    assert_eq!(keys(&view), ["tool:A", "message:B"]);
    assert_eq!(
        view.oldest,
        Some(1),
        "the next page is before the lowest first seq"
    );
}

// Pins: g0-session#12
#[test]
fn the_active_run_is_the_pages_or_a_sent_run_until_a_turn_row_ends_it() {
    let all = all();
    let page: Vec<Row> = newest_form(&all)
        .into_iter()
        .filter(|r| (20..=24).contains(&r.seq))
        .collect();
    let mut view = SessionView::default();
    view.prepend_page(page, true);
    assert_eq!(view.turn, None);
    assert_eq!(view.active_run(Some("r2")).as_deref(), Some("r2"));
    assert_eq!(view.active_run(None), None);
    let view = live(
        view,
        &all.iter()
            .filter(|r| r.seq >= 25)
            .cloned()
            .collect::<Vec<_>>(),
    );
    assert_eq!(view.active_run(Some("r2")), None);

    let idle = live(SessionView::default(), &fixture("rows-text.json"));
    assert_eq!(idle.active_run(None), None);
    assert_eq!(idle.active_run(Some("r2")).as_deref(), Some("r2"));
    let idle = live(
        idle,
        &all.iter()
            .filter(|r| (10..=11).contains(&r.seq))
            .cloned()
            .collect::<Vec<_>>(),
    );
    assert_eq!(idle.active_run(Some("r2")).as_deref(), Some("r2"));
    assert_eq!(idle.active_run(None).as_deref(), Some("r2"));
}

// Pins: g0-session#12
#[test]
fn a_sent_run_that_ended_stays_ended_when_a_run_the_agent_continues_by_itself_ends_after_it() {
    let stats = |run: &str| {
        json!({ "runId": run, "status": "completed", "startedAt": 0, "durationMs": 1, "firstTokenMs": 1, "model": "m",
            "inputTokens": 4, "outputTokens": 121, "cacheHitPct": 94.9, "costUsd": 0.05, "toolCalls": 1, "branch": null,
            "head": null, "prevBranch": null, "prevHead": null })
    };
    let view = live(
        SessionView::default(),
        &[
            row(
                1,
                "r1",
                None,
                "user",
                json!({ "text": "Run it in the background" }),
            ),
            row(2, "r1", None, "turn", json!({ "state": "started" })),
            row(3, "r1", None, "stats", stats("r1")),
            row(4, "r1", None, "turn", json!({ "state": "finished" })),
            row(
                5,
                "r1",
                Some("context:r1"),
                "context",
                json!({ "used": 43115, "size": 1000000 }),
            ),
            row(
                6,
                "r2",
                None,
                "turn",
                json!({ "state": "started", "reason": "Continued after a background task finished" }),
            ),
            row(7, "r2", None, "stats", stats("r2")),
            row(8, "r2", None, "turn", json!({ "state": "finished" })),
        ],
    );
    assert_eq!(view.active_run(Some("r1")), None);
}

// Pins: g0-session#12
#[test]
fn a_running_turn_counts_from_its_turn_row_so_reopening_it_mid_turn_keeps_its_time() {
    let mut started = row(7, "r2", None, "turn", json!({ "state": "started" }));
    started.created_at = 1_000;
    let mut view = SessionView::default();
    view.prepend_page(vec![started.clone()], false);
    assert_eq!(view.run_started_at("r2"), Some(1_000));
    assert_eq!(view.run_started_at("r3"), None);
    view.apply(&Row {
        seq: 8,
        payload: json!({ "state": "finished" }),
        created_at: 9_000,
        ..started
    });
    assert_eq!(view.run_started_at("r2"), None);
}

#[test]
fn the_fold_carries_tool_state_the_plan_turn_state_and_stats() {
    let all = all();
    let view = live(
        SessionView::default(),
        &all.iter()
            .filter(|r| r.seq <= 20)
            .cloned()
            .collect::<Vec<_>>(),
    );
    let turn = view.turn.clone().unwrap();
    assert_eq!(
        (turn.state, turn.run_id.as_deref()),
        (TurnState::Started, Some("r2"))
    );
    let tools: Vec<(String, ToolState)> = view
        .items
        .iter()
        .filter_map(|item| match &item.kind {
            ItemKind::Tool { tool, .. } => Some((tool.name.clone(), tool.state)),
            _ => None,
        })
        .collect();
    assert_eq!(
        tools,
        [
            ("Bash".into(), ToolState::Completed),
            ("Read".into(), ToolState::Failed),
            ("Edit".into(), ToolState::Completed),
            ("TodoWrite".into(), ToolState::Started),
        ]
    );
    assert!(view.plan.is_none());

    let view = live(
        view,
        &all.iter()
            .filter(|r| r.seq > 20)
            .cloned()
            .collect::<Vec<_>>(),
    );
    let statuses: Vec<PlanStatus> = view
        .plan
        .as_ref()
        .unwrap()
        .1
        .iter()
        .map(|step| step.status)
        .collect();
    assert_eq!(statuses, [PlanStatus::Completed, PlanStatus::InProgress]);
    assert_eq!(view.turn.as_ref().unwrap().state, TurnState::Failed);
    assert_eq!(view.stats["r1"].status, "completed");
    assert_eq!(
        view.stats["r2"].prev_head.as_deref(),
        Some("7fd1a60b01f91b314f59955a4e4d4e80d8edf11d")
    );
    let error = view
        .items
        .iter()
        .find(|item| matches!(item.kind, ItemKind::Error { .. }))
        .unwrap();
    assert!(
        matches!(&error.kind, ItemKind::Error { message, .. } if message == "API Error: 529 Overloaded")
    );
    assert_eq!(error.run_id.as_deref(), Some("r2"));
}

// Pins: g0-session#2, g0-session#7
#[test]
fn an_older_page_goes_in_above_and_keeps_every_loaded_item_plan_and_turn() {
    let all = all();
    let mut view = SessionView::default();
    view.prepend_page(
        newest_form(&all)
            .into_iter()
            .filter(|r| r.seq >= 10)
            .collect(),
        true,
    );
    assert_eq!((view.oldest, view.has_before), (Some(10), true));
    let loaded: Vec<Item> = view.items.clone();
    let plan = view.plan.clone();
    let count = view.prepend_page(
        newest_form(&all)
            .into_iter()
            .filter(|r| r.seq < 10)
            .collect(),
        false,
    );
    assert_eq!(count, 3);
    assert_eq!((view.oldest, view.has_before), (Some(1), false));
    assert_eq!(&keys(&view)[..3], ["user:u1", "message:m1", "seq:9"]);
    assert_eq!(view.items[3..], loaded[..], "every loaded item is kept");
    assert_eq!(view.plan, plan, "an older page never replaces a newer plan");
    assert_eq!(
        view.turn.as_ref().unwrap().state,
        TurnState::Failed,
        "nor the newer turn state"
    );
}

// Pins: g0-session#3
#[test]
fn a_row_of_an_item_already_loaded_in_a_newer_form_is_skipped_by_an_older_page() {
    let all = all();
    let mut view = live(
        SessionView::default(),
        &all.iter()
            .filter(|r| (14..=17).contains(&r.seq))
            .cloned()
            .collect::<Vec<_>>(),
    );
    view.prepend_page(all.iter().filter(|r| r.seq == 14).cloned().collect(), true);
    let tools: Vec<&Item> = view
        .items
        .iter()
        .filter(|item| item.key == "tool:tool1")
        .collect();
    assert_eq!(tools.len(), 1);
    assert!(
        matches!(&tools[0].kind, ItemKind::Tool { tool, .. } if tool.state == ToolState::Completed)
    );
}

// Pins: g0-session#17
#[test]
fn a_task_row_stays_one_item_as_its_state_changes() {
    let task = |seq, state: &str| {
        row(
            seq,
            "r1",
            Some("task:b1"),
            "task",
            json!({ "name": "Background command", "state": state, "canStop": false, "toolCallId": "t1" }),
        )
    };
    let view = live(
        SessionView::default(),
        &[task(1, "running"), task(2, "completed")],
    );
    assert_eq!(keys(&view), ["task:task:b1"]);
    assert!(
        matches!(&view.items[0].kind, ItemKind::Task { task_id, task } if task_id == "b1" && task.state == "completed")
    );
}

// Pins: g0-session#17
#[test]
fn a_subagent_row_stays_one_item_keyed_by_its_child_session() {
    let view = live(
        SessionView::default(),
        &[
            row(
                1,
                "r1",
                Some("subagent:s2"),
                "subagent",
                json!({ "sessionId": "c1", "title": "Explore", "state": "running", "canStop": true }),
            ),
            row(
                2,
                "r1",
                Some("subagent:s2"),
                "subagent",
                json!({ "sessionId": "c1", "title": "Explore", "state": "idle", "stopReason": "end_turn", "canStop": false, "toolCallId": "t1" }),
            ),
        ],
    );
    assert_eq!(keys(&view), ["subagent:subagent:s2"]);
    let ItemKind::Subagent {
        acp_session_id,
        subagent,
    } = &view.items[0].kind
    else {
        panic!("not a subagent")
    };
    assert_eq!(
        (
            acp_session_id.as_str(),
            subagent.state.as_str(),
            subagent.tool_call_id.as_deref()
        ),
        ("s2", "idle", Some("t1"))
    );
}

#[test]
fn a_run_the_agent_started_itself_shows_its_reason_where_a_prompt_would_be() {
    let view = live(
        SessionView::default(),
        &[
            row(
                1,
                "r2",
                None,
                "turn",
                json!({ "state": "started", "reason": "Continued after a background task finished" }),
            ),
            row(2, "r2", None, "turn", json!({ "state": "finished" })),
        ],
    );
    assert_eq!(view.items.len(), 1);
    assert!(
        matches!(&view.items[0].kind, ItemKind::Note { text } if text == "Continued after a background task finished")
    );
    assert_eq!(view.turn.as_ref().unwrap().state, TurnState::Finished);
}

// Pins: g0-session#16
#[test]
fn an_ask_row_opens_a_card_that_its_settled_row_updates_in_place() {
    let options = json!([{ "optionId": "allow", "name": "Allow", "kind": "allow_once" }]);
    let view = live(
        SessionView::default(),
        &[
            row(
                1,
                "r",
                Some("ask:7"),
                "ask",
                json!({ "kind": "permission", "state": "open", "title": "Run ls", "options": options }),
            ),
            row(
                2,
                "r",
                Some("ask:7"),
                "ask",
                json!({ "kind": "permission", "state": "answered", "title": "Run ls", "options": options, "answer": { "optionId": "allow" } }),
            ),
        ],
    );
    assert_eq!(keys(&view), ["ask:ask:7"]);
    assert!(
        matches!(&view.items[0].kind, ItemKind::Ask { ask_id, ask } if ask_id == "7" && ask.state == "answered")
    );
}

// Pins: g0-session#28
#[test]
fn the_newest_context_report_wins_live_or_paged_and_draws_no_item() {
    let context = |seq: i64, used: i64| {
        row(
            seq,
            &format!("run-{seq}"),
            Some(&format!("context:run-{seq}")),
            "context",
            json!({ "used": used, "size": 1000 }),
        )
    };
    let mut view = live(SessionView::default(), &[context(5, 100), context(9, 300)]);
    assert_eq!(view.context, Some((9, 300, 1000)));
    assert!(view.items.is_empty());
    view.prepend_page(vec![context(2, 50)], false);
    assert_eq!(view.context.unwrap().1, 300);
    let mut fresh = SessionView::default();
    fresh.prepend_page(vec![context(2, 50)], false);
    assert_eq!(fresh.context.unwrap().1, 50);
}

// Pins: g0-session#3
#[test]
fn a_kind_this_build_does_not_know_or_cannot_read_shows_by_name() {
    let view = live(
        SessionView::default(),
        &[
            row(1, "r", None, "hologram", json!({ "x": 1 })),
            row(2, "r", Some("t"), "tool", json!({ "name": 3 })),
        ],
    );
    let kinds: Vec<&str> = view
        .items
        .iter()
        .map(|item| match &item.kind {
            ItemKind::Other { kind } => kind.as_str(),
            _ => "drawn",
        })
        .collect();
    assert_eq!(kinds, ["hologram", "tool"]);
}

// Pins: g0-session#4
#[test]
fn applying_a_row_says_what_changed_so_only_that_item_is_measured_again() {
    let rows = fixture("rows-text.json");
    let mut view = SessionView::default();
    assert_eq!(view.apply(&rows[0]), Change::Appended);
    assert_eq!(view.apply(&rows[1]), Change::Nothing);
    assert_eq!(view.apply(&rows[2]), Change::Appended);
    assert_eq!(view.apply(&rows[3]), Change::Updated(1));
}
