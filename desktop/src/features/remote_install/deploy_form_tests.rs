use super::super::job::{Ending, Expected, Job};
use super::{DeployForm, LastDeploy};
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use futures_channel::mpsc::{UnboundedSender, unbounded};
use gpui::{Entity, TestAppContext, VisualTestContext};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};
use uuid::Uuid;

const SHA: &str = "abc123";
const COMMIT: &str = "fb6afc15";
const ENGINE: &str = "engine-actual";

type Frames = Arc<Mutex<Vec<Value>>>;

fn job(id: &Value, version: i64, state: &str, result: Value) -> Value {
    json!({"operationId":id,"version":version,"machine":"test","state":state,"phase":"uploading",
        "bytesDone":1,"totalBytes":2,"errorCode":null,"result":result})
}

fn facts_reply(frame: &Value) -> Reply {
    Reply::Frame(json!({"t":"result","id":frame["id"],"result":{
        "sha256":SHA,"bytes":10,"commit":COMMIT,"target":"linux-x64","protocol":1,"format":5,"buildKind":"development"
    }}))
}

fn source_reply(frame: &Value) -> Reply {
    Reply::Frame(json!({"t":"result","id":frame["id"],"result":{
        "machine":ENGINE,"generation":3,"routeMachine":"test"
    }}))
}

/// A backend that answers `inspect` and `resolve_source`, and everything else through `rest`.
fn backend(
    rest: impl FnMut(&Value, &Frames) -> Vec<Reply> + Send + 'static,
) -> (
    FakeBackend,
    Frames,
    UnboundedSender<()>,
    futures_channel::mpsc::UnboundedReceiver<()>,
) {
    let frames: Frames = Default::default();
    let seen = frames.clone();
    let (changed, changes) = unbounded();
    let heard = changed.clone();
    let mut rest = rest;
    let fake = FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        seen.lock().unwrap().push(frame.clone());
        let replies = if frame["method"] == "resolve_source" {
            vec![source_reply(frame)]
        } else if frame["method"] == "inspect" {
            vec![facts_reply(frame)]
        } else if frame["t"] == "sub" && frame["topic"] == "remoteInstall" {
            let mut replies = vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
            replies.extend(rest(frame, &seen));
            replies
        } else if frame["t"] == "sub" {
            vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )]
        } else {
            rest(frame, &seen)
        };
        let _ = heard.unbounded_send(());
        replies
    });
    (fake, frames, changed, changes)
}

fn open<'a>(
    port: u16,
    cx: &'a mut TestAppContext,
) -> (Entity<DeployForm>, &'a mut VisualTestContext) {
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port,
                token: "secret".into(),
            },
            cx,
        );
    });
    cx.add_window_view(|window, cx| DeployForm::new(window, cx))
}

fn call(frames: &Frames, method: &str) -> Option<Value> {
    (frames.lock().unwrap().iter())
        .find(|frame| frame["t"] == "call" && frame["method"] == method)
        .cloned()
}

/// Waits for the connection, picks the machine and inspects a bundle.
async fn prepare(
    form: &Entity<DeployForm>,
    changes: &mut futures_channel::mpsc::UnboundedReceiver<()>,
    changed: &UnboundedSender<()>,
    cx: &mut VisualTestContext,
) {
    until("the connection", changes, changed, || {
        cx.run_until_parked();
        form.read_with(cx, |form, cx| form.current_generation(cx).is_some())
    })
    .await;
    form.update_in(cx, |form, window, cx| {
        form.pick_machine("test".into(), cx);
        form.path.update(cx, |input, cx| {
            input.set_value("/local/bundle.tar.gz", window, cx)
        });
        form.inspect(cx);
    });
    until("the bundle and Engine", changes, changed, || {
        cx.run_until_parked();
        form.read_with(cx, |form, cx| form.ready(cx).is_some())
    })
    .await;
}

