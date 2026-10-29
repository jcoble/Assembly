//! When the Changes tab reads `git/diff` and `git/branchDiff`, on the socket client against `FakeBackend` (scm#13,
//! scm#17, scm#18).

use std::sync::{Arc, Mutex};

use futures_channel::mpsc::{UnboundedSender, unbounded};
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use serde_json::{Value, json};

use super::{ChangesPane, Picked, Shown};
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::features::changes::file_diff::Row;

/// What the backend saw: each `git/diff` call's path and id, and each cancel's id.
#[derive(Default)]
struct Seen {
    reads: Vec<(String, Value)>,
    cancels: Vec<Value>,
}

/// Answers a one-line edit for any path but `slow.rs`, which it never answers.
fn backend(seen: Arc<Mutex<Seen>>, heard: UnboundedSender<()>) -> FakeBackend {
    FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let id = frame["id"].clone();
        let mut seen = seen.lock().unwrap();
        let replies = match frame["t"].as_str() {
            Some("call") if frame["method"] == "diff" => {
                let path = frame["args"]["path"]
                    .as_str()
                    .unwrap_or_default()
                    .to_string();
                seen.reads.push((path.clone(), id.clone()));
                if path == "slow.rs" {
                    Vec::new()
                } else {
                    vec![Reply::Frame(json!({"t": "result", "id": id, "result": {
                        "path": path, "staged": "", "unstaged": "@@ -1 +1 @@\n-a\n+b\n",
                        "binary": false, "truncated": false }}))]
                }
            }
            Some("call") if frame["method"] == "branchDiff" => {
                seen.reads.push(("branch".into(), id.clone()));
                vec![Reply::Frame(json!({"t": "result", "id": id, "result": {
                    "base": "origin/main", "truncated": false,
                    "diff": "diff --git a/src/a.rs b/src/a.rs\n@@ -1 +1 @@\n-a\n+b\n" }}))]
            }
            Some("cancel") => {
                seen.cancels.push(id);
                Vec::new()
            }
            _ => Vec::new(),
        };
        drop(seen);
        _ = heard.unbounded_send(());
        replies
    })
}

fn picked(path: &str) -> Picked {
    Picked::File {
        session: "s1".into(),
        path: path.into(),
        original_path: None,
    }
}

// Pins: scm#13, scm#17
#[gpui::test]
async fn picking_another_file_shows_it_and_stops_the_read_on_its_way(cx: &mut TestAppContext) {
    let seen = Arc::new(Mutex::new(Seen::default()));
    let (changed, mut changes) = unbounded();
    let backend = backend(seen.clone(), changed.clone());
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
    let pane = cx.new(|cx| ChangesPane::new(Some(picked("slow.rs")), cx));
    let tell = changed.clone();
    let _heard = cx.update(|cx| cx.observe(&pane, move |_, _| _ = tell.unbounded_send(())));
    until("slow read sent", &mut changes, &changed, || {
        cx.run_until_parked();
        !seen.lock().unwrap().reads.is_empty()
    })
    .await;

    pane.update(cx, |pane, cx| pane.show(picked("fast.rs"), cx));
    until("fast read shown", &mut changes, &changed, || {
        cx.run_until_parked();
        let shown = cx.update(|cx| {
            let pane = pane.read(cx);
            pane.picked == Some(picked("fast.rs")) && matches!(pane.shown, Shown::Read { .. })
        });
        shown && !seen.lock().unwrap().cancels.is_empty()
    })
    .await;
    let seen = seen.lock().unwrap();
    assert_eq!(seen.cancels, vec![seen.reads[0].1.clone()]);
    drop(backend);
}

// Pins: scm#18
#[gpui::test]
async fn opening_the_branch_diff_shows_each_file_under_its_base_and_stops_the_file_read(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Seen::default()));
    let (changed, mut changes) = unbounded();
    let backend = backend(seen.clone(), changed.clone());
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
    let pane = cx.new(|cx| ChangesPane::new(Some(picked("slow.rs")), cx));
    let tell = changed.clone();
    let _heard = cx.update(|cx| cx.observe(&pane, move |_, _| _ = tell.unbounded_send(())));
    until("slow read sent", &mut changes, &changed, || {
        cx.run_until_parked();
        !seen.lock().unwrap().reads.is_empty()
    })
    .await;

    let branch = Picked::Branch {
        session: "s1".into(),
    };
    pane.update(cx, |pane, cx| pane.show(branch, cx));
    until("branch diff shown", &mut changes, &changed, || {
        cx.run_until_parked();
        let shown = cx.update(|cx| {
            matches!(&pane.read(cx).shown, Shown::Read { base: Some(base), rows, .. }
                if base == "origin/main" && rows[0] == Row::Label("src/a.rs".into()))
        });
        shown && !seen.lock().unwrap().cancels.is_empty()
    })
    .await;
    let seen = seen.lock().unwrap();
    assert_eq!(seen.cancels, vec![seen.reads[0].1.clone()]);
    drop(backend);
}
