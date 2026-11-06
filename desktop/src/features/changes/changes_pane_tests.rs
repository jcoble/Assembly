//! When the Changes tab reads and what it shows, on the socket client against `FakeBackend` (scm#13, scm#14, scm#17,
//! scm#18).
use std::sync::{Arc, Mutex};

use futures_channel::mpsc::{UnboundedSender, unbounded};
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use gpui_kit::component::diff::DiffMode;
use gpui_kit::{Entity, Subscription};
use serde_json::{Value, json};

use super::view::Waiting;
use super::{ChangesPane, CommitPick, Picked, Scope, Target};
use crate::core::socket::checkout_selector::CheckoutSelector;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::socket::test_wait::until;
use crate::core::state::inspected_checkout::{Inspected, InspectedCheckout};
use crate::core::state::selected_session::SelectedSession;
use crate::features::changes::file_diff::FileDiff;

fn patch(path: &str, new: &str) -> String {
    format!("diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -1 +1 @@\n-old\n+{new}\n")
}
#[derive(Default)]
struct Seen {
    /// Each `git/diff` in order, with its id.
    reads: Vec<(String, Value)>,
    cancels: Vec<Value>,
    args: Vec<Value>,
    statuses: Vec<Value>,
    branches: Vec<Value>,
    commits: Vec<Value>,
    /// What each `git/status` lists.
    files: Vec<Value>,
    /// What `git/diff` answers for a path; a path that isn't here never answers.
    diffs: Vec<(String, Value)>,
}
fn changed(path: &str) -> Value {
    json!({"path":path,"originalPath":null,"index":" ","worktree":"M"})
}
fn untracked(path: &str) -> Value {
    json!({"path":path,"originalPath":null,"index":"?","worktree":"?"})
}
fn diff_answer(path: &str, staged: &str, unstaged: &str) -> (String, Value) {
    (
        path.to_string(),
        json!({"path":path,"staged":staged,"unstaged":unstaged,"binary":false,"truncated":false}),
    )
}
fn backend(seen: Arc<Mutex<Seen>>, heard: UnboundedSender<()>) -> FakeBackend {
    FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let id = frame["id"].clone();
        let result =
            |value: Value| vec![Reply::Frame(json!({"t":"result","id":id,"result":value}))];
        let mut seen = seen.lock().unwrap();
        let replies = match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("call"), Some("status")) => {
                seen.statuses.push(frame["args"].clone());
                result(
                    json!({"branch":"main","head":"a","ahead":0,"behind":0,"hasUpstream":false,
                    "files":seen.files,"totalFiles":seen.files.len(),"totalStaged":0,"fingerprint":"f"}),
                )
            }
            (Some("call"), Some("diff")) => {
                let path = frame["args"]["path"]
                    .as_str()
                    .unwrap_or_default()
                    .to_string();
                seen.reads.push((path.clone(), id.clone()));
                seen.args.push(frame["args"].clone());
                match seen.diffs.iter().find(|(known, _)| *known == path) {
                    Some((_, answer)) => result(answer.clone()),
                    None => Vec::new(),
                }
            }
            (Some("call"), Some("branchDiff")) => {
                seen.branches.push(frame["args"].clone());
                result(
                    json!({"base":"origin/main","truncated":false,"diff":patch("src/committed.rs","b")}),
                )
            }
            (Some("call"), Some("commitDiff")) => {
                seen.commits.push(frame["args"].clone());
                result(
                    json!({"path":"a.rs","staged":"","unstaged":patch("a.rs","b"),"binary":false,"truncated":false}),
                )
            }
            (Some("cancel"), _) => {
                seen.cancels.push(id.clone());
                Vec::new()
            }
            _ => Vec::new(),
        };
        drop(seen);
        _ = heard.unbounded_send(());
        replies
    })
}
fn origin() -> ExpectedRowSource {
    ExpectedRowSource {
        machine: "captured-owner".into(),
        generation: 12,
        route_machine: Some("workbox-test".into()),
    }
}
struct Open {
    pane: Entity<ChangesPane>,
    seen: Arc<Mutex<Seen>>,
    changed: UnboundedSender<()>,
    changes: futures_channel::mpsc::UnboundedReceiver<()>,
    _backend: FakeBackend,
    _heard: Subscription,
}
impl Open {
    async fn wait(
        &mut self,
        what: &str,
        cx: &mut TestAppContext,
        done: impl Fn(&Seen, &ChangesPane) -> bool,
    ) {
        let (seen, pane) = (self.seen.clone(), self.pane.clone());
        until(what, &mut self.changes, &self.changed, || {
            cx.run_until_parked();
            cx.update(|cx| done(&seen.lock().unwrap(), pane.read(cx)))
        })
        .await;
    }
}
/// A pane on selected session s1, with nothing shown yet.
fn open(cx: &mut TestAppContext, files: Vec<Value>, diffs: Vec<(String, Value)>) -> Open {
    let seen = Arc::new(Mutex::new(Seen {
        files,
        diffs,
        ..Seen::default()
    }));
    let (changed, changes) = unbounded();
    let backend = backend(seen.clone(), changed.clone());
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        SelectedSession::global(cx)
            .update(cx, |selected, cx| selected.select(Some("s1".into()), cx));
    });
    let pane = cx.new(|cx| ChangesPane::new(None, cx));
    let tell = changed.clone();
    let heard = cx.update(|cx| cx.observe(&pane, move |_, _| _ = tell.unbounded_send(())));
    Open {
        pane,
        seen,
        changed,
        changes,
        _backend: backend,
        _heard: heard,
    }
}
fn inspected(session: &str, answers: u64) -> Inspected {
    Inspected {
        session: session.into(),
        source: None,
        fingerprint: "f".into(),
        answers,
    }
}
fn linked() -> CheckoutSelector {
    CheckoutSelector {
        path: "/linked".into(),
        expected_root: "/own".into(),
    }
}

