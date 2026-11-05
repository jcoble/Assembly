//! The Files touched card on the fake client: its count chips, what a click opens, and an older remote's answer.

use std::cell::RefCell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::component::ActiveTheme as _;
use gpui_kit::component::WindowExt as _;
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{AppContext as _, Entity, Modifiers, VisualTestContext};
use serde_json::{Value, json};

use super::super::listing::Listing;
use super::super::{ContextPane, FileTouched};
use super::{RowLook, names, row_looks, touched};
use crate::core::engine::SessionKey;
use crate::core::intents::open_file::OpenFile;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::client::fake_client::FakeClient;

type Opened = Rc<RefCell<Vec<OpenFile>>>;

/// The pane in a kit root, which a full-size image needs, with every `OpenFile` dispatched kept.
fn open(
    cx: &mut TestAppContext,
) -> (
    Entity<ContextPane>,
    FakeClient,
    Opened,
    &mut VisualTestContext,
) {
    let opened = Opened::default();
    let kept = opened.clone();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        cx.on_action(move |action: &OpenFile, _| kept.borrow_mut().push(action.clone()));
    });
    let client = FakeClient::default();
    let fake = client.clone();
    let pane = cx.update(|cx| cx.new(|cx| ContextPane::new(Rc::new(fake), cx)));
    let inner = pane.clone();
    let (_, cx) =
        cx.add_window_view(move |window, cx| gpui_kit::base::Root::new(inner, window, cx));
    (pane, client, opened, cx)
}

fn source() -> ExpectedRowSource {
    ExpectedRowSource {
        machine: "a".into(),
        generation: 3,
        route_machine: None,
    }
}

/// Shows the tab on s1 from machine a's source, with `files` as its list and no images.
fn shown_with(
    files: Value,
    pane: &Entity<ContextPane>,
    client: &FakeClient,
    cx: &mut VisualTestContext,
) {
    let key = SessionKey {
        machine: "a".into(),
        id: "s1".into(),
    };
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.commit_open(key, source(), false, cx)
        })
    });
    client.queue_answer(Ok(files));
    client.queue_answer(Ok(json!({ "prompts": [] })));
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    cx.run_until_parked();
    draw(cx);
}

