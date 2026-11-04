//! The run view's step controls in a real window over the sole EngineFeed and both sockets.
use super::run_view::RunActivity;
use crate::core::{
    engine::{EngineFeed, EngineScope, EngineWatch, SessionKey},
    socket::{
        client::SocketClient,
        endpoint::Endpoint,
        fake_backend::{FakeBackend, Reply},
        test_wait::until,
    },
};
use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::{Entity, Modifiers, Subscription, TestAppContext, VisualTestContext};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

const RETRY: &str = "step-control:step:Retry";
const REFUSAL: &str = "step-refusal:step";

/// What the fake engine holds, and what it does with a command.
struct Engine {
    run: Value,
    step: Value,
    answer: Value,
    commands: Vec<Value>,
    runs_stream: Option<Value>,
}

struct Fixture {
    _backend: FakeBackend,
    _sockets: (Entity<SocketClient>, Entity<SocketClient>),
    _watch: EngineWatch,
    _observers: Vec<Subscription>,
    engine: Arc<Mutex<Engine>>,
    changed: UnboundedSender<()>,
    changes: UnboundedReceiver<()>,
    _view: Entity<RunActivity>,
}

fn run(state: &str) -> Value {
    json!({"entityKind":"run","machine":"owner","id":"run","version":4,"state":state,"ownerEpoch":9,"definitionHash":"hash","activeStepIds":[],"paused":false,"reference":true})
}

fn step(state: &str) -> Value {
    json!({"entityKind":"step","machine":"owner","id":"step","version":2,"runId":"run","name":"check","kind":"agent","state":state,"attempt":1,"maxAttempts":2,"required":1,"ruleSkippable":0,"labelPresent":"Checking","labelPast":"Checked","progress":null,"badges":[],"scopeGranted":false,"closed":false,"dependencies":[],"needsMeIds":[]})
}

/// The engine retries the step as a real one would: it starts running, and the run's stream says the run moved.
fn engine_replies(engine: &Mutex<Engine>, frame: &Value) -> Vec<Reply> {
    let id = frame["id"].clone();
    let result = |value| Reply::Frame(json!({"t":"result","id":id,"result":value}));
    let mut engine = engine.lock().unwrap();
    match (frame["t"].as_str(), frame["method"].as_str()) {
        (Some("hello"), _) => vec![Reply::Frame(
            json!({"t":"welcome","protocol":1,"machine":"Friendly","engine":"owner"}),
        )],
        (Some("sub"), _) if frame["stream"] == "engine" => {
            let items = if frame["topic"] == "runs" {
                engine.runs_stream = Some(id.clone());
                vec![engine.run.clone()]
            } else {
                vec![
                    json!({"entityKind":"session","machine":"owner","id":"parent","version":1,"provider":"fixture","folder":"/fixture","state":"idle","activityState":"idle","epoch":9,"runId":"run"}),
                ]
            };
            vec![Reply::Frame(
                json!({"t":"engine_snapshot","id":id,"machine":"owner","through":3,"items":items,"more":false}),
            )]
        }
        (Some("call"), Some("command")) => {
            engine.commands.push(frame["args"].clone());
            let answer = engine.answer.clone();
            if answer["applied"] != true {
                return vec![result(answer)];
            }
            engine.step["state"] = json!("running");
            engine.step["version"] = json!(3);
            engine.run["version"] = answer["version"].clone();
            let events = json!({"t":"engine_events","id":engine.runs_stream,"machine":"owner","after":3,"through":4,"events":[
                {"machine":"owner","seq":4,"entityKind":"run","entityId":"run","version":answer["version"]}
            ]});
            vec![result(answer), Reply::Frame(events)]
        }
        (Some("call"), Some("entities")) => {
            vec![result(json!({"items":[engine.run.clone()],"next":null}))]
        }
        (Some("call"), Some("run")) => {
            vec![result(
                json!({"run":engine.run,"steps":[engine.step],"next":null}),
            )]
        }
        _ => vec![],
    }
}

