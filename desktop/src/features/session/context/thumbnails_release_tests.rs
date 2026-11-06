//! The thumbnails released on every way out of the tab, decoded within limits, kept across a re-read, dropped when no
//! longer wanted, and stopped by any read's changed source. Uses thumbnails_tests.rs's setup.

use std::sync::Arc;

use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{Entity, Image, ImageFormat, VisualTestContext};
use serde_json::{Value, json};

use super::scale;
use super::tests::{
    NEW, OLD, draw, drawn, image_reads, in_atlas, open, pick_s1, piece, png, queue_lists, show,
    thumbnail, two_images,
};
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::client::session_client::CallError;
use crate::features::session::context::context_pane::ContextPane;
use crate::features::session::context::live_facts::LiveFacts;

/// Picks s1 and shows the tab with both images' thumbnails made and drawn.
fn shown_with_both(pane: &Entity<ContextPane>, client: &FakeClient, cx: &mut VisualTestContext) {
    pick_s1(two_images(), client, cx);
    client.queue_answer(Ok(piece(&png(8, 8))));
    client.queue_answer(Ok(piece(&png(8, 8))));
    show(pane, cx);
    draw(cx);
}

fn facts(pane: &Entity<ContextPane>, prompt: i64) -> LiveFacts {
    LiveFacts {
        publisher: pane.entity_id(),
        session: "s1".into(),
        key: None,
        provider: Some("claude".into()),
        context: None,
        input_tokens: None,
        output_tokens: None,
        model: None,
        effort: None,
        access: None,
        newest_prompt_seq: Some(prompt),
        newest_ended_run: None,
        drafts: Vec::new(),
    }
}

fn publish(facts: LiveFacts, cx: &mut VisualTestContext) {
    cx.update(|_, cx| LiveFacts::publish(facts, cx));
    cx.run_until_parked();
    draw(cx);
}

/// Asks for these paths' thumbnails, as a re-read listing them would.
fn want(pane: &Entity<ContextPane>, paths: &[&str], cx: &mut VisualTestContext) {
    pane.update(cx, |pane, cx| {
        let paths = paths.iter().map(|path| path.to_string()).collect();
        if let Some(thumbnails) = pane.thumbnails.as_mut() {
            thumbnails.want("s1".into(), None, paths, cx);
        }
    });
    cx.run_until_parked();
}

// Pins: g0-shape#18
#[gpui::test]
fn a_second_show_with_no_hide_releases_the_first_shows_thumbnails(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown_with_both(&pane, &client, cx);
    let made = thumbnail(&pane, NEW, cx).expect("the thumbnail is made");
    assert!(in_atlas(&made, cx));

    // Moved to another tab group, the tab hears `true` again with no `false` first.
    queue_lists(two_images(), &client);
    client.queue_answer(Ok(piece(&png(8, 8))));
    client.queue_answer(Ok(piece(&png(8, 8))));
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    assert!(!in_atlas(&made, cx), "GPUI's copy is released");
    cx.run_until_parked();
    draw(cx);
    assert!(thumbnail(&pane, NEW, cx).is_some_and(|again| !Arc::ptr_eq(&again, &made)));
}

// Pins: g0-shape#18
#[gpui::test]
fn removing_the_tab_from_the_dock_releases_and_stops(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick_s1(two_images(), &client, cx);
    client.queue_answer(Ok(piece(&png(8, 8))));
    let second = client.queue_pending();
    show(&pane, cx);
    draw(cx);
    let made = thumbnail(&pane, NEW, cx).expect("the thumbnail is made");
    assert!(in_atlas(&made, cx));

    pane.update_in(cx, |pane, window, cx| pane.on_removed(window, cx));
    cx.run_until_parked();
    assert!(!in_atlas(&made, cx), "GPUI's copy is released");
    assert!(thumbnail(&pane, NEW, cx).is_none());
    second.send(Ok(piece(&png(8, 8)))).ok();
    cx.run_until_parked();
    assert_eq!(image_reads(&client).len(), 2, "nothing more is read");
    assert!(
        thumbnail(&pane, OLD, cx).is_none(),
        "the read going was dropped"
    );
}

