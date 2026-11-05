//! The Attachments card's thumbnails on the fake client, with real PNGs in its answers: one read at a time, newest
//! first, only while the tab shows, released on hide and on another pick, stopped by a changed source.

use std::io::Cursor;
use std::rc::Rc;
use std::sync::Arc;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{
    AppContext as _, Entity, Image, ImageFormat, Modifiers, RenderImage, VisualTestContext,
};
use serde_json::{Value, json};

use super::scale;
use crate::core::engine::SessionKey;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::client::session_client::CallError;
use crate::features::session::context::context_pane::ContextPane;

const NEW: &str = "/h/attachments/s1/b0.png";
const OLD: &str = "/h/attachments/s1/9f.png";

/// The pane with thumbnails, in a kit root.
fn open(cx: &mut TestAppContext) -> (Entity<ContextPane>, FakeClient, &mut VisualTestContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
    });
    let client = FakeClient::default();
    let fake = client.clone();
    let pane = cx.update(|cx| cx.new(|cx| ContextPane::with_thumbnails(Rc::new(fake), cx)));
    let inner = pane.clone();
    let (_, cx) =
        cx.add_window_view(move |window, cx| gpui_kit::base::Root::new(inner, window, cx));
    (pane, client, cx)
}

fn draw(cx: &mut VisualTestContext) {
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

fn png(width: u32, height: u32) -> Vec<u8> {
    let mut bytes = Vec::new();
    image::RgbaImage::new(width, height)
        .write_to(&mut Cursor::new(&mut bytes), image::ImageFormat::Png)
        .expect("a PNG is written");
    bytes
}

/// `attachments/read`'s answer holding the whole image in one piece.
fn piece(bytes: &[u8]) -> Value {
    json!({ "data": STANDARD.encode(bytes), "size": bytes.len(), "mime": "image/png" })
}

/// The file names `attachments/read` was asked for, in order.
fn image_reads(client: &FakeClient) -> Vec<String> {
    (client.calls().into_iter())
        .filter(|(call, _)| call == "attachments/read")
        .map(|(_, args)| args["name"].as_str().unwrap_or_default().to_string())
        .collect()
}

/// The two prompts' images, newest prompt first.
fn two_images() -> Value {
    json!({ "prompts": [
        format!("again\nAttached image: {NEW} (new.png)"),
        format!("look\nAttached image: {OLD} (shot.png)"),
    ] })
}

/// Queues the tab's three reads, which come before any image's, and picks s1.
fn pick_s1(images: Value, client: &FakeClient, cx: &mut VisualTestContext) {
    queue_lists(images, client);
    cx.update(|_, cx| {
        SelectedSession::global(cx)
            .update(cx, |selected, cx| selected.select(Some("s1".into()), cx))
    });
}

/// The answers to the tab's reads of the files, the images and the total.
fn queue_lists(images: Value, client: &FakeClient) {
    client.queue_answer(Ok(json!([])));
    client.queue_answer(Ok(images));
    client.queue_answer(Ok(json!({})));
}

/// Shows the tab; the caller has queued its reads.
fn show(pane: &Entity<ContextPane>, cx: &mut VisualTestContext) {
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    cx.run_until_parked();
    draw(cx);
}

fn hide(pane: &Entity<ContextPane>, cx: &mut VisualTestContext) {
    pane.update_in(cx, |pane, window, cx| pane.set_active(false, window, cx));
    cx.run_until_parked();
}

fn thumbnail(
    pane: &Entity<ContextPane>,
    path: &str,
    cx: &mut VisualTestContext,
) -> Option<Arc<RenderImage>> {
    pane.read_with(cx, |pane, _| {
        pane.thumbnails
            .as_ref()
            .and_then(|thumbnails| thumbnails.get(path))
    })
}

/// Whether the element with this selector was drawn.
fn drawn(cx: &mut VisualTestContext, selector: String) -> bool {
    cx.debug_bounds(selector.leak()).is_some()
}

fn in_atlas(image: &RenderImage, cx: &mut VisualTestContext) -> bool {
    cx.update(|window, _| window.has_image_atlas_entry(image))
}

#[gpui::test]
fn images_are_read_one_at_a_time_newest_first(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick_s1(two_images(), &client, cx);
    let first = client.queue_pending();
    show(&pane, cx);
    assert_eq!(
        image_reads(&client),
        ["b0.png"],
        "the newest image is read first, alone"
    );

    let second = client.queue_pending();
    first
        .send(Ok(piece(&png(8, 8))))
        .expect("the read is waiting");
    cx.run_until_parked();
    assert_eq!(
        image_reads(&client),
        ["b0.png", "9f.png"],
        "the next read starts only after the first answers"
    );
    second
        .send(Ok(piece(&png(8, 8))))
        .expect("the read is waiting");
    cx.run_until_parked();
    assert_eq!(image_reads(&client).len(), 2, "nothing is left to read");
    assert!(thumbnail(&pane, NEW, cx).is_some() && thumbnail(&pane, OLD, cx).is_some());
}

#[gpui::test]
fn a_row_shows_the_placeholder_until_its_thumbnail_arrives(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick_s1(two_images(), &client, cx);
    let first = client.queue_pending();
    show(&pane, cx);
    assert!(drawn(cx, format!("attachment-{NEW}-placeholder")));
    assert!(!drawn(cx, format!("attachment-{NEW}-picture")));

    client.queue_answer(Ok(piece(&png(8, 8))));
    first
        .send(Ok(piece(&png(8, 8))))
        .expect("the read is waiting");
    cx.run_until_parked();
    draw(cx);
    assert!(drawn(cx, format!("attachment-{NEW}-picture")));
    assert!(!drawn(cx, format!("attachment-{NEW}-placeholder")));
    let made = thumbnail(&pane, NEW, cx).expect("the thumbnail is made");
    assert!(in_atlas(&made, cx), "the row draws the thumbnail");
}

#[gpui::test]
fn a_failed_read_keeps_the_placeholder_and_is_not_tried_again(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick_s1(two_images(), &client, cx);
    client.queue_answer(Err(CallError::Failed("gone".into())));
    client.queue_answer(Ok(piece(&png(8, 8))));
    show(&pane, cx);
    draw(cx);
    assert_eq!(
        image_reads(&client),
        ["b0.png", "9f.png"],
        "a failure moves on to the next"
    );
    assert!(drawn(cx, format!("attachment-{NEW}-placeholder")));
    assert!(drawn(cx, format!("attachment-{OLD}-picture")));
    assert!(
        cx.debug_bounds("attachments-open-failed").is_none(),
        "no error is shown"
    );

    pane.update(cx, |pane, cx| pane.want_thumbnails(cx));
    cx.run_until_parked();
    assert_eq!(
        image_reads(&client).len(),
        2,
        "the failed image isn't read again"
    );
}

#[gpui::test]
fn hiding_the_tab_cancels_the_queue(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick_s1(two_images(), &client, cx);
    let first = client.queue_pending();
    show(&pane, cx);
    hide(&pane, cx);

    first.send(Ok(piece(&png(8, 8)))).ok();
    cx.run_until_parked();
    assert_eq!(image_reads(&client), ["b0.png"], "nothing more is read");
    assert!(thumbnail(&pane, NEW, cx).is_none(), "the answer is dropped");
}

#[gpui::test]
fn hiding_the_tab_releases_every_thumbnail_and_gpuis_copy(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick_s1(two_images(), &client, cx);
    client.queue_answer(Ok(piece(&png(8, 8))));
    client.queue_answer(Ok(piece(&png(8, 8))));
    show(&pane, cx);
    draw(cx);
    let made = thumbnail(&pane, NEW, cx).expect("the thumbnail is made");
    assert!(in_atlas(&made, cx), "drawn, so GPUI holds a copy");

    hide(&pane, cx);
    assert!(thumbnail(&pane, NEW, cx).is_none() && thumbnail(&pane, OLD, cx).is_none());
    assert!(!in_atlas(&made, cx), "GPUI's copy is released");
    draw(cx);
    assert_eq!(
        Arc::strong_count(&made),
        1,
        "nothing but this test holds it"
    );
}

#[gpui::test]
fn picking_another_session_releases_the_thumbnails(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick_s1(two_images(), &client, cx);
    client.queue_answer(Ok(piece(&png(8, 8))));
    client.queue_answer(Ok(piece(&png(8, 8))));
    show(&pane, cx);
    draw(cx);
    let made = thumbnail(&pane, NEW, cx).expect("the thumbnail is made");
    assert!(in_atlas(&made, cx));

    queue_lists(json!({ "prompts": [] }), &client);
    cx.update(|_, cx| {
        SelectedSession::global(cx)
            .update(cx, |selected, cx| selected.select(Some("s2".into()), cx))
    });
    cx.run_until_parked();
    assert!(thumbnail(&pane, NEW, cx).is_none());
    assert!(!in_atlas(&made, cx), "GPUI's copy is released");
}

#[gpui::test]
fn showing_the_tab_again_starts_over(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick_s1(two_images(), &client, cx);
    client.queue_answer(Ok(piece(&png(8, 8))));
    client.queue_answer(Ok(piece(&png(8, 8))));
    show(&pane, cx);
    hide(&pane, cx);

    queue_lists(two_images(), &client);
    client.queue_answer(Ok(piece(&png(8, 8))));
    client.queue_answer(Ok(piece(&png(8, 8))));
    show(&pane, cx);
    assert_eq!(
        image_reads(&client),
        ["b0.png", "9f.png", "b0.png", "9f.png"]
    );
    assert!(thumbnail(&pane, NEW, cx).is_some() && thumbnail(&pane, OLD, cx).is_some());
}

#[gpui::test]
fn a_changed_source_stops_the_queue(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    let key = SessionKey {
        machine: "a".into(),
        id: "s1".into(),
    };
    let source = ExpectedRowSource {
        machine: "a".into(),
        generation: 3,
        route_machine: None,
    };
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.commit_open(key, source, false, cx)
        })
    });
    queue_lists(two_images(), &client);
    client.queue_answer(Err(CallError::Refused {
        code: "source_changed".into(),
        message: "Owner replaced".into(),
    }));
    show(&pane, cx);

    assert_eq!(
        image_reads(&client),
        ["b0.png"],
        "the second image isn't read"
    );
    draw(cx);
    let row = (cx.debug_bounds(format!("attachment-{NEW}").leak())).expect("the row is drawn");
    cx.simulate_click(row.center(), Modifiers::none());
    cx.run_until_parked();
    assert!(
        cx.debug_bounds("attachments-open-failed").is_some(),
        "the source is marked invalid, so the image doesn't open"
    );
    pane.update(cx, |pane, cx| pane.want_thumbnails(cx));
    cx.run_until_parked();
    assert_eq!(
        image_reads(&client).len(),
        1,
        "nothing is read from a changed source"
    );
}

#[gpui::test]
fn a_remote_image_with_no_source_is_not_read(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    let mut images = two_images();
    images["machine"] = json!("far");
    pick_s1(images, &client, cx);
    show(&pane, cx);

    assert!(image_reads(&client).is_empty());
    assert!(drawn(cx, format!("attachment-{NEW}-placeholder")));
}

#[test]
fn a_large_picture_scales_to_120_px_on_its_longest_edge() {
    let image = Image::from_bytes(ImageFormat::Png, png(4000, 3000));
    let small = scale(&image).expect("it scales");
    assert_eq!((small.size(0).width.0, small.size(0).height.0), (120, 90));
}
