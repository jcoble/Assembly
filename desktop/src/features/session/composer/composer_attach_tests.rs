//! Attaching images on the fake client: a dropped or picked file, a pasted image, what's turned away, other dropped
//! files, removing a chip, and send waiting for uploads.

use std::path::PathBuf;

use gpui::{ClipboardItem, Image, ImageFormat, TestAppContext};
use gpui_kit::{Entity, VisualTestContext};
use serde_json::json;

use super::super::Composer;
use super::super::tests::{draft, open};
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::composer::attachment::AttachmentState;

fn on_s1(cx: &mut TestAppContext) -> (Entity<Composer>, FakeClient, &mut VisualTestContext) {
    let (composer, client, _, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    (composer, client, cx)
}

/// A file with these bytes in a folder of this test's own.
fn file(test: &str, name: &str, bytes: &[u8]) -> PathBuf {
    let folder =
        std::env::temp_dir().join(format!("composer-attach-{}-{test}", std::process::id()));
    std::fs::create_dir_all(&folder).unwrap();
    let path = folder.join(name);
    std::fs::write(&path, bytes).unwrap();
    path
}

/// The answers one small image's upload takes: begin, one chunk, commit.
fn queue_upload(client: &FakeClient, saved_at: &str) {
    client.queue_answer(Ok(json!({ "upload": "u1" })));
    client.queue_answer(Ok(json!({ "bytes": 0 })));
    client.queue_answer(Ok(json!({ "path": saved_at })));
}

/// Each chip as (name, where its upload is).
fn chips(composer: &Entity<Composer>, cx: &mut VisualTestContext) -> Vec<(String, String)> {
    cx.update(|_, cx| {
        let attachments = &composer.read(cx).attachments;
        attachments
            .iter()
            .map(|attachment| {
                let state = match &attachment.state {
                    AttachmentState::Uploading => "uploading".to_string(),
                    AttachmentState::Saved { path } => format!("saved {path}"),
                    AttachmentState::Failed(why) => format!("failed {why}"),
                };
                (attachment.name.to_string(), state)
            })
            .collect()
    })
}

fn type_and_send(composer: &Entity<Composer>, text: &str, cx: &mut VisualTestContext) {
    composer.update_in(cx, |composer, window, cx| {
        composer.draft.update(cx, |draft, cx| {
            draft.set_value(text.to_string(), window, cx)
        });
        composer.send(window, cx);
    });
    cx.run_until_parked();
}

fn sends(client: &FakeClient) -> Vec<String> {
    let calls = client.calls().into_iter();
    calls
        .filter(|(call, _)| call == "send/send")
        .map(|(_, args)| args["text"].as_str().unwrap_or_default().to_string())
        .collect()
}

// Pins: g0-session#24
#[gpui::test]
fn a_dropped_image_uploads_and_goes_out_with_the_text(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    let path = file("dropped", "shot.png", b"not really a png");
    queue_upload(&client, "/box/shot.png");
    composer.update_in(cx, |composer, window, cx| {
        composer.dropped(&[path], window, cx)
    });
    assert_eq!(
        chips(&composer, cx),
        [("shot.png".to_string(), "uploading".to_string())]
    );
    cx.run_until_parked();
    assert_eq!(chips(&composer, cx)[0].1, "saved /box/shot.png");
    client.queue_answer(Ok(json!({ "runId": "r1" })));
    type_and_send(&composer, "look", cx);
    assert_eq!(
        sends(&client),
        ["look\n\nAttached image: /box/shot.png (shot.png)"]
    );
    assert!(chips(&composer, cx).is_empty());
}

// Pins: g0-session#24
#[gpui::test]
fn send_waits_until_every_image_has_uploaded(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    let path = file("waits", "wait.png", b"bytes");
    queue_upload(&client, "/box/wait.png");
    composer.update_in(cx, |composer, window, cx| {
        composer.attach_paths(&[path], cx);
        composer
            .draft
            .update(cx, |draft, cx| draft.set_value("now", window, cx));
        composer.send(window, cx);
    });
    assert_eq!(draft(&composer, cx), "now", "nothing went out");
    cx.run_until_parked();
    assert!(sends(&client).is_empty());
    client.queue_answer(Ok(json!({ "runId": "r1" })));
    composer.update_in(cx, |composer, window, cx| composer.send(window, cx));
    cx.run_until_parked();
    assert_eq!(
        sends(&client),
        ["now\n\nAttached image: /box/wait.png (wait.png)"]
    );
}

// Pins: g0-session#24
#[gpui::test]
fn what_cant_attach_is_turned_away_and_says_why(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    let notes = file("away", "notes.txt", b"text");
    let big = file("away", "big.png", b"");
    std::fs::File::options()
        .write(true)
        .open(&big)
        .and_then(|file| file.set_len(21 * 1024 * 1024))
        .unwrap();
    let error = |composer: &Entity<Composer>, cx: &mut VisualTestContext| {
        cx.update(|_, cx| composer.read(cx).error.clone().map(String::from))
    };
    composer.update(cx, |composer, cx| composer.attach_paths(&[notes], cx));
    assert_eq!(
        error(&composer, cx).as_deref(),
        Some("notes.txt is not a supported image.")
    );
    composer.update(cx, |composer, cx| composer.attach_paths(&[big], cx));
    assert_eq!(
        error(&composer, cx).as_deref(),
        Some("big.png is larger than 20 MB.")
    );
    cx.run_until_parked();
    assert!(chips(&composer, cx).is_empty());
    assert!(client.calls().is_empty(), "nothing uploads");
}

// Pins: g0-session#24
#[gpui::test]
fn other_dropped_files_go_into_the_draft_as_their_paths(cx: &mut TestAppContext) {
    let (composer, _, cx) = on_s1(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.draft.update(cx, |draft, cx| {
            draft.set_value("see", window, cx);
            draft.set_selected_range(3..3, cx);
        });
        let dropped = [
            PathBuf::from("/work/a.rs"),
            PathBuf::from("/work/my notes.txt"),
        ];
        composer.dropped(&dropped, window, cx);
    });
    assert_eq!(
        draft(&composer, cx),
        "see /work/a.rs \"/work/my notes.txt\""
    );
    assert!(chips(&composer, cx).is_empty());
}