// Pins: TSK-1649 temporary remote install control
#[gpui::test]
async fn a_deploy_subscribes_first_sends_the_actual_engine_and_hash_and_ignores_a_stale_push(
    cx: &mut TestAppContext,
) {
    let (fake, frames, changed, mut changes) = backend(|frame, seen| {
        if frame["method"] != "start" {
            return vec![];
        }
        let seen = seen.lock().unwrap();
        let sub = seen
            .iter()
            .find(|item| item["t"] == "sub" && item["topic"] == "remoteInstall")
            .expect("subscribed before start");
        let id = frame["args"]["operationId"].clone();
        let done = json!({"commit":COMMIT,"machine":"test","engine":ENGINE});
        vec![
            Reply::Frame(
                json!({"t":"result","id":frame["id"],"result":job(&id,1,"accepted",Value::Null)}),
            ),
            Reply::Frame(
                json!({"t":"change","sub":sub["id"],"key":sub["key"],"item":job(&id,3,"succeeded",done)}),
            ),
            Reply::Frame(
                json!({"t":"change","sub":sub["id"],"key":sub["key"],"item":job(&id,2,"running",Value::Null)}),
            ),
        ]
    });
    let (form, cx) = open(fake.port, cx);
    prepare(&form, &mut changes, &changed, cx).await;
    form.update(cx, |form, cx| form.deploy(cx));
    until("the settled job", &mut changes, &changed, || {
        cx.run_until_parked();
        form.read_with(cx, |form, _| form.job.as_ref().is_some_and(Job::settled))
    })
    .await;
    let start = call(&frames, "start").unwrap();
    assert_eq!(start["feature"], "remoteInstall");
    assert_eq!(start["args"]["machine"], "test");
    assert_eq!(start["args"]["expectedEngine"], ENGINE);
    assert_eq!(start["args"]["bundleFile"], "/local/bundle.tar.gz");
    assert_eq!(start["args"]["sha256"], SHA);
    assert!(Uuid::parse_str(start["args"]["operationId"].as_str().unwrap()).is_ok());
    form.read_with(cx, |form, _| {
        let job = form.job.as_ref().unwrap();
        assert_eq!(job.version, 3);
        assert!(matches!(
            job.ending(form.expected.as_ref()),
            Some(Ending::Confirmed(_))
        ));
    });
}

// Pins: TSK-1649 temporary remote install control
#[gpui::test]
async fn a_refused_start_leaves_the_form_free_and_says_why(cx: &mut TestAppContext) {
    let (fake, _frames, changed, mut changes) = backend(|frame, _| {
        if frame["method"] != "start" {
            return vec![];
        }
        vec![Reply::Frame(
            json!({"t":"error","id":frame["id"],"code":"install_busy","message":"raw"}),
        )]
    });
    let (form, cx) = open(fake.port, cx);
    prepare(&form, &mut changes, &changed, cx).await;
    form.update(cx, |form, cx| form.deploy(cx));
    until("the refusal", &mut changes, &changed, || {
        cx.run_until_parked();
        form.read_with(cx, |form, _| form.start_error.is_some())
    })
    .await;
    form.read_with(cx, |form, _| {
        assert_eq!(
            form.start_error.as_deref(),
            Some("An install is already running on this machine.")
        );
        assert!(form.expected.is_none());
        assert!(!form.occupied());
    });
}

// Pins: TSK-1649 temporary remote install control
#[gpui::test]
async fn a_reopened_dialog_with_no_job_on_the_backend_says_the_outcome_is_unknown(
    cx: &mut TestAppContext,
) {
    let (fake, _frames, changed, mut changes) = backend(|frame, _| {
        if frame["method"] != "status" {
            return vec![];
        }
        vec![Reply::Frame(
            json!({"t":"error","id":frame["id"],"code":"operation_unknown","message":"unknown"}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: fake.port,
                token: "secret".into(),
            },
            cx,
        );
        cx.set_global(LastDeploy(Some(Expected {
            operation_id: Uuid::new_v4().to_string(),
            machine: "test".into(),
            engine: ENGINE.into(),
            commit: COMMIT.into(),
        })));
    });
    let (form, cx) = cx.add_window_view(|window, cx| DeployForm::new(window, cx));
    until("the unknown outcome", &mut changes, &changed, || {
        cx.run_until_parked();
        form.read_with(cx, |form, _| form.unknown.is_some())
    })
    .await;
    form.read_with(cx, |form, _| {
        assert!(form.job.is_none());
        assert!(form.occupied());
        assert!(
            form.unknown
                .as_deref()
                .unwrap()
                .contains("no longer has a record")
        );
    });
    form.update(cx, |form, cx| form.forget(cx));
    form.read_with(cx, |form, _| assert!(!form.occupied()));
}

// Pins: TSK-1649 temporary remote install control
#[gpui::test]
async fn editing_the_path_clears_what_was_inspected(cx: &mut TestAppContext) {
    let (fake, _frames, changed, mut changes) = backend(|_, _| vec![]);
    let (form, cx) = open(fake.port, cx);
    prepare(&form, &mut changes, &changed, cx).await;
    form.update_in(cx, |form, window, cx| {
        form.path.update(cx, |input, cx| {
            input.set_value("/local/other.tar.gz", window, cx)
        });
    });
    cx.run_until_parked();
    // A deploy refuses a path that isn't the one inspected, whichever way it changed.
    form.read_with(cx, |form, cx| assert!(form.ready(cx).is_none()));
    form.update(cx, |form, cx| form.path_changed(cx));
    form.read_with(cx, |form, _| assert!(form.facts.is_none()));
}