async fn view_of(
    run: Value,
    step: Value,
    answer: Value,
    cx: &mut TestAppContext,
) -> (Fixture, &mut VisualTestContext) {
    let engine = Arc::new(Mutex::new(Engine {
        run,
        step,
        answer,
        commands: Vec::new(),
        runs_stream: None,
    }));
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let fake = engine.clone();
    let backend = FakeBackend::start(move |frame| {
        let _ = tell.unbounded_send(());
        engine_replies(&fake, frame)
    });
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    let (main, bulk, feed) = cx.update(|cx| {
        let endpoint = Endpoint {
            port: backend.port,
            token: "fixture".into(),
        };
        (
            SocketClient::start(endpoint.clone(), cx),
            SocketClient::start_bulk(endpoint, cx),
            EngineFeed::global(cx),
        )
    });
    let mut observers = Vec::new();
    cx.update(|cx| {
        for socket in [&main, &bulk] {
            let tell = changed.clone();
            observers.push(cx.observe(socket, move |_, _| {
                let _ = tell.unbounded_send(());
            }));
        }
        let tell = changed.clone();
        observers.push(cx.observe(&feed, move |_, _| {
            let _ = tell.unbounded_send(());
        }));
    });
    until("both sockets", &mut changes, &changed, || {
        cx.update(|cx| {
            main.read(cx).connection_generation().is_some()
                && bulk.read(cx).connection_generation().is_some()
        })
    })
    .await;
    let sessions = EngineScope {
        machine: "owner".into(),
        topic: "sessions".into(),
        key: None,
    };
    let watch =
        cx.update(|cx| feed.update(cx, |feed, cx| feed.watch(sessions.clone(), cx).unwrap()));
    until("the session", &mut changes, &changed, || {
        cx.update(|cx| feed.read(cx).loaded(&sessions))
    })
    .await;
    let parent = SessionKey {
        machine: "owner".into(),
        id: "parent".into(),
    };
    let (view, visual) = cx.add_window_view(|_, cx| RunActivity::new(parent, cx));
    let tell = changed.clone();
    observers.push(visual.update(|_, cx| {
        cx.observe(&view, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    }));
    (
        Fixture {
            _backend: backend,
            _sockets: (main, bulk),
            _watch: watch,
            _observers: observers,
            engine,
            changed,
            changes,
            _view: view,
        },
        visual,
    )
}

fn shown(selector: &'static str, cx: &mut VisualTestContext) -> bool {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.debug_bounds(selector).is_some()
}

async fn wait_shown(
    selector: &'static str,
    wanted: bool,
    fixture: &mut Fixture,
    cx: &mut VisualTestContext,
) {
    let changed = fixture.changed.clone();
    until(selector, &mut fixture.changes, &changed, || {
        shown(selector, cx) == wanted
    })
    .await;
}

fn click(selector: &'static str, cx: &mut VisualTestContext) {
    let at = cx.debug_bounds(selector).unwrap().center();
    cx.simulate_click(at, Modifiers::none());
}

#[gpui::test]
async fn retrying_a_failed_step_sends_the_command_and_the_button_goes_when_the_step_moves(
    cx: &mut TestAppContext,
) {
    let answer = json!({"applied":true,"version":5});
    let (mut fixture, visual) = view_of(run("running"), step("failed"), answer, cx).await;
    wait_shown(RETRY, true, &mut fixture, visual).await;
    click(RETRY, visual);
    wait_shown(RETRY, false, &mut fixture, visual).await;
    assert!(!shown(REFUSAL, visual), "nothing was refused");
    let engine = fixture.engine.lock().unwrap();
    assert_eq!(engine.commands.len(), 1);
    let sent = &engine.commands[0];
    assert_eq!(
        sent["command"],
        json!({"kind":"retry_workflow_step","stepId":"step"})
    );
    assert_eq!(sent["entityId"], "run");
    assert_eq!(sent["expectedVersion"], 4);
    assert_eq!(sent["epoch"], 9);
    assert_eq!(sent["surface"], "rail");
}

#[gpui::test]
async fn a_refusal_shows_under_the_step_and_the_button_can_be_used_again(cx: &mut TestAppContext) {
    let answer = json!({"applied":false,"error":"attempts_exhausted","version":4});
    let (mut fixture, visual) = view_of(run("running"), step("failed"), answer, cx).await;
    wait_shown(RETRY, true, &mut fixture, visual).await;
    click(RETRY, visual);
    wait_shown(REFUSAL, true, &mut fixture, visual).await;
    click(RETRY, visual);
    let (engine, changed) = (fixture.engine.clone(), fixture.changed.clone());
    until("the second try", &mut fixture.changes, &changed, || {
        engine.lock().unwrap().commands.len() == 2
    })
    .await;
    let engine = engine.lock().unwrap();
    assert_ne!(
        engine.commands[0]["commandId"], engine.commands[1]["commandId"],
        "a second press is a new command"
    );
}

#[gpui::test]
async fn a_finished_run_has_no_controls(cx: &mut TestAppContext) {
    let answer = json!({"applied":true,"version":5});
    let (mut fixture, visual) = view_of(run("failed"), step("failed"), answer, cx).await;
    wait_shown("step-row-shown:step", true, &mut fixture, visual).await;
    assert!(!shown(RETRY, visual));
}