fn draw(cx: &mut VisualTestContext) {
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

fn click(selector: &'static str, cx: &mut VisualTestContext) {
    let row = cx.debug_bounds(selector).expect("the row is drawn");
    cx.simulate_click(row.center(), Modifiers::none());
    cx.run_until_parked();
    draw(cx);
}

/// How the shown list's rows are drawn.
fn shown_looks(pane: &Entity<ContextPane>, cx: &mut VisualTestContext) -> Vec<RowLook> {
    let Listing::Read(files) = pane.read_with(cx, |pane, _| pane.files.clone()) else {
        panic!("the list is read");
    };
    cx.update(|_, cx| row_looks(&files, cx))
}

fn file(path: &str) -> FileTouched {
    FileTouched {
        path: path.into(),
        added: 0,
        removed: 0,
        count: 1,
        name: None,
    }
}

fn opening(path: &str) -> OpenFile {
    OpenFile {
        session_id: "s1".into(),
        path: path.into(),
        expected_source: Some(source()),
        line: None,
        column: None,
        pinned: false,
    }
}

#[gpui::test]
fn a_count_chip_shows_only_when_more_than_one_call_named_the_file(cx: &mut TestAppContext) {
    let (pane, client, _, cx) = open(cx);
    shown_with(
        json!([
            { "path": "src/b.rs", "added": 0, "removed": 0, "count": 3, "lastSeq": 9 },
            { "path": "src/a.rs", "added": 2, "removed": 0, "count": 1, "lastSeq": 4 },
        ]),
        &pane,
        &client,
        cx,
    );
    assert!(
        cx.debug_bounds("touched-count-src/b.rs").is_some(),
        "three calls show 3"
    );
    assert!(cx.debug_bounds("touched-file-src/a.rs").is_some());
    assert!(
        cx.debug_bounds("touched-count-src/a.rs").is_none(),
        "one call shows no chip"
    );
}

#[gpui::test]
fn a_click_opens_the_file_through_the_normal_open_with_the_listed_source(cx: &mut TestAppContext) {
    let (pane, client, opened, cx) = open(cx);
    shown_with(
        json!([{ "path": "src/a.rs", "added": 1, "removed": 0, "count": 2, "lastSeq": 4 }]),
        &pane,
        &client,
        cx,
    );
    click("touched-file-src/a.rs", cx);
    assert!(*opened.borrow() == [opening("src/a.rs")]);
}

#[gpui::test]
fn a_file_outside_the_folder_is_a_normal_row_and_opens_by_its_whole_path(cx: &mut TestAppContext) {
    let (pane, client, opened, cx) = open(cx);
    shown_with(
        json!([
            { "path": "/home/x/.codex/AGENTS.md", "added": 0, "removed": 0, "count": 1, "lastSeq": 9 },
            { "path": "src/a.rs", "added": 0, "removed": 0, "count": 1, "lastSeq": 4 },
        ]),
        &pane,
        &client,
        cx,
    );
    let outside = cx
        .debug_bounds("touched-file-/home/x/.codex/AGENTS.md")
        .expect("it has a row");
    let inside = cx
        .debug_bounds("touched-file-src/a.rs")
        .expect("it has a row");
    assert_eq!(outside.size, inside.size, "the same row as any other");
    let looks = shown_looks(&pane, cx);
    let muted = cx.update(|_, cx| cx.theme().muted_foreground);
    assert_eq!(
        (looks[0].name_color, looks[0].folder_color),
        (looks[1].name_color, looks[1].folder_color),
        "drawn in the same colours as a file inside the folder"
    );
    assert_ne!(looks[0].name_color, muted, "its name isn't muted");
    click("touched-file-/home/x/.codex/AGENTS.md", cx);
    assert!(*opened.borrow() == [opening("/home/x/.codex/AGENTS.md")]);
}

#[gpui::test]
fn an_attached_image_shows_its_name_and_opens_full_size_by_its_file_name(cx: &mut TestAppContext) {
    let (pane, client, opened, cx) = open(cx);
    shown_with(
        json!([{
            "path": "/h/attachments/s1/9f.png", "added": 0, "removed": 0, "count": 1, "lastSeq": 2, "name": "shot.png"
        }]),
        &pane,
        &client,
        cx,
    );
    client.queue_answer(Ok(
        json!({ "data": "aA==", "size": 1, "mime": "image/png" }),
    ));
    click("touched-file-/h/attachments/s1/9f.png", cx);
    let reads: Vec<Value> = client
        .calls()
        .into_iter()
        .filter(|(call, _)| call == "attachments/read")
        .map(|(_, args)| args)
        .collect();
    assert_eq!(
        reads,
        [json!({ "session": "s1", "name": "9f.png", "offset": 0, "expectedSource": source() })]
    );
    assert!(
        cx.update(|window, cx| window.has_active_dialog(cx)),
        "the image is open"
    );
    assert!(
        opened.borrow().is_empty(),
        "an image isn't sent to the Editor"
    );
}

#[test]
fn an_older_remotes_answer_with_no_count_or_name_still_reads() {
    let answer = json!([{ "path": "src/a.rs", "added": 5, "removed": 1, "lastSeq": 4 }]);
    assert_eq!(
        touched(Ok(answer)),
        Listing::Read(vec![FileTouched {
            path: "src/a.rs".into(),
            added: 5,
            removed: 1,
            count: 0,
            name: None,
        }])
    );
}

#[gpui::test]
fn an_unavailable_source_reads_the_total_as_answered_with_none(cx: &mut TestAppContext) {
    let (pane, client, _, cx) = open(cx);
    let key = SessionKey {
        machine: "a".into(),
        id: "s1".into(),
    };
    let elsewhere = ExpectedRowSource {
        machine: "b".into(),
        ..source()
    };
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.commit_open(key, elsewhere, false, cx)
        })
    });
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    cx.run_until_parked();
    let (files, total) = pane.read_with(cx, |pane, _| (pane.files.clone(), pane.total));
    assert!(matches!(files, Listing::Failed(_)));
    assert_eq!(total, Some(None), "not \"Reading…\" for good");
    assert!(client.calls().is_empty(), "nothing is read");
}

#[gpui::test]
fn an_attached_row_shows_the_name_it_was_attached_with(cx: &mut TestAppContext) {
    let (pane, client, _, cx) = open(cx);
    shown_with(
        json!([
            { "path": "/h/attachments/s1/9f.png", "added": 0, "removed": 0, "count": 1, "lastSeq": 2, "name": "shot.png" },
            { "path": "/h/attachments/s1/a.rs", "added": 0, "removed": 0, "count": 1, "lastSeq": 1 },
        ]),
        &pane,
        &client,
        cx,
    );
    let names: Vec<String> = shown_looks(&pane, cx)
        .into_iter()
        .map(|look| look.name)
        .collect();
    assert_eq!(
        names,
        ["shot.png", "a.rs"],
        "not the stored file name 9f.png"
    );
}

#[test]
fn a_whole_path_outside_the_shared_folder_keeps_its_leading_slash() {
    let files = [file("/home/x/.codex/AGENTS.md"), file("src/a.rs")];
    assert_eq!(
        names(&files),
        [("AGENTS.md", "/home/x/.codex"), ("a.rs", "src")]
    );
}

#[test]
fn a_home_path_outside_the_shared_folder_keeps_its_leading_tilde() {
    let files = [file("~/.codex/AGENTS.md"), file("src/a.rs")];
    assert_eq!(names(&files), [("AGENTS.md", "~/.codex"), ("a.rs", "src")]);
}
