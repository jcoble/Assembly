use super::{DiscardRequest, ask};
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use crate::features::source_control::git_action::GitWrites;
use crate::features::source_control::git_status::ChangedFile;
use crate::features::source_control::git_target::GitTarget;
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{AppContext as _, EmptyView};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};
fn file(path: &str, index: &str, worktree: &str) -> ChangedFile {
    ChangedFile {
        path: path.into(),
        original_path: None,
        index: index.into(),
        worktree: worktree.into(),
    }
}
// Pins: g9#3
#[test]
fn discard_everything_leaves_new_files_unless_ticked() {
    let request = DiscardRequest::Everything {
        fingerprint: "seen".into(),
        listed: vec![file("a", ".", "M"), file("new", "?", "?")],
        omitted: 0,
    };
    let kept = request.args("s", false);
    assert_eq!(kept["includeUntracked"], false);
    assert_eq!(kept["expectedFingerprint"], "seen");
    assert_eq!(request.question(false).confirm, "Discard everything");
    assert_eq!(request.args("s", true)["includeUntracked"], true);
    assert_eq!(request.question(true).confirm, "Discard and delete");
    assert_eq!(request.question(false).title, "Discard every change?");
}
#[test]
fn discard_of_a_staged_new_file_warns_it_will_be_deleted() {
    let question = DiscardRequest::Files(vec![file("new", "A", ".")]).question(false);
    assert_eq!(question.title, "Discard changes to this file?");
    assert_eq!(question.confirm, "Delete the file");
    assert_eq!(
        question.lines,
        ["1 file git has never tracked will be deleted from disk."]
    );
}
#[test]
fn several_files_say_how_many_of_each_and_keep_their_own_words() {
    let question = DiscardRequest::Files(vec![
        file("a", ".", "M"),
        file("b", "M", "."),
        file("new", "?", "?"),
    ])
    .question(false);
    assert_eq!(question.title, "Discard changes to 3 files?");
    assert_eq!(question.confirm, "Discard changes");
    assert_eq!(
        question.lines,
        [
            "1 file git has never tracked will be deleted from disk.",
            "2 files goes back to how the last commit has it, staged or not."
        ]
    );
}
#[test]
fn a_rename_restores_both_paths_and_new_files_go_in_their_own_list() {
    let mut renamed = file("new-name", "R", ".");
    renamed.original_path = Some("old-name".into());
    let args = DiscardRequest::Files(vec![renamed, file("n", "?", "?")]).args("s", false);
    assert_eq!(args["tracked"], serde_json::json!(["new-name", "old-name"]));
    assert_eq!(args["untracked"], serde_json::json!(["n"]));
    assert_eq!(args["expected"].as_array().unwrap().len(), 2);
}
// Pins: g9#3
#[gpui::test]
async fn cancel_sends_nothing(cx: &mut TestAppContext) {
    let discards = Arc::new(Mutex::new(Vec::<Value>::new()));
    let seen = discards.clone();
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return vec![];
        }
        if frame["method"] == "discard" {
            seen.lock().unwrap().push(frame["args"].clone());
        }
        let _ = heard.unbounded_send(());
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{}}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let (_, visual) = cx.add_window_view(|window, cx| {
        let content = cx.new(|_| EmptyView);
        gpui_kit::base::Root::new(content, window, cx)
    });
    let writes = visual.update(|_, cx| GitWrites::new(cx));
    let request = DiscardRequest::Files(vec![file("a.rs", " ", "M")]);
    let asked = |visual: &mut gpui_kit::VisualTestContext| {
        let (request, action) = (request.clone(), writes.action.clone());
        visual.update(|window, cx| ask(request, GitTarget::new("s1"), action, window, cx));
        visual.run_until_parked();
        visual.update(|window, cx| window.draw(cx).clear(cx));
    };
    asked(visual);
    visual.simulate_keystrokes("escape");
    visual.run_until_parked();
    let barrier = visual.update(|_, cx| {
        SocketClient::global(cx)
            .read(cx)
            .call::<Value>("test", "test_barrier", json!({}), None)
    });
    let _ = barrier.await;
    visual.run_until_parked();
    assert!(discards.lock().unwrap().is_empty());
    asked(visual);
    visual.simulate_keystrokes("enter");
    until(
        "confirming sends the discard",
        &mut changes,
        &changed,
        || {
            visual.run_until_parked();
            !discards.lock().unwrap().is_empty()
        },
    )
    .await;
    assert_eq!(discards.lock().unwrap()[0]["session"], "s1");
}
