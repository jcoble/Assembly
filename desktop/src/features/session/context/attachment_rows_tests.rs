//! The Attachments card's rows on the fake client: each image's type, the draft's images above the sent ones, and the
//! sent images read again when a prompt is sent while the tab shows.

use std::rc::Rc;
use std::sync::Arc;

use gpui::TestAppContext;
use gpui_kit::component::WindowExt as _;
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{AppContext as _, Entity, Image, ImageFormat, Modifiers, VisualTestContext};
use serde_json::{Value, json};

use super::super::listing::Listing;
use super::super::{ContextPane, FileTouched};
use super::{sent_type, type_words};
use crate::core::engine::SessionKey;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::client::session_client::CallError;
use crate::features::session::context::live_facts::{DraftImage, LiveFacts};

/// The pane in a kit root, which a full-size image needs, with the live facts set up.
fn open(cx: &mut TestAppContext) -> (Entity<ContextPane>, FakeClient, &mut VisualTestContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        LiveFacts::init(cx);
    });
    let client = FakeClient::default();
    let fake = client.clone();
    let pane = cx.update(|cx| cx.new(|cx| ContextPane::new(Rc::new(fake), cx)));
    let inner = pane.clone();
    let (_, cx) =
        cx.add_window_view(move |window, cx| gpui_kit::base::Root::new(inner, window, cx));
    (pane, client, cx)
}