// Pins: scm#13, scm#17
#[gpui::test]
async fn the_working_copy_reads_four_files_at_once_with_the_picked_one_first(
    cx: &mut TestAppContext,
) {
    let paths = ["a.rs", "b.rs", "c.rs", "d.rs", "e.rs", "f.rs"];
    let mut open = open(
        cx,
        paths.iter().map(|path| changed(path)).collect(),
        Vec::new(),
    );
    open.pane.update(cx, |pane, cx| {
        pane.show(Picked::File("f.rs".into()), cx);
        pane.activate(true, cx);
    });
    open.wait("four reads out", cx, |seen, _| seen.reads.len() == 4)
        .await;
    let seen = open.seen.lock().unwrap();
    assert_eq!(seen.reads[0].0, "f.rs");
    assert!(seen.cancels.is_empty());
    assert_eq!(seen.args[0]["session"], "s1");
}

// Pins: scm#17
#[gpui::test]
async fn hiding_the_tab_stops_its_reads_and_showing_it_reads_the_status_again(
    cx: &mut TestAppContext,
) {
    let mut open = open(cx, vec![changed("slow.rs")], Vec::new());
    open.pane.update(cx, |pane, cx| pane.activate(true, cx));
    open.wait("the read out", cx, |seen, _| seen.reads.len() == 1)
        .await;
    open.pane.update(cx, |pane, cx| pane.activate(false, cx));
    open.wait("the read stopped", cx, |seen, _| !seen.cancels.is_empty())
        .await;
    {
        let seen = open.seen.lock().unwrap();
        assert_eq!(seen.cancels, vec![seen.reads[0].1.clone()]);
    }
    let statuses = open.seen.lock().unwrap().statuses.len();
    open.pane.update(cx, |pane, cx| pane.activate(true, cx));
    open.wait("read again", cx, |seen, _| {
        seen.statuses.len() > statuses && seen.reads.len() == 2
    })
    .await;
}

// Pins: scm#14
#[gpui::test]
async fn a_file_with_staged_and_working_lines_shows_both_under_one_tree_row(
    cx: &mut TestAppContext,
) {
    let staged = patch("src/a.rs", "staged");
    let working = patch("src/a.rs", "working");
    let mut open = open(
        cx,
        vec![changed("src/a.rs"), changed("b.rs")],
        vec![
            diff_answer("src/a.rs", &staged, &working),
            diff_answer("b.rs", "", &patch("b.rs", "x")),
        ],
    );
    open.pane.update(cx, |pane, cx| pane.activate(true, cx));
    open.wait("both shown", cx, |_, pane| {
        pane.tree
            .rows()
            .iter()
            .filter(|row| row.path.is_some())
            .count()
            == 2
    })
    .await;
    cx.update(|cx| {
        let pane = open.pane.read(cx);
        let state = pane.state.read(cx);
        let paths: Vec<&str> = state
            .files()
            .iter()
            .map(|file| file.path().as_ref())
            .collect();
        assert_eq!(
            paths,
            ["src/a.rs", "src/a.rs", "b.rs"],
            "stacked in the tree's order"
        );
        assert!(pane.both.contains("src/a.rs"));
        assert!(!pane.both.contains("b.rs"));
        assert_eq!(pane.totals, (3, 3));
        assert_eq!(pane.waiting(cx), None);
    });
}

// Pins: scm#18
#[gpui::test]
async fn branch_stacks_its_one_read_with_the_untracked_files_and_names_its_base(
    cx: &mut TestAppContext,
) {
    let mut open = open(
        cx,
        vec![untracked("new.rs"), changed("src/committed.rs")],
        vec![diff_answer("new.rs", "", &patch("new.rs", "n"))],
    );
    open.pane.update(cx, |pane, cx| {
        pane.scope = Scope::Branch;
        pane.activate(true, cx);
    });
    open.wait("both parts", cx, |_, pane| {
        pane.working.pending() == 0 && pane.working.answers().len() == 2
    })
    .await;
    let seen = open.seen.lock().unwrap();
    assert_eq!(seen.branches.len(), 1);
    assert_eq!(
        seen.reads
            .iter()
            .map(|read| read.0.as_str())
            .collect::<Vec<_>>(),
        ["new.rs"]
    );
    drop(seen);
    cx.update(|cx| {
        let pane = open.pane.read(cx);
        assert_eq!(pane.base.as_deref(), Some("origin/main"));
        assert_eq!(pane.state.read(cx).files().len(), 2);
    });
}

