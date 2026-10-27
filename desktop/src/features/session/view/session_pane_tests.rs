//! The pane on the fake client: opening a session, its pages and live rows, a second session, and a send.

use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};
use serde_json::{Value, json};

use super::SessionPane;
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::client::session_client::{CallError, RowsEvent};
use crate::features::session::composer::composer_view::ComposerEvent;
use crate::features::session::model::item::ItemKind;
use crate::features::session::rows::row::Row;

fn open(cx: &mut TestAppContext) -> (Entity<SessionPane>, FakeClient, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let client = FakeClient::default();
    let fake = client.clone();
    let (pane, cx) =
        cx.add_window_view(move |window, cx| SessionPane::new(Rc::new(fake), window, cx));
    (pane, client, cx)
}

fn row(seq: i64, kind: &str, item: Option<&str>, payload: Value) -> Value {
    json!({ "sessionId": "s1", "seq": seq, "runId": "r1", "itemId": item, "kind": kind, "payload": payload, "createdAt": seq })
}

fn user(seq: i64, text: &str) -> Value {
    row(seq, "user", None, json!({ "text": text }))
}

fn page(rows: Vec<Value>, has_before: bool, active: Option<&str>) -> Value {
    json!({ "rows": rows, "hasBefore": has_before, "activeRunId": active })
}

fn live(client: &FakeClient, row: Value, cx: &mut VisualTestContext) {
    let row: Row = serde_json::from_value(row).unwrap();
    cx.update(|_, cx| client.push(RowsEvent::Row(Box::new(row)), cx));
    cx.run_until_parked();
}

fn jump(client: &FakeClient, seq: i64, cx: &mut VisualTestContext) {
    cx.update(|_, cx| client.push(RowsEvent::Jump { seq }, cx));
    cx.run_until_parked();
}

fn show(pane: &Entity<SessionPane>, session: &str, cx: &mut VisualTestContext) {
    pane.update_in(cx, |pane, window, cx| pane.open(session.into(), window, cx));
    cx.run_until_parked();
}

fn keys(pane: &Entity<SessionPane>, cx: &mut VisualTestContext) -> Vec<String> {
    cx.update(|_, cx| {
        pane.read(cx)
            .view
            .items
            .iter()
            .map(|item| item.key.clone())
            .collect()
    })
}

// Pins: g0-session#1
#[gpui::test]
fn opening_subscribes_then_the_jump_loads_the_page_below_it_and_live_rows_go_after(
    cx: &mut TestAppContext,
) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    assert_eq!(client.subscribed(), [("s1".to_string(), None)]);
    assert!(
        client.calls().is_empty(),
        "nothing loads before the socket says where the session is"
    );
    client.queue_answer(Ok(page(vec![user(3, "one"), user(5, "two")], false, None)));
    jump(&client, 5, cx);
    assert_eq!(
        client.calls(),
        [(
            "rows/page".to_string(),
            json!({ "session": "s1", "before": 6, "limit": 100 })
        )]
    );
    live(&client, user(6, "three"), cx);
    assert_eq!(keys(&pane, cx), ["seq:3", "seq:5", "seq:6"]);
}

// Pins: g0-session#3
#[gpui::test]
fn a_later_jump_drops_what_is_held_and_loads_again(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![user(3, "one")], false, None)));
    jump(&client, 3, cx);
    client.queue_answer(Ok(page(vec![user(9, "nine")], true, None)));
    jump(&client, 9, cx);
    assert_eq!(keys(&pane, cx), ["seq:9"]);
    assert_eq!(client.calls()[1].1["before"], 10);
}

// Pins: g0-session#1
#[gpui::test]
fn opening_another_session_drops_the_old_subscription(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![user(3, "one")], false, None)));
    jump(&client, 3, cx);
    show(&pane, "s2", cx);
    assert_eq!(client.cancels(), 1);
    assert_eq!(client.subscribed()[1], ("s2".to_string(), None));
    assert!(keys(&pane, cx).is_empty());
    show(&pane, "s2", cx);
    assert_eq!(
        client.subscribed().len(),
        2,
        "showing the open session again changes nothing"
    );
}

// Pins: g0-session#23
#[gpui::test]
fn a_new_session_with_no_rows_is_ready_empty_and_a_sent_run_shows_working(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![], false, None)));
    jump(&client, 0, cx);
    assert!(keys(&pane, cx).is_empty());
    assert_eq!(cx.update(|_, cx| pane.read(cx).status.clone()), "");
    let composer = cx.update(|_, cx| pane.read(cx).composer.clone());
    cx.update(|_, cx| {
        composer.update(cx, |_, cx| {
            cx.emit(ComposerEvent::Sent {
                session: "s1".into(),
                run_id: "r1".into(),
            })
        })
    });
    cx.run_until_parked();
    assert_eq!(
        cx.update(|_, cx| pane
            .read(cx)
            .view
            .active_run(pane.read(cx).known_run.as_deref()))
            .as_deref(),
        Some("r1")
    );
}

// Pins: g0-session#12
#[gpui::test]
fn the_pages_active_run_counts_until_its_turn_row_ends_it(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![user(3, "one")], false, Some("r1"))));
    jump(&client, 3, cx);
    let active = |pane: &Entity<SessionPane>, cx: &mut VisualTestContext| {
        cx.update(|_, cx| {
            pane.read(cx)
                .view
                .active_run(pane.read(cx).known_run.as_deref())
        })
    };
    assert_eq!(active(&pane, cx).as_deref(), Some("r1"));
    live(
        &client,
        row(4, "turn", None, json!({ "state": "finished" })),
        cx,
    );
    assert_eq!(active(&pane, cx), None);
}

