//! The Context tab on the fake client: when it reads the selected session's files, and what it shows.

use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{Entity, VisualTestContext};
use serde_json::{Value, json};

use super::{ContextPane, FileTouched, Files};
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

fn reads(client: &FakeClient) -> Vec<Value> {
    client
        .calls()
        .into_iter()
        .map(|(call, args)| {
            assert_eq!(call, "rows/files");
            args
        })
        .collect()
}

fn files(pane: &Entity<ContextPane>, cx: &mut VisualTestContext) -> Files {
    pane.read_with(cx, |pane, _| pane.files.clone())
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
        Files::Read(vec![file("src/b.rs", 2, 0), file("src/a.rs", 5, 1)])
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
    assert_eq!(files(&pane, cx), Files::Read(vec![]));

    shown(&pane, false, cx);
    pick(Some("s3"), cx);
    assert_eq!(reads(&client).len(), 2, "hidden again, it reads nothing");
    assert_eq!(
        files(&pane, cx),
        Files::Reading,
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
    assert_eq!(files(&pane, cx), Files::Failed("disk is full".into()));
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(cx.debug_bounds("context-note-failed").is_some());

    // As the socket closing answers a waiting call: nothing reads again, so it mustn't stay on "Reading".
    client.queue_answer(Err(CallError::Aborted));
    pick(Some("s2"), cx);
    assert_eq!(files(&pane, cx), Files::Failed("it was cancelled".into()));
}
