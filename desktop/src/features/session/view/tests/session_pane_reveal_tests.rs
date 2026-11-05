//! The streaming reply's reveal in the pane: where it starts, how a frame steps it, and that it stops.

use std::time::{Duration, Instant};

use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};

use super::SessionPane;
use super::reveal::REVEAL_TICK;
use super::tests::{jump, live, message, open, page, show, user};
use crate::features::session::model::reveal::Reveal;

fn reveal(pane: &Entity<SessionPane>, cx: &mut VisualTestContext) -> Option<Reveal> {
    cx.update(|_, cx| pane.read(cx).reveal.clone())
}

// Pins: g0-session#4
#[gpui::test]
fn a_streaming_reply_reveals_from_what_already_showed(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(
        vec![user(1, "go"), message(2, 0, &["Hel"])],
        false,
        Some("r1"),
    )));
    jump(&client, 2, cx);
    assert_eq!(reveal(&pane, cx), None, "a page shows whole");
    live(&client, message(3, 0, &["Hello there, a longer reply"]), cx);
    let started = reveal(&pane, cx).expect("the new text reveals");
    assert_eq!(started.key, "message:m1");
    assert!(started.shown >= 3, "what showed stays shown");
    assert!(started.shown < 27, "the new text comes in over frames");
}

// Pins: g0-session#4
#[gpui::test]
fn a_frame_steps_the_reveal_and_it_stops_once_caught_up(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![user(1, "go")], false, Some("r1"))));
    jump(&client, 1, cx);
    live(&client, message(2, 0, &["Hello there, a longer reply"]), cx);
    assert!(reveal(&pane, cx).is_some());
    // A frame long after the last one catches up at once, and the reveal rests.
    pane.update_in(cx, |pane, window, cx| {
        pane.revealed_at = Instant::now() - Duration::from_secs(1);
        pane.reveal_frame(window, cx);
    });
    assert_eq!(reveal(&pane, cx), None);
}

// Pins: g0-session#4
#[gpui::test]
fn a_final_row_with_more_text_still_comes_in_smoothly(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(
        vec![user(1, "go"), message(2, 0, &["Hel"])],
        false,
        Some("r1"),
    )));
    jump(&client, 2, cx);
    let done = serde_json::json!({ "sessionId": "s1", "seq": 3, "runId": "r1", "itemId": "m1", "kind": "message",
        "payload": { "blocks": [{ "kind": "paragraph", "parts": [{ "kind": "text", "value": "Hello there, the end" }] }] },
        "createdAt": 3 });
    live(&client, done, cx);
    let started = reveal(&pane, cx).expect("the rest reveals");
    assert_eq!(started.shown, 3);
}

/// Display frames alone must not move the reveal: a redraw on each one held up a core for as long as a reply streamed,
/// up to 120 full windows a second on a Mac.
#[gpui::test]
fn a_reveal_steps_on_its_own_tick_and_not_on_every_display_frame(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![user(1, "go")], false, Some("r1"))));
    jump(&client, 1, cx);
    let long = "A long reply that takes a good while to come in, word by word.".repeat(20);
    live(&client, message(2, 0, &[&long]), cx);
    cx.run_until_parked();
    let started = reveal(&pane, cx).expect("the new text reveals").shown;

    for _ in 0..30 {
        cx.update(|window, cx| window.simulate_next_frame(cx));
        cx.run_until_parked();
    }
    assert_eq!(
        reveal(&pane, cx).expect("still revealing").shown,
        started,
        "display frames don't step it"
    );

    cx.executor().advance_clock(REVEAL_TICK);
    cx.run_until_parked();
    let stepped = reveal(&pane, cx).expect("still revealing").shown;
    assert!(stepped > started, "its tick steps it");

    cx.executor().advance_clock(REVEAL_TICK);
    cx.run_until_parked();
    assert!(
        reveal(&pane, cx).expect("still revealing").shown > stepped,
        "and the next tick steps it again, without a new row"
    );

    // Once it has caught up, nothing is left asking for ticks.
    pane.update_in(cx, |pane, _, _| {
        pane.revealed_at = Instant::now() - Duration::from_secs(1);
    });
    cx.executor().advance_clock(REVEAL_TICK);
    cx.run_until_parked();
    assert_eq!(reveal(&pane, cx), None, "it rests");
    cx.update(|_, cx| assert!(pane.read(cx)._reveal_tick.is_none(), "and its tick is gone"));
}

fn builds(pane: &Entity<SessionPane>, cx: &mut VisualTestContext) -> usize {
    cx.update(|_, cx| pane.read(cx).timeline.read(cx).builds)
}

// Pins: g0-session#4
#[gpui::test]
fn a_streaming_reply_redraws_only_its_row(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![user(1, "go")], false, Some("r1"))));
    jump(&client, 1, cx);
    live(&client, message(2, 0, &["Hello"]), cx);
    let start = builds(&pane, cx);
    live(&client, message(3, 0, &["Hello there"]), cx);
    live(&client, message(4, 0, &["Hello there, and more"]), cx);
    pane.update_in(cx, |pane, window, cx| {
        pane.revealed_at = Instant::now() - Duration::from_millis(16);
        pane.reveal_frame(window, cx);
    });
    assert_eq!(
        builds(&pane, cx),
        start,
        "neither the deltas nor a frame work the entries out again"
    );
}