// Pins: g0-session#2
#[gpui::test]
fn an_older_page_loads_above_once_at_a_time_and_not_past_the_first(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![user(10, "ten")], true, None)));
    jump(&client, 10, cx);
    client.queue_answer(Ok(page(vec![user(4, "four")], false, None)));
    pane.update(cx, |pane, cx| {
        pane.load_older(cx);
        pane.load_older(cx);
    });
    cx.run_until_parked();
    let pages: Vec<Value> = client
        .calls()
        .into_iter()
        .filter(|(call, _)| call == "rows/page")
        .map(|(_, args)| args)
        .collect();
    assert_eq!(pages.len(), 2, "one older page at a time");
    assert_eq!(pages[1]["before"], 10);
    assert_eq!(keys(&pane, cx), ["seq:4", "seq:10"]);
    pane.update(cx, |pane, cx| pane.load_older(cx));
    cx.run_until_parked();
    assert_eq!(client.calls().len(), 2, "nothing older to load");
}

// Pins: g0-session#29
#[gpui::test]
fn a_page_that_fails_says_why_and_an_aborted_one_says_nothing(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Err(CallError::Unreachable {
        machine: "box".into(),
    }));
    jump(&client, 3, cx);
    assert_eq!(
        cx.update(|_, cx| pane.read(cx).status.clone()),
        "box is offline"
    );
    client.queue_answer(Err(CallError::Aborted));
    jump(&client, 3, cx);
    assert_eq!(cx.update(|_, cx| pane.read(cx).status.clone()), "");
}

fn message(seq: i64, from: usize, texts: &[&str]) -> Value {
    let blocks: Vec<Value> = texts
        .iter()
        .map(|text| json!({ "kind": "paragraph", "parts": [{ "kind": "text", "value": text }] }))
        .collect();
    row(
        seq,
        "delta",
        Some("m1"),
        json!({ "from": from, "text": texts.concat(), "blocks": blocks }),
    )
}

fn users(seqs: std::ops::RangeInclusive<i64>) -> Vec<Value> {
    seqs.map(|seq| user(seq, "hi")).collect()
}

fn top(pane: &Entity<SessionPane>, cx: &mut VisualTestContext) -> usize {
    cx.update(|_, cx| pane.read(cx).timeline.read(cx).top_row(cx))
}

// Pins: g0-session#4
#[gpui::test]
fn rows_that_arrive_before_the_first_page_wait_for_it(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![message(5, 0, &["a", "b"])], false, None)));
    let delta: Row = serde_json::from_value(message(6, 2, &["c"])).unwrap();
    cx.update(|_, cx| {
        client.push(RowsEvent::Jump { seq: 5 }, cx);
        client.push(RowsEvent::Row(Box::new(delta)), cx);
    });
    cx.run_until_parked();
    let blocks = cx.update(|_, cx| match &pane.read(cx).view.items[0].kind {
        ItemKind::Message { blocks, .. } => blocks.len(),
        _ => 0,
    });
    assert_eq!(blocks, 3, "the page's first two blocks and the live third");
}

// Pins: g0-session#2
#[gpui::test]
fn a_failed_older_page_is_not_asked_for_again_by_itself(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![user(10, "ten")], true, None)));
    jump(&client, 10, cx);
    client.queue_answer(Err(CallError::Timeout("slow".into())));
    pane.update(cx, |pane, cx| pane.load_older(cx));
    cx.run_until_parked();
    pane.update(cx, |pane, cx| pane.load_older(cx));
    cx.run_until_parked();
    assert_eq!(client.calls().len(), 2, "the first page and one older page");
}

// Pins: g0-session#5
#[gpui::test]
fn a_sent_prompt_goes_to_the_top_once_it_arrives(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(users(1..=30), false, None)));
    jump(&client, 30, cx);
    let composer = cx.update(|_, cx| pane.read(cx).composer.clone());
    cx.update(|_, cx| {
        composer.update(cx, |_, cx| {
            cx.emit(ComposerEvent::Sending { text: "hi".into() })
        })
    });
    live(&client, user(31, "hi"), cx);
    assert_eq!(top(&pane, cx), 30);
}

// Pins: g0-session#22
#[gpui::test]
fn a_send_answered_after_switching_sessions_leaves_the_new_one_alone(cx: &mut TestAppContext) {
    let (pane, _, cx) = open(cx);
    show(&pane, "s2", cx);
    let composer = cx.update(|_, cx| pane.read(cx).composer.clone());
    let sent = ComposerEvent::Sent {
        session: "s1".into(),
        run_id: "r1".into(),
    };
    cx.update(|_, cx| composer.update(cx, |_, cx| cx.emit(sent)));
    cx.run_until_parked();
    assert_eq!(cx.update(|_, cx| pane.read(cx).known_run.clone()), None);
}

// Pins: g0-session#8
#[gpui::test]
fn each_session_keeps_its_reading_place_when_you_switch_away_and_back(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(users(1..=30), false, None)));
    jump(&client, 30, cx);
    let timeline = cx.update(|_, cx| pane.read(cx).timeline.clone());
    cx.update(|_, cx| timeline.update(cx, |timeline, cx| timeline.scroll_to_row(5, cx)));
    show(&pane, "s2", cx);
    client.queue_answer(Ok(page(users(1..=3), false, None)));
    jump(&client, 3, cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(users(1..=30), false, None)));
    jump(&client, 30, cx);
    assert_eq!(top(&pane, cx), 5);
}
