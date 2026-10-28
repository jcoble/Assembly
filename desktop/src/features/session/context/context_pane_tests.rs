//! The Context tab on the fake client: when it reads the selected session's files and attachments, and what it shows.

use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::component::WindowExt as _;
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{AppContext as _, Entity, Modifiers, VisualTestContext};
use serde_json::{Value, json};

use super::listing::Listing;
use super::{ContextPane, FileTouched, names};
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::client::session_client::CallError;

fn open(cx: &mut TestAppContext) -> (Entity<ContextPane>, FakeClient, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let client = FakeClient::default();
    let fake = client.clone();
    let (pane, cx) = cx.add_window_view(move |_, cx| ContextPane::new(Rc::new(fake), cx));
    (pane, client, cx)
}

fn pick(session: Option<&str>, cx: &mut VisualTestContext) {
    let session = session.map(String::from);
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| selected.select(session, cx))
    });
    cx.run_until_parked();
}

fn shown(pane: &Entity<ContextPane>, active: bool, cx: &mut VisualTestContext) {
    pane.update_in(cx, |pane, window, cx| pane.set_active(active, window, cx));
    cx.run_until_parked();
}

/// The pane in a kit root, which a full-size image needs to open.
fn open_in_root(
    cx: &mut TestAppContext,
) -> (Entity<ContextPane>, FakeClient, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let client = FakeClient::default();
    let fake = client.clone();
    let pane = cx.update(|cx| cx.new(|cx| ContextPane::new(Rc::new(fake), cx)));
    let inner = pane.clone();
    let (_, cx) =
        cx.add_window_view(move |window, cx| gpui_kit::base::Root::new(inner, window, cx));
    (pane, client, cx)
}

/// The calls a read made of `method`, by their args.
fn calls_of(client: &FakeClient, method: &str) -> Vec<Value> {
    client
        .calls()
        .into_iter()
        .filter(|(call, _)| call == method)
        .map(|(_, args)| args)
        .collect()
}

fn reads(client: &FakeClient) -> Vec<Value> {
    calls_of(client, "rows/files")
}

fn files(pane: &Entity<ContextPane>, cx: &mut VisualTestContext) -> Listing<FileTouched> {
    pane.read_with(cx, |pane, _| pane.files.clone())
}

fn attachments(
    pane: &Entity<ContextPane>,
    cx: &mut VisualTestContext,
) -> Listing<(String, String)> {
    pane.read_with(cx, |pane, _| pane.attachments.clone())
}

fn image(path: &str, name: &str) -> (String, String) {
    (path.into(), name.into())
}