// Pins: g0-session#24
#[gpui::test]
fn a_pasted_image_attaches_and_text_pastes_as_usual(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    queue_upload(&client, "/box/pasted.png");
    let image = ClipboardItem::new_image(&Image::from_bytes(ImageFormat::Png, vec![1, 2, 3]));
    let taken = composer.update(cx, |composer, cx| composer.pasted(&image, cx));
    assert!(taken, "the draft gets no text for it");
    cx.run_until_parked();
    assert_eq!(
        chips(&composer, cx),
        [(
            "Pasted image.png".to_string(),
            "saved /box/pasted.png".to_string()
        )]
    );
    let text = ClipboardItem::new_string("words".into());
    let taken = composer.update(cx, |composer, cx| composer.pasted(&text, cx));
    assert!(!taken, "a text paste is the draft's");
}

// Pins: g0-session#24
#[gpui::test]
fn removing_a_chip_drops_its_upload_and_it_goes_out_with_nothing(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    let path = file("removed", "gone.png", b"bytes");
    queue_upload(&client, "/box/gone.png");
    composer.update(cx, |composer, cx| {
        composer.attach_paths(&[path], cx);
        let id = composer.attachments[0].id;
        composer.remove_attachment(id, cx);
        assert!(composer.uploads.is_empty(), "its upload is let go");
    });
    cx.run_until_parked();
    assert!(
        chips(&composer, cx).is_empty(),
        "a late answer brings it back"
    );
    client.queue_answer(Ok(json!({ "runId": "r1" })));
    type_and_send(&composer, "hi", cx);
    assert_eq!(sends(&client), ["hi"]);
}
