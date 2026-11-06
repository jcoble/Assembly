//! The pane on the fake client: each session's reading place, and the newest row it tells the backend was shown.
use super::*;

// Pins: g0-session#8
#[gpui::test]
fn each_session_keeps_its_reading_place_when_you_switch_away_and_back(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(users(1..=30), false, None)));
    jump(&client, 30, cx);
    let timeline = cx.update(|_, cx| pane.read(cx).timeline.clone());
    cx.update(|_, cx| timeline.update(cx, |timeline, cx| timeline.scroll_to_row(5, cx)));
    let place = cx.update(|_, cx| timeline.read(cx).capture_place(cx));
    show(&pane, "s2", cx);
    client.queue_answer(Ok(page(users(1..=3), false, None)));
    jump(&client, 3, cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(users(1..=30), false, None)));
    jump(&client, 30, cx);
    cx.update(|_, cx| {
        timeline.update(cx, |timeline, cx| {
            timeline.restore_place(&place, cx).unwrap()
        })
    });
    assert_eq!(top(&pane, cx), 5);
}

// Pins: g0-session#8
#[gpui::test]
fn a_working_line_before_the_first_page_keeps_the_reading_place(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(users(1..=30), false, None)));
    jump(&client, 30, cx);
    let timeline = cx.update(|_, cx| pane.read(cx).timeline.clone());
    cx.update(|_, cx| timeline.update(cx, |timeline, cx| timeline.scroll_to_row(5, cx)));
    let place = cx.update(|_, cx| timeline.read(cx).capture_place(cx));
    show(&pane, "s2", cx);
    show(&pane, "s1", cx);
    // A send from s1 is accepted before s1's rows come back.
    let composer = cx.update(|_, cx| pane.read(cx).composer.clone());
    cx.update(|_, cx| composer.update(cx, |_, cx| cx.emit(accepted("s1"))));
    cx.run_until_parked();
    client.queue_answer(Ok(page(users(1..=30), false, None)));
    jump(&client, 30, cx);
    cx.update(|_, cx| {
        timeline.update(cx, |timeline, cx| {
            timeline.restore_place(&place, cx).unwrap()
        })
    });
    assert_eq!(top(&pane, cx), 5);
}

// Pins: g0-session#8
#[gpui::test]
fn a_reading_place_older_than_the_newest_page_opens_at_the_end_and_never_jumps_later(
    cx: &mut TestAppContext,
) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(users(1..=30), false, None)));
    jump(&client, 30, cx);
    let timeline = cx.update(|_, cx| pane.read(cx).timeline.clone());
    cx.update(|_, cx| timeline.update(cx, |timeline, cx| timeline.scroll_to_row(5, cx)));
    let place = cx.update(|_, cx| timeline.read(cx).capture_place(cx));
    show(&pane, "s2", cx);
    client.queue_answer(Ok(page(users(1..=3), false, None)));
    jump(&client, 3, cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(users(11..=30), true, None)));
    jump(&client, 30, cx);
    cx.update(|_, cx| {
        timeline.update(cx, |timeline, cx| {
            timeline.restore_place(&place, cx).unwrap()
        })
    });
    let at_open = top(&pane, cx);
    client.queue_answer(Ok(page(users(1..=10), false, None)));
    pane.update(cx, |pane, cx| pane.load_older(cx));
    cx.run_until_parked();
    assert!(
        top(&pane, cx) >= at_open,
        "the older page that holds the old place doesn't scroll to it"
    );
}

// Pins: sessions-list#10
#[gpui::test]
fn an_open_session_tells_the_backend_the_newest_row_shown_so_its_unread_dot_clears(
    cx: &mut TestAppContext,
) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![user(3, "one"), user(5, "two")], false, None)));
    jump(&client, 5, cx);
    live(&client, user(6, "three"), cx);
    assert_eq!(
        client.reads(),
        [
            json!({ "session": "s1", "seq": 5 }),
            json!({ "session": "s1", "seq": 6 })
        ]
    );
    // A reconnect's jump tells it again: a read may have been lost with the connection.
    client.queue_answer(Ok(page(vec![user(6, "three")], true, None)));
    jump(&client, 6, cx);
    assert_eq!(client.reads()[2], json!({ "session": "s1", "seq": 6 }));
    show(&pane, "s2", cx);
    client.queue_answer(Ok(page(vec![user(2, "other")], false, None)));
    jump(&client, 2, cx);
    assert_eq!(client.reads()[3], json!({ "session": "s2", "seq": 2 }));
}

// A streamed reply sends no read: unread only changes when a turn ends.
// Pins: sessions-list#10
#[gpui::test]
fn a_streaming_turn_sends_no_read_until_it_ends(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![user(3, "one")], false, Some("r1"))));
    jump(&client, 3, cx);
    live(
        &client,
        row(4, "turn", None, json!({ "state": "started" })),
        cx,
    );
    live(&client, message(5, 0, &["a"]), cx);
    live(&client, message(6, 1, &["b"]), cx);
    assert!(client.reads().is_empty(), "{:?}", client.reads());
    live(
        &client,
        row(7, "turn", None, json!({ "state": "finished" })),
        cx,
    );
    assert_eq!(client.reads(), [json!({ "session": "s1", "seq": 7 })]);
}