/// Shows the tab on s1, its files read empty and its prompts' attachment lines as given.
fn shown_with(
    prompts: Value,
    pane: &Entity<ContextPane>,
    client: &FakeClient,
    cx: &mut VisualTestContext,
) {
    client.queue_answer(Ok(json!([])));
    client.queue_answer(Ok(prompts));
    pick(Some("s1"), cx);
    shown(pane, true, cx);
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

fn click(selector: &'static str, cx: &mut VisualTestContext) {
    let row = cx.debug_bounds(selector).expect("the row is drawn");
    cx.simulate_click(row.center(), Modifiers::none());
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

fn one_prompt() -> Value {
    json!({ "prompts": ["look\nAttached image: /h/attachments/s1/9f.png (shot.png)"] })
}

fn file(path: &str, added: i64, removed: i64) -> FileTouched {
    FileTouched {
        path: path.into(),
        added,
        removed,
    }
}

// Pins: g0-shape#13
#[gpui::test]
fn the_shown_tab_lists_the_picked_sessions_files_newest_first(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    client.queue_answer(Ok(json!([
        { "path": "src/b.rs", "added": 2, "removed": 0, "lastSeq": 9 },
        { "path": "src/a.rs", "added": 5, "removed": 1, "lastSeq": 4 },
    ])));
    pick(Some("s1"), cx);
    shown(&pane, true, cx);

    assert_eq!(reads(&client), [json!({ "session": "s1" })]);
    assert_eq!(
        files(&pane, cx),
        Listing::Read(vec![file("src/b.rs", 2, 0), file("src/a.rs", 5, 1)])
    );
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let b = cx
        .debug_bounds("touched-file-src/b.rs")
        .expect("b.rs has a row");
    let a = cx
        .debug_bounds("touched-file-src/a.rs")
        .expect("a.rs has a row");
    assert!(b.top() < a.top(), "the newest is first");
}

// Pins: g0-shape#13
#[gpui::test]
fn a_hidden_tab_reads_nothing_and_a_pick_while_shown_reads_again(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick(Some("s1"), cx);
    assert!(reads(&client).is_empty(), "a hidden tab reads nothing");

    client.queue_answer(Ok(json!([])));
    shown(&pane, true, cx);
    client.queue_answer(Ok(json!([])));
    pick(Some("s2"), cx);
    assert_eq!(
        reads(&client),
        [json!({ "session": "s1" }), json!({ "session": "s2" })]
    );
    assert_eq!(files(&pane, cx), Listing::Read(vec![]));

    shown(&pane, false, cx);
    pick(Some("s3"), cx);
    assert_eq!(reads(&client).len(), 2, "hidden again, it reads nothing");
    assert_eq!(
        files(&pane, cx),
        Listing::Reading,
        "nor keeps the last session's list"
    );
    client.queue_answer(Ok(json!([])));
    shown(&pane, true, cx);
    assert_eq!(
        reads(&client).last(),
        Some(&json!({ "session": "s3" })),
        "shown again, it reads"
    );
}

// Pins: g0-shape#13
#[gpui::test]
fn with_no_session_picked_it_reads_nothing_and_says_pick_one(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown(&pane, true, cx);
    assert!(reads(&client).is_empty());
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(cx.debug_bounds("context-note-pick").is_some());
}

// Pins: g0-shape#13
#[gpui::test]
fn a_failed_or_cancelled_read_says_why(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    client.queue_answer(Err(CallError::Failed("disk is full".into())));
    pick(Some("s1"), cx);
    shown(&pane, true, cx);
    assert_eq!(files(&pane, cx), Listing::Failed("disk is full".into()));
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(cx.debug_bounds("context-note-failed").is_some());

    // As the socket closing answers a waiting call: nothing reads again, so it mustn't stay on "Reading".
    client.queue_answer(Err(CallError::Aborted));
    pick(Some("s2"), cx);
    assert_eq!(files(&pane, cx), Listing::Failed("it was cancelled".into()));
}

// Pins: g0-shape#13
#[test]
fn a_file_shows_its_folder_below_the_one_every_file_shares() {
    let files = [
        file("/w/s1/src/main.rs", 3, 0),
        file("/w/s1/docs/todo.md", 3, 0),
        file("/w/s1/notes.txt", 2, 1),
    ];
    assert_eq!(
        names(&files),
        [("main.rs", "src"), ("todo.md", "docs"), ("notes.txt", "")]
    );
    assert_eq!(names(&[file("/w/s1/src/main.rs", 1, 0)]), [("main.rs", "")]);
    let apart = [file("/w/s1/a.rs", 1, 0), file("/w/s10/b.rs", 1, 0)];
    assert_eq!(names(&apart), [("a.rs", "s1"), ("b.rs", "s10")]);
}

// Pins: g0-shape#14
#[gpui::test]
fn the_shown_tab_lists_the_sessions_images_newest_prompt_first(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown_with(
        json!({ "prompts": [
            "both\nAttached image: /h/a/s1/b.png (two.png)\nAttached image: /h/a/s1/c.png (three.png)",
            // Only a prompt's trailing lines name images: this one just mentions the words.
            "what does Attached image: /h/a/s1/x.png (x.png) mean?",
            "look\nAttached image: /h/a/s1/a.png (one.png)",
        ] }),
        &pane,
        &client,
        cx,
    );

    assert_eq!(
        calls_of(&client, "rows/attachments"),
        [json!({ "session": "s1" })]
    );
    assert_eq!(
        attachments(&pane, cx),
        Listing::Read(vec![
            image("/h/a/s1/b.png", "two.png"),
            image("/h/a/s1/c.png", "three.png"),
            image("/h/a/s1/a.png", "one.png"),
        ])
    );
    let two = cx
        .debug_bounds("attachment-/h/a/s1/b.png")
        .expect("two.png has a row");
    let one = cx
        .debug_bounds("attachment-/h/a/s1/a.png")
        .expect("one.png has a row");
    assert!(
        two.top() < one.top(),
        "the newest prompt's images are first"
    );
}

// Pins: g0-shape#14
#[gpui::test]
fn a_session_with_no_images_says_where_they_come_from(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown_with(json!({ "prompts": [] }), &pane, &client, cx);
    assert_eq!(attachments(&pane, cx), Listing::Read(vec![]));
    assert!(cx.debug_bounds("attachments-note-empty").is_some());
}

// Pins: g0-shape#14
#[gpui::test]
fn a_session_with_older_images_left_out_says_so(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    let mut answer = one_prompt();
    answer["more"] = json!(true);
    shown_with(answer, &pane, &client, cx);
    assert!(cx.debug_bounds("attachments-note-more").is_some());

    client.queue_answer(Ok(json!([])));
    client.queue_answer(Ok(one_prompt()));
    pick(Some("s2"), cx);
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(
        cx.debug_bounds("attachments-note-more").is_none(),
        "a session with none left out doesn't"
    );
}

// Pins: g0-shape#14
#[gpui::test]
fn a_failed_attachments_read_says_why_and_leaves_the_files(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    client.queue_answer(Ok(
        json!([{ "path": "src/a.rs", "added": 1, "removed": 0, "lastSeq": 2 }]),
    ));
    client.queue_answer(Err(CallError::Failed("disk is full".into())));
    pick(Some("s1"), cx);
    shown(&pane, true, cx);
    assert_eq!(
        files(&pane, cx),
        Listing::Read(vec![file("src/a.rs", 1, 0)])
    );
    assert_eq!(
        attachments(&pane, cx),
        Listing::Failed("disk is full".into())
    );
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(cx.debug_bounds("attachments-note-failed").is_some());
}

// Pins: g0-shape#14
#[gpui::test]
fn a_click_reads_the_image_by_its_file_name_and_opens_it_full_size(cx: &mut TestAppContext) {
    let (pane, client, cx) = open_in_root(cx);
    shown_with(one_prompt(), &pane, &client, cx);

    // Two pieces, as a large image comes: the second starts where the first ended.
    client.queue_answer(Ok(
        json!({ "data": "aA==", "size": 2, "mime": "image/png" }),
    ));
    client.queue_answer(Ok(
        json!({ "data": "aQ==", "size": 2, "mime": "image/png" }),
    ));
    click("attachment-/h/attachments/s1/9f.png", cx);
    assert_eq!(
        calls_of(&client, "attachments/read"),
        [
            json!({ "session": "s1", "name": "9f.png", "offset": 0 }),
            json!({ "session": "s1", "name": "9f.png", "offset": 1 }),
        ]
    );
    assert!(
        cx.update(|window, cx| window.has_active_dialog(cx)),
        "the image is open"
    );
}

// Pins: g0-shape#14
#[gpui::test]
fn an_image_that_wont_open_says_why_until_another_session_is_picked(cx: &mut TestAppContext) {
    let (pane, client, cx) = open_in_root(cx);
    shown_with(one_prompt(), &pane, &client, cx);

    client.queue_answer(Err(CallError::Failed("no such attachment".into())));
    click("attachment-/h/attachments/s1/9f.png", cx);
    assert!(!cx.update(|window, cx| window.has_active_dialog(cx)));
    let failed =
        |cx: &mut VisualTestContext| pane.read_with(cx, |pane, _| pane.open_failed.clone());
    assert_eq!(failed(cx), Some("no such attachment".into()));
    assert!(cx.debug_bounds("attachments-open-failed").is_some());

    client.queue_answer(Ok(json!([])));
    client.queue_answer(Ok(json!({ "prompts": [] })));
    pick(Some("s2"), cx);
    assert_eq!(failed(cx), None, "another session doesn't keep it");
}

// Pins: g0-shape#14
#[gpui::test]
fn a_remote_sessions_images_dont_open_and_say_which_machine_has_them(cx: &mut TestAppContext) {
    let (pane, client, cx) = open_in_root(cx);
    let mut answer = one_prompt();
    answer["machine"] = json!("Workbox Test");
    shown_with(answer, &pane, &client, cx);

    assert_eq!(
        pane.read_with(cx, |pane, _| pane.machine.clone()),
        Some("Workbox Test".into())
    );
    assert!(cx.debug_bounds("attachments-note-remote").is_some());
    click("attachment-/h/attachments/s1/9f.png", cx);
    assert!(
        calls_of(&client, "attachments/read").is_empty(),
        "a click reads nothing"
    );
    assert!(!cx.update(|window, cx| window.has_active_dialog(cx)));
}
