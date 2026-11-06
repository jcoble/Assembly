//! What a need offers, drawn in a real window over the sole EngineFeed: a need nothing here can answer says so at once.
use super::NeedRow;
use crate::core::{
    engine::{EngineFeed, EngineKey, EngineScope, EngineWatch},
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

const NOTICE: &str = "need-no-controls";
const REVIEW: &str = "need-review";
const CONTROLS: &str = "need-answer-controls";
const DISMISS: &str = "need-dismiss";

/// Everything a row needs alive: the backend, both sockets and the feed's watches.
struct Fixture {
    _backend: FakeBackend,
    _sockets: (Entity<SocketClient>, Entity<SocketClient>),
    _watches: (EngineWatch, EngineWatch),
    _observers: Vec<Subscription>,
    commands: Arc<Mutex<Vec<Value>>>,
    changed: UnboundedSender<()>,
    changes: UnboundedReceiver<()>,
    row: Entity<NeedRow>,
}

fn need(extra: Value) -> Value {
    let mut need = json!({"entityKind":"needs_me","id":"question","machine":"owner","version":3,"sessionId":"parent","kind":"question","state":"open","text":"Actual supplied question","displayNumber":1});
    for (key, value) in extra.as_object().unwrap() {
        need[key] = value.clone();
    }
    need
}

async fn row_for(need: Value, cx: &mut TestAppContext) -> (Fixture, &mut VisualTestContext) {
    let commands = Arc::new(Mutex::new(Vec::new()));
    let seen = commands.clone();
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        let _ = tell.unbounded_send(());
        let id = frame["id"].clone();
        let result = |value| Reply::Frame(json!({"t":"result","id":id,"result":value}));
        match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("hello"), _) => vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"Friendly","engine":"owner"}),
            )],
            (Some("sub"), _) if frame["stream"] == "engine" => {
                let items = if frame["topic"] == "needs_me" {
                    vec![need.clone()]
                } else {
                    vec![
                        json!({"entityKind":"session","machine":"owner","id":"parent","version":1,"provider":"fixture","folder":"/fixture","state":"idle","activityState":"idle","epoch":9}),
                    ]
                };
                vec![Reply::Frame(
                    json!({"t":"engine_snapshot","id":id,"machine":"owner","through":3,"items":items,"more":false}),
                )]
            }
            (Some("call"), Some("command")) => {
                seen.lock().unwrap().push(frame["args"].clone());
                vec![result(json!({"applied":true,"version":4}))]
            }
            (Some("call"), Some("entities")) => {
                vec![result(json!({"items":[need.clone()],"next":null}))]
            }
            _ => vec![],
        }
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
    let scope = EngineScope {
        machine: "owner".into(),
        topic: "needs_me".into(),
        key: None,
    };
    let sessions = EngineScope {
        topic: "sessions".into(),
        ..scope.clone()
    };
    let watches = cx.update(|cx| {
        feed.update(cx, |feed, cx| {
            (
                feed.watch(scope.clone(), cx).unwrap(),
                feed.watch(sessions.clone(), cx).unwrap(),
            )
        })
    });
    until("the need and its session", &mut changes, &changed, || {
        cx.update(|cx| feed.read(cx).loaded(&scope) && feed.read(cx).loaded(&sessions))
    })
    .await;
    let key = EngineKey {
        machine: "owner".into(),
        kind: "needs_me".into(),
        id: "question".into(),
    };
    let (row, visual) = cx.add_window_view(|window, cx| {
        let need = feed.read(cx).need(&key).unwrap();
        NeedRow::build(
            key.clone(),
            scope.clone(),
            feed.clone(),
            "rail",
            need,
            window,
            cx,
        )
    });
    (
        Fixture {
            _backend: backend,
            _sockets: (main, bulk),
            _watches: watches,
            _observers: observers,
            commands,
            changed,
            changes,
            row,
        },
        visual,
    )
}

fn shown(selector: &'static str, cx: &mut VisualTestContext) -> bool {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.debug_bounds(selector).is_some()
}

async fn nothing_here_can_answer_it(extra: Value, cx: &mut TestAppContext) {
    let (fixture, visual) = row_for(need(extra), cx).await;
    for stage in ["as it first appears", "once opened"] {
        assert!(shown(NOTICE, visual), "says why {stage}");
        assert!(!shown(REVIEW, visual), "no way into nothing {stage}");
        assert!(!shown(CONTROLS, visual), "no answer controls {stage}");
        assert!(shown(DISMISS, visual), "can still be dismissed {stage}");
        fixture
            .row
            .update_in(visual, |row, window, cx| row.open(window, cx));
    }
    let dismiss = visual.debug_bounds(DISMISS).unwrap().center();
    visual.simulate_click(dismiss, Modifiers::none());
    let commands = fixture.commands.clone();
    let (mut changes, changed) = (fixture.changes, fixture.changed.clone());
    until("the dismissal", &mut changes, &changed, || {
        !commands.lock().unwrap().is_empty()
    })
    .await;
    assert_eq!(
        commands.lock().unwrap()[0]["command"]["state"],
        "dismissed",
        "Dismiss still sends"
    );
}

#[gpui::test]
async fn an_input_mode_with_no_controls_says_so_and_offers_only_dismiss(cx: &mut TestAppContext) {
    nothing_here_can_answer_it(
        json!({"inputMode":"hologram","options":[],"freeText":false}),
        cx,
    )
    .await;
}

#[gpui::test]
async fn a_need_with_no_presentation_says_so_and_offers_only_dismiss(cx: &mut TestAppContext) {
    nothing_here_can_answer_it(json!({"optionsJson":"[\"legacy\"]"}), cx).await;
}

#[gpui::test]
async fn a_need_it_can_answer_shows_no_notice_and_its_controls_once_opened(
    cx: &mut TestAppContext,
) {
    let choice = need(json!({
        "inputMode":"choice",
        "options":[{"id":"actual:a","label":"First actual label","kind":"allow_once"}],
        "freeText":false,
    }));
    let (fixture, visual) = row_for(choice, cx).await;
    assert!(!shown(NOTICE, visual));
    assert!(shown(REVIEW, visual));
    assert!(!shown(CONTROLS, visual));
    fixture
        .row
        .update_in(visual, |row, window, cx| row.open(window, cx));
    assert!(!shown(NOTICE, visual));
    assert!(!shown(REVIEW, visual));
    assert!(shown(CONTROLS, visual));
    assert!(shown(DISMISS, visual));
}
