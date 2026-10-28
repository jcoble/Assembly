//! The Board fixes from the UI check: the list stays at the top when you are at the top, a machine that comes back
//! rereads the expanded card, and each feed line shows its own question, answer or delivery status.

use serde_json::{Value, json};

use super::super::board_payload::BoardEvent;
use super::BoardState;
use super::tests::{page, question, task};

#[test]
fn board_refresh_stays_at_top_but_preserves_a_scrolled_anchor() {
    use gpui_kit::{ListOffset, px};
    for (index, offset, expected) in [(0, 0., None), (0, 12., Some("a")), (1, 0., Some("b"))] {
        let mut state = BoardState::default();
        let read = state.changed().unwrap();
        state.apply_page(read.epoch, page(&["a", "b"], None));
        let anchor = state.scroll_anchor(ListOffset {
            item_ix: index,
            offset_in_item: px(offset),
        });
        assert_eq!(anchor.as_ref().map(|(_, key)| key.as_str()), expected);
        let read = state.changed().unwrap();
        let mut fresh = page(&["needs-you", "a", "b"], None);
        fresh.items[0].attention_group = 0;
        state.apply_page(read.epoch, fresh);
        let top = anchor
            .and_then(|key| {
                state.items.iter().position(|item| {
                    (item.task.machine_id.clone(), item.task.task_key.clone()) == key
                })
            })
            .unwrap_or(0);
        assert_eq!(
            top,
            expected.map_or(0, |key| if key == "a" { 1 } else { 2 })
        );
    }
}

#[test]
fn board_remote_reconnect_rereads_only_the_expanded_source() {
    let mut state = BoardState::default();
    assert!(
        state
            .machines_changed(vec!["Workbox Test".into()])
            .is_none()
    );
    let read = state.select("Workbox Test", "t1").unwrap();
    assert!(
        state
            .machines_changed(vec!["Workbox Test".into(), "other".into()])
            .is_none()
    );
    assert_eq!(state.machines_changed(vec!["other".into()]), Some(read));
    assert!(state.machines_changed(vec![]).is_none());
    state.select("Workbox Test", "t1");
    state.machines_changed(vec!["Workbox Test".into()]);
    assert!(state.machines_changed(vec![]).is_none());
    state.select("local", "t1");
    state.machines_changed(vec!["Workbox Test".into()]);
    assert!(state.machines_changed(vec![]).is_none());
}

#[test]
fn board_feed_uses_each_events_question_answer_and_delivery() {
    use super::super::board_feed::event_text;
    for (kind, answer, delivery, expected) in [
        ("question_asked", Value::Null, "none", "Merge now?"),
        (
            "answer_recorded",
            json!({"kind":"text", "text":"Ship it"}),
            "pending",
            "Ship it",
        ),
        (
            "answer_recorded",
            json!({"kind":"approve", "text":null}),
            "pending",
            "Approved",
        ),
        (
            "answer_recorded",
            json!({"kind":"deny", "text":null}),
            "pending",
            "Denied",
        ),
        (
            "answer_delivered",
            json!({"kind":"text", "text":"Ship it"}),
            "delivered",
            "Delivered to the agent.",
        ),
        (
            "answer_delivery_failed",
            json!({"kind":"text", "text":"Ship it"}),
            "delivery_failed",
            "Not delivered; the answer is kept.",
        ),
        ("task_reported", Value::Null, "none", "Working"),
    ] {
        let mut q = question("local", 1, answer);
        q["deliveryState"] = json!(delivery);
        let event: BoardEvent = serde_json::from_value(json!({"kind":kind, "occurredAt":5000,
            "task":task("t1", "running"), "questions":[q]}))
        .unwrap();
        assert_eq!(event_text(&event), expected, "{kind}");
    }
}