// Pins: g0-shape#18
#[gpui::test]
fn an_image_over_the_limits_is_skipped_and_not_tried_again(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick_s1(two_images(), &client, cx);
    client.queue_answer(Ok(piece(&png(16_385, 1))));
    client.queue_answer(Ok(piece(&png(8, 8))));
    show(&pane, cx);
    draw(cx);

    assert!(thumbnail(&pane, NEW, cx).is_none());
    assert!(drawn(cx, format!("attachment-{NEW}-placeholder")));
    assert!(thumbnail(&pane, OLD, cx).is_some(), "the queue moves on");
    pane.update(cx, |pane, cx| pane.want_thumbnails(cx));
    cx.run_until_parked();
    assert_eq!(image_reads(&client).len(), 2, "it isn't read again");
}

// Pins: g0-shape#18
#[test]
fn decoding_refuses_an_image_wider_than_the_limit() {
    let wide = Image::from_bytes(ImageFormat::Png, png(16_385, 1));
    assert!(scale(&wide).is_err());
    let widest = Image::from_bytes(ImageFormat::Png, png(16_384, 1));
    assert!(scale(&widest).is_ok());
}

// Pins: g0-shape#18
#[test]
fn a_small_picture_keeps_its_size() {
    let small = scale(&Image::from_bytes(ImageFormat::Png, png(8, 6))).expect("it decodes");
    assert_eq!((small.size(0).width.0, small.size(0).height.0), (8, 6));
}

// Pins: g0-shape#18
#[gpui::test]
fn a_re_read_keeps_the_thumbnails_made_and_reads_only_the_new_image(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    publish(facts(&pane, 2), cx);
    shown_with_both(&pane, &client, cx);
    let made = thumbnail(&pane, NEW, cx).expect("the thumbnail is made");

    let newest = "/h/attachments/s1/c1.png";
    let images: Value = json!({ "prompts": [
        format!("more\nAttached image: {newest} (third.png)"),
        format!("again\nAttached image: {NEW} (new.png)"),
        format!("look\nAttached image: {OLD} (shot.png)"),
    ] });
    client.queue_answer(Ok(images));
    client.queue_answer(Ok(piece(&png(8, 8))));
    publish(facts(&pane, 4), cx);

    assert_eq!(image_reads(&client), ["b0.png", "9f.png", "c1.png"]);
    assert!(thumbnail(&pane, NEW, cx).is_some_and(|kept| Arc::ptr_eq(&kept, &made)));
    assert!(in_atlas(&made, cx), "the kept one stays drawn");
}

// Pins: g0-shape#18
#[gpui::test]
fn a_path_dropped_from_the_list_is_released(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown_with_both(&pane, &client, cx);
    let (new, old) = (
        thumbnail(&pane, NEW, cx).unwrap(),
        thumbnail(&pane, OLD, cx).unwrap(),
    );
    assert!(in_atlas(&new, cx) && in_atlas(&old, cx));

    want(&pane, &[OLD], cx);
    assert!(thumbnail(&pane, NEW, cx).is_none());
    assert!(!in_atlas(&new, cx), "GPUI's copy is released");
    assert!(in_atlas(&old, cx), "the one still listed stays");
}

// Pins: g0-shape#18
#[gpui::test]
fn a_read_finishing_after_its_path_was_dropped_is_not_kept(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick_s1(two_images(), &client, cx);
    let first = client.queue_pending();
    show(&pane, cx);

    want(&pane, &[OLD], cx);
    client.queue_answer(Ok(piece(&png(8, 8))));
    first
        .send(Ok(piece(&png(8, 8))))
        .expect("the read is waiting");
    cx.run_until_parked();
    assert!(thumbnail(&pane, NEW, cx).is_none(), "dropped at once");
    assert!(thumbnail(&pane, OLD, cx).is_some());
    assert_eq!(image_reads(&client), ["b0.png", "9f.png"]);
}

// Pins: g0-shape#18
#[gpui::test]
fn another_reads_changed_source_stops_the_queue(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    publish(facts(&pane, 2), cx);
    pick_s1(two_images(), &client, cx);
    let first = client.queue_pending();
    show(&pane, cx);

    client.queue_answer(Err(CallError::Refused {
        code: "source_changed".into(),
        message: "Owner replaced".into(),
    }));
    publish(facts(&pane, 4), cx);
    first.send(Ok(piece(&png(8, 8)))).ok();
    cx.run_until_parked();
    assert_eq!(image_reads(&client), ["b0.png"], "nothing more is read");
    assert!(
        thumbnail(&pane, NEW, cx).is_none(),
        "the read going was dropped"
    );
    pane.update(cx, |pane, cx| pane.want_thumbnails(cx));
    cx.run_until_parked();
    assert_eq!(
        image_reads(&client).len(),
        1,
        "nothing is read from a changed source"
    );
}
