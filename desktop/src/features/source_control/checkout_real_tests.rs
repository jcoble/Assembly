//! The checkout commands as the pane words them, against the real backend: a fake one takes any shape of command.

use std::process::Command;

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use serde_json::{Value, json};

use super::checkout::{branch_wire, folder_wire};
use crate::core::engine::commands::WindowCommand;
use crate::core::engine::{EngineFeed, EngineKey, EngineScope, SessionKey};
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::real_backend_cancel_tests::{add_project, backend_with_stub, stub_session};
use crate::core::socket::test_wait::until;
use crate::core::state::selected_session::SelectedSession;

fn git(repo: &str, args: &[&str]) -> String {
    let out = Command::new("git").args(["-C", repo]).args(args).output().expect("git");
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
    String::from_utf8_lossy(&out.stdout).trim().to_string()
}

#[gpui::test]
async fn the_backend_takes_each_checkout_command_the_pane_sends(cx: &mut TestAppContext) {
    let (backend, repo) = backend_with_stub().expect("the desktop backend is not built");
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    let (changed, mut changes) = unbounded();
    let (tell_main, tell_bulk, tell_feed) = (changed.clone(), changed.clone(), changed.clone());
    let (main, bulk, _heard) = cx.update(|cx| {
        SelectedSession::init(cx);
        let main = SocketClient::start(backend.endpoint(), cx);
        let bulk = SocketClient::start_bulk(backend.endpoint(), cx);
        let heard = [
            cx.observe(&main, move |_, _| _ = tell_main.unbounded_send(())),
            cx.observe(&bulk, move |_, _| _ = tell_bulk.unbounded_send(())),
        ];
        (main, bulk, heard)
    });
    until("both sockets", &mut changes, &changed, || {
        cx.read(|cx| {
            [&main, &bulk]
                .iter()
                .all(|socket| matches!(socket.read(cx).state(), ConnectionState::Connected { .. }))
        })
    })
    .await;
    let Outcome::Ok(added) = cx.read(|cx| main.read(cx).call::<Value>("projects", "add", add_project(&repo), None)).await
    else {
        panic!("the project was not added");
    };
    let mut create = stub_session(&added);
    create["checkout"] = json!({"kind": "current"});
    let Outcome::Ok(made) = cx.read(|cx| main.read(cx).call::<Value>("sessions", "create", create, None)).await else {
        panic!("the session was not made");
    };
    let id = made["sessionId"].as_str().expect("its id").to_string();
    let machine = cx.read(|cx| main.read(cx).engine_machine()).expect("the owning Engine");
    let feed = cx.update(EngineFeed::global);
    let _feed = cx.update(|cx| cx.observe(&feed, move |_, _| _ = tell_feed.unbounded_send(())));
    let scope = EngineScope { machine: machine.clone(), topic: "sessions".into(), key: None };
    let _watch = cx.update(|cx| feed.update(cx, |feed, cx| feed.watch(scope, cx).expect("the watch")));
    let key = SessionKey { machine: machine.clone(), id: id.clone() };
    let item = EngineKey { machine: machine.clone(), kind: "session".into(), id: id.clone() };
    let row = |cx: &TestAppContext| {
        cx.read(|cx| feed.read(cx).item(&item).map(|item| item.value.clone()).unwrap_or_default())
    };
    // It takes another version on the way to idle, and a command held against an older one is refused.
    until("the session to settle", &mut changes, &changed, || {
        let row = row(cx);
        row["checkoutState"] == "ready" && row["state"] == "idle"
    })
    .await;

    let head = git(&repo, &["rev-parse", "HEAD"]);
    git(&repo, &["branch", "other"]);
    let linked = format!("{repo}-linked");
    git(&repo, &["worktree", "add", "--quiet", &linked, "-b", "linked"]);
    let cases = [
        (branch_wire("other", false, &head), "ready", "other"),
        (branch_wire("made-here", true, &head), "ready", "made-here"),
        // The stub can't resume, so the backend reads this one and then refuses it on its merits.
        (folder_wire(&linked), "refused", "made-here"),
    ];
    for (wire, outcome, branch) in cases {
        let operation = uuid::Uuid::new_v4().to_string();
        let (expected_version, epoch) = cx.read(|cx| {
            let session = feed.read(cx).session(&key).expect("the session");
            (session.version, session.epoch)
        });
        let command = WindowCommand {
            command_id: operation.clone(),
            entity_id: id.clone(),
            expected_version,
            epoch,
            machine: Some(machine.clone()),
            surface: None,
            command: wire,
        };
        let receipt = cx.update(|cx| feed.update(cx, |feed, cx| feed.submit(command, cx))).await;
        let receipt = receipt.unwrap_or_else(|error| panic!("{}: {}", error.code, error.message));
        assert!(receipt.applied, "{:?}", receipt.error);
        until("the checkout's outcome", &mut changes, &changed, || {
            let row = row(cx);
            row["checkoutOperationId"] == operation.as_str() && row["checkoutState"] == outcome
        })
        .await;
        assert_eq!(git(&repo, &["rev-parse", "--abbrev-ref", "HEAD"]), branch);
    }
    assert_eq!(row(cx)["checkoutReason"], "unsupported_resume");
}