fn draw(cx: &mut VisualTestContext) {
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

fn calls_of(client: &FakeClient, method: &str) -> Vec<Value> {
    client
        .calls()
        .into_iter()
        .filter(|(call, _)| call == method)
        .map(|(_, args)| args)
        .collect()
}

fn prompts(lines: &[&str]) -> Value {
    json!({ "prompts": lines })
}

/// Picks s1 and shows the tab, its files and images read as given and its total not reported.
fn shown_on_s1(
    files: Value,
    images: Value,
    pane: &Entity<ContextPane>,
    client: &FakeClient,
    cx: &mut VisualTestContext,
) {
    client.queue_answer(Ok(files));
    client.queue_answer(Ok(images));
    client.queue_answer(Ok(json!({})));
    cx.update(|_, cx| {
        SelectedSession::global(cx)
            .update(cx, |selected, cx| selected.select(Some("s1".into()), cx))
    });
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    cx.run_until_parked();
    draw(cx);
}

fn picture() -> Arc<Image> {
    Arc::new(Image::from_bytes(ImageFormat::Png, vec![1, 2, 3]))
}

fn draft(id: u64, name: &str, saved_path: Option<&str>, preview: Option<Arc<Image>>) -> DraftImage {
    DraftImage {
        id,
        name: name.to_string().into(),
        mime: Some("image/png"),
        saved_path: saved_path.map(String::from),
        preview,
    }
}

fn facts(
    pane: &Entity<ContextPane>,
    session: &str,
    prompt: i64,
    drafts: Vec<DraftImage>,
) -> LiveFacts {
    LiveFacts {
        publisher: pane.entity_id(),
        session: session.into(),
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
        drafts,
    }
}

fn publish(facts: LiveFacts, cx: &mut VisualTestContext) {
    cx.update(|_, cx| LiveFacts::publish(facts, cx));
    cx.run_until_parked();
    draw(cx);
}

/// How many rows the card lists, which its count chip shows.
fn rows(pane: &Entity<ContextPane>, cx: &mut VisualTestContext) -> usize {
    pane.read_with(cx, |pane, cx| {
        let (drafts, sent) = pane.listed(cx);
        drafts.len() + sent.len()
    })
}

fn images(pane: &Entity<ContextPane>, cx: &mut VisualTestContext) -> Listing<(String, String)> {
    pane.read_with(cx, |pane, _| pane.attachments.clone())
}

#[test]
fn a_sent_images_type_comes_from_its_extension() {
    assert_eq!(sent_type("/h/a/9f.png", "shot.png"), Some("image/png"));
    assert_eq!(sent_type("/h/a/9f.jpg", "Photo.JPG"), Some("image/jpeg"));
    assert_eq!(sent_type("/h/a/9f.jpeg", "photo.jpeg"), Some("image/jpeg"));
    assert_eq!(sent_type("/h/a/9f.gif", "spin.gif"), Some("image/gif"));
    assert_eq!(sent_type("/h/a/9f.webp", "small.webp"), Some("image/webp"));
    assert_eq!(
        sent_type("/h/a/9f.png", "pasted"),
        Some("image/png"),
        "a name with no extension reads the saved file's"
    );
    assert_eq!(type_words(Some("image/png")), "image/png");
}

#[test]
fn an_unknown_type_reads_as_not_reported() {
    assert_eq!(sent_type("/h/a/9f.bmp", "old.bmp"), None);
    assert_eq!(sent_type("/h/a/9f", "pasted"), None);
    assert_eq!(type_words(None), "type not reported");
}

#[gpui::test]
fn draft_images_come_first_and_the_chip_counts_every_row(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown_on_s1(
        json!([]),
        prompts(&["look\nAttached image: /h/attachments/s1/9f.png (shot.png)"]),
        &pane,
        &client,
        cx,
    );
    publish(
        facts(
            &pane,
            "s1",
            2,
            vec![
                draft(7, "new.png", None, Some(picture())),
                draft(8, "next.png", None, None),
            ],
        ),
        cx,
    );

    let first = cx
        .debug_bounds("attachment-draft-7")
        .expect("the draft is drawn");
    let second = cx
        .debug_bounds("attachment-draft-8")
        .expect("the restored draft is drawn");
    let sent = cx
        .debug_bounds("attachment-/h/attachments/s1/9f.png")
        .expect("the sent image is drawn");
    assert!(
        first.top() < second.top() && second.top() < sent.top(),
        "drafts come first"
    );
    assert_eq!(rows(&pane, cx), 3);
    assert!(cx.debug_bounds("attachments-note-empty").is_none());
}

#[gpui::test]
fn a_draft_opens_its_own_picture_full_size_with_no_read(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown_on_s1(json!([]), prompts(&[]), &pane, &client, cx);
    publish(
        facts(
            &pane,
            "s1",
            2,
            vec![draft(7, "new.png", None, Some(picture()))],
        ),
        cx,
    );

    let row = cx
        .debug_bounds("attachment-draft-7")
        .expect("the draft is drawn");
    cx.simulate_click(row.center(), Modifiers::none());
    cx.run_until_parked();
    assert!(calls_of(&client, "attachments/read").is_empty());
    assert!(
        cx.update(|window, cx| window.has_active_dialog(cx)),
        "the picture is open"
    );
}

#[gpui::test]
fn another_sessions_drafts_are_not_listed(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown_on_s1(json!([]), prompts(&[]), &pane, &client, cx);
    publish(
        facts(&pane, "s2", 2, vec![draft(7, "new.png", None, None)]),
        cx,
    );

    assert!(cx.debug_bounds("attachment-draft-7").is_none());
    assert!(cx.debug_bounds("attachments-note-empty").is_some());
    assert_eq!(rows(&pane, cx), 0);
}

#[gpui::test]
fn a_saved_draft_also_listed_as_sent_shows_once_as_sent(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown_on_s1(
        json!([]),
        prompts(&["look\nAttached image: /h/attachments/s1/9f.png (shot.png)"]),
        &pane,
        &client,
        cx,
    );
    publish(
        facts(
            &pane,
            "s1",
            2,
            vec![
                draft(7, "shot.png", Some("/h/attachments/s1/9f.png"), None),
                draft(8, "other.png", Some("/h/attachments/s1/aa.png"), None),
            ],
        ),
        cx,
    );

    assert!(
        cx.debug_bounds("attachment-draft-7").is_none(),
        "shown as sent"
    );
    assert!(
        cx.debug_bounds("attachment-/h/attachments/s1/9f.png")
            .is_some()
    );
    assert!(
        cx.debug_bounds("attachment-draft-8").is_some(),
        "not sent yet"
    );
    assert_eq!(rows(&pane, cx), 2);
}

#[gpui::test]
fn a_new_prompt_reads_only_the_attachments_again_and_keeps_the_lists_meanwhile(
    cx: &mut TestAppContext,
) {
    let (pane, client, cx) = open(cx);
    let files = json!([{ "path": "src/a.rs", "added": 1, "removed": 0, "count": 1 }]);
    let one = "look\nAttached image: /h/attachments/s1/9f.png (shot.png)";
    shown_on_s1(files, prompts(&[one]), &pane, &client, cx);
    publish(
        facts(&pane, "s1", 2, vec![draft(7, "new.png", None, None)]),
        cx,
    );
    assert_eq!(
        calls_of(&client, "rows/attachments").len(),
        1,
        "the first facts only set where it stands"
    );

    let two = "again\nAttached image: /h/attachments/s1/b0.png (new.png)";
    client.queue_answer(Ok(prompts(&[two, one])));
    cx.update(|_, cx| LiveFacts::publish(facts(&pane, "s1", 4, Vec::new()), cx));
    assert_eq!(
        images(&pane, cx),
        Listing::Read(vec![("/h/attachments/s1/9f.png".into(), "shot.png".into())]),
        "the list stays while it reads"
    );
    cx.run_until_parked();
    draw(cx);

    assert_eq!(
        calls_of(&client, "rows/attachments"),
        [json!({ "session": "s1" }), json!({ "session": "s1" })]
    );
    assert_eq!(calls_of(&client, "rows/files").len(), 1);
    assert_eq!(calls_of(&client, "rows/tokens").len(), 1);
    assert_eq!(
        images(&pane, cx),
        Listing::Read(vec![
            ("/h/attachments/s1/b0.png".into(), "new.png".into()),
            ("/h/attachments/s1/9f.png".into(), "shot.png".into()),
        ])
    );
    assert_eq!(
        pane.read_with(cx, |pane, _| pane.files.clone()),
        Listing::Read(vec![FileTouched {
            path: "src/a.rs".into(),
            added: 1,
            removed: 0,
            count: 1,
            name: None,
        }])
    );
    assert!(
        cx.debug_bounds("attachment-/h/attachments/s1/b0.png")
            .is_some()
    );
}

#[gpui::test]
fn a_hidden_tab_reads_nothing_when_a_prompt_is_sent(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown_on_s1(json!([]), prompts(&[]), &pane, &client, cx);
    publish(facts(&pane, "s1", 2, Vec::new()), cx);
    pane.update_in(cx, |pane, window, cx| pane.set_active(false, window, cx));
    cx.run_until_parked();

    publish(facts(&pane, "s1", 4, Vec::new()), cx);
    assert_eq!(calls_of(&client, "rows/attachments").len(), 1);
}

#[gpui::test]
fn a_refused_re_read_marks_the_source_invalid(cx: &mut TestAppContext) {
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
            selected.commit_open(key.clone(), source, false, cx)
        })
    });
    client.queue_answer(Ok(json!([])));
    client.queue_answer(Ok(prompts(&[])));
    client.queue_answer(Ok(json!({})));
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    cx.run_until_parked();
    let mut live = facts(&pane, "s1", 2, Vec::new());
    live.key = Some(key);
    publish(live.clone(), cx);

    client.queue_answer(Err(CallError::Refused {
        code: "source_changed".into(),
        message: "Owner replaced".into(),
    }));
    live.newest_prompt_seq = Some(4);
    publish(live, cx);

    assert_eq!(calls_of(&client, "rows/attachments").len(), 2);
    assert_eq!(
        pane.read_with(cx, |pane, _| pane
            .context_scope
            .as_ref()
            .map(|scope| scope.valid)),
        Some(false)
    );
}
