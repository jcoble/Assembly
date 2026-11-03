//! `SessionView`'s edge cases: a row it couldn't read, empty fields, and the newest seq it has applied.

use serde_json::json;

use super::change::Change;
use super::item::ItemKind;
use super::session_view::SessionView;
use super::test_rows::{all, keys, live, newest_form, row};
use crate::features::session::rows::row::Row;

#[test]
fn a_pending_prompt_has_no_run_and_its_own_user_row_replaces_it_once() {
    let mut view = SessionView::default();
    view.pending_prompt("c", "hello", "sending", 1);
    view.pending_prompt("c", "hello", "queued", 1);
    assert_eq!(view.items.len(), 1);
    assert_eq!(view.items[0].run_id, None);
    assert_eq!(view.active_run(None), None);
    let own: Row = serde_json::from_value(json!({"sessionId":"s","seq":5,"runId":"real",
        "itemId":"u","kind":"user","createdAt":2,"payload":{"text":"hello","clientId":"c"}}))
    .unwrap();
    view.apply(&own);
    view.apply(&own);
    view.pending_prompt("c", "hello", "queued", 1);
    assert_eq!(view.items.len(), 1);
    assert_eq!(view.items[0].run_id.as_deref(), Some("real"));
    assert!(view.pending.is_empty());
}

#[test]
fn a_reopen_page_reconciles_pending_by_client_not_text() {
    let mut view = SessionView::default();
    view.pending_prompt("ours", "same text", "queued", 1);
    let mut user: Row = serde_json::from_value(json!({"sessionId":"s","seq":5,"runId":"real",
        "itemId":"u","kind":"user","createdAt":2,"payload":{"text":"same text","clientId":"other"}})).unwrap();
    view.prepend_page(vec![user.clone()], false);
    assert_eq!(view.items.len(), 2);
    user.seq = 6;
    user.item_id = Some("u2".into());
    user.payload["clientId"] = json!("ours");
    view.prepend_page(vec![user], false);
    assert_eq!(view.items.len(), 2);
    assert!(view.pending.is_empty());
}

// Pins: g0-session#3
#[test]
fn a_readable_row_after_an_unreadable_one_draws_the_item_in_its_place() {
    let mut view = live(
        SessionView::default(),
        &[row(1, "r", Some("t"), "tool", json!({ "name": 3 }))],
    );
    let good = row(
        2,
        "r",
        Some("t"),
        "tool",
        json!({ "name": "Bash", "state": "completed", "hasDetail": false }),
    );
    assert_eq!(view.apply(&good), Change::Updated(0));
    assert_eq!(keys(&view), ["tool:t"]);
    assert!(matches!(&view.items[0].kind, ItemKind::Tool { tool, .. } if tool.name == "Bash"));
}

#[test]
fn an_empty_reason_adds_no_note() {
    let view = live(
        SessionView::default(),
        &[row(
            1,
            "r",
            None,
            "turn",
            json!({ "state": "started", "reason": "" }),
        )],
    );
    assert!(view.items.is_empty());
}

// Pins: g0-session#1
#[test]
fn the_newest_seq_is_the_highest_applied_live_or_paged() {
    let all = all();
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
    assert_eq!(view.newest, 15);
    let view = live(
        view,
        &all.iter()
            .filter(|r| r.seq > 15)
            .cloned()
            .collect::<Vec<_>>(),
    );
    assert_eq!(view.newest, all.iter().map(|r| r.seq).max().unwrap());
}