// Pins: scm#17
#[gpui::test]
fn changing_presentation_retains_the_same_patch_revision(cx: &mut TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
    });
    let pane = cx.new(|cx| ChangesPane::new(None, cx));
    pane.update(cx, |pane, cx| {
        let diff = FileDiff {
            path: "a".into(),
            staged: String::new(),
            unstaged: patch("a", "b"),
            binary: false,
            truncated: false,
        };
        let file = crate::features::changes::working_copy::read_file(&diff).unwrap();
        pane.working.set_only("a".into(), file);
        pane.show_answers(cx);
        let revision = pane.state.entity_id();
        pane.set_mode(DiffMode::Split, cx);
        assert_eq!(pane.state.entity_id(), revision);
        assert_eq!(pane.state.read(cx).mode(), DiffMode::Split);
        assert_eq!(pane.state.read(cx).files()[0].additions(), 1);
    });
}

// Pins: scm#17
#[gpui::test]
async fn a_status_source_control_read_reads_the_files_again(cx: &mut TestAppContext) {
    let mut open = open(
        cx,
        vec![changed("a.rs")],
        vec![diff_answer("a.rs", "", &patch("a.rs", "b"))],
    );
    open.pane.update(cx, |pane, cx| pane.activate(true, cx));
    open.wait("first read", cx, |seen, _| seen.reads.len() == 1)
        .await;
    cx.update(|cx| InspectedCheckout::publish(Some(inspected("s1", 1)), cx));
    open.wait("second read", cx, |seen, _| seen.reads.len() == 2)
        .await;
    let statuses = open.seen.lock().unwrap().statuses.len();
    cx.update(|cx| InspectedCheckout::publish(Some(inspected("s1", 1)), cx));
    cx.run_until_parked();
    assert_eq!(
        open.seen.lock().unwrap().statuses.len(),
        statuses,
        "the same answer reads nothing"
    );
}

// Pins: scm#13
#[gpui::test]
async fn a_commit_stays_on_its_own_while_source_control_moves_on(cx: &mut TestAppContext) {
    let mut open = open(cx, vec![changed("a.rs")], Vec::new());
    let pick = CommitPick {
        session: "s1".into(),
        expected_source: Some(origin()),
        sha: "a".repeat(40),
        path: "a.rs".into(),
        title: "Fix".into(),
        checkout: Some(linked()),
    };
    open.pane.update(cx, |pane, cx| {
        pane.activate(true, cx);
        pane.show(Picked::Commit(pick.clone()), cx);
    });
    open.wait("the commit read", cx, |_, pane| {
        pane.working.answers().len() == 1
    })
    .await;
    cx.update(|cx| InspectedCheckout::publish(Some(inspected("s1", 1)), cx));
    cx.run_until_parked();
    let seen = open.seen.lock().unwrap();
    assert_eq!(seen.commits.len(), 1);
    assert_eq!(seen.commits[0]["checkoutPath"], "/linked");
    assert_eq!(
        seen.commits[0]["expectedSource"],
        serde_json::to_value(origin()).unwrap()
    );
    assert!(seen.reads.is_empty());
    drop(seen);
    cx.update(|cx| {
        let pane = open.pane.read(cx);
        assert_eq!(pane.target, Target::Commit(pick));
        assert_eq!(pane.state.read(cx).files().len(), 1);
    });
}

// Pins: scm#17
#[gpui::test]
async fn it_says_what_it_is_waiting_for(cx: &mut TestAppContext) {
    let mut open = open(cx, vec![changed("slow.rs")], Vec::new());
    cx.update(|cx| {
        assert_eq!(
            open.pane.read(cx).waiting(cx),
            Some(Waiting::Quiet(
                "Pick a session with a project folder to see its changes."
            ))
        );
    });
    open.pane.update(cx, |pane, cx| pane.activate(true, cx));
    cx.update(|cx| {
        assert_eq!(
            open.pane.read(cx).waiting(cx),
            Some(Waiting::Busy("Reading the working copy…"))
        );
    });
    open.wait("the file read", cx, |seen, _| seen.reads.len() == 1)
        .await;
    cx.update(|cx| {
        assert_eq!(
            open.pane.read(cx).waiting(cx),
            Some(Waiting::Busy("Reading the changes…"))
        );
    });
}
// Pins: a checkout that's gone goes back to the session's own folder once, even while Source control still names it.
