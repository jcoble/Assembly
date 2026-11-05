//! A window test's engine: the real socket clients and shared feed against a fake backend that serves what the test
//! seeds and records every command. It proves what the window sends and shows, not what an engine does with it.
use std::sync::{Arc, Mutex};

use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::TestAppContext;
use gpui_kit::{Entity, Subscription};
use serde_json::{Value, json};

use super::{EngineFeed, EngineScope, EngineWatch};
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply};
use crate::core::socket::outcome::Outcome;
use crate::core::socket::test_wait::until;

pub const MACHINE: &str = "owner";

#[derive(Default)]
pub struct Seeded {
    /// Pushed in the snapshot of the scope's topic.
    pub items: Vec<Value>,
    /// `workflow/run`'s answer.
    pub run: Value,
    /// The `machines` topic's items, by the name each is kept under.
    pub machines: Vec<Value>,
    pub commands: Vec<Value>,
    /// The next command's refusal; it's applied without one.
    pub refusal: Option<&'static str>,
}

pub struct TestEngine {
    pub state: Arc<Mutex<Seeded>>,
    pub feed: Entity<EngineFeed>,
    pub changed: UnboundedSender<()>,
    pub changes: UnboundedReceiver<()>,
    _backend: FakeBackend,
    _sockets: [Entity<SocketClient>; 2],
    _watching: Vec<Subscription>,
    _watches: Vec<EngineWatch>,
}

fn answer(state: &mut Seeded, frame: &Value) -> Vec<Reply> {
    let id = frame["id"].clone();
    let result = |value: Value| vec![Reply::Frame(json!({"t":"result","id":id,"result":value}))];
    match (
        frame["t"].as_str(),
        frame["feature"].as_str(),
        frame["method"].as_str(),
    ) {
        (Some("hello"), ..) => vec![Reply::Frame(
            json!({"t":"welcome","protocol":1,"machine":"Friendly","engine":MACHINE}),
        )],
        (Some("sub"), ..) if frame["topic"] == "machines" => {
            let items = (state.machines.iter())
                .map(|machine| json!({"key":machine["name"],"item":machine}))
                .collect::<Vec<_>>();
            vec![Reply::Frame(json!({"t":"state","sub":id,"items":items}))]
        }
        (Some("sub"), ..) if frame["stream"] == "engine" => {
            let topic = frame["topic"].as_str().unwrap_or_default();
            let kind = topic.trim_end_matches('s');
            let items = (state.items.iter())
                .filter(|item| {
                    item["entityKind"]
                        .as_str()
                        .is_some_and(|held| held.starts_with(kind))
                })
                .cloned()
                .collect::<Vec<_>>();
            vec![Reply::Frame(
                json!({"t":"engine_snapshot","id":id,"machine":MACHINE,"through":1,"items":items,"more":false}),
            )]
        }
        (Some("call"), Some("engine"), Some("command")) => {
            state.commands.push(frame["args"].clone());
            match state.refusal.take() {
                Some(code) => result(
                    json!({"applied":false,"late":false,"error":code,"version":2,"seq":2,"epoch":3}),
                ),
                None => result(json!({"applied":true,"late":false,"version":2,"seq":2,"epoch":3})),
            }
        }
        (Some("call"), Some("engine"), Some("entities")) => {
            let wanted = frame["args"]["refs"]
                .as_array()
                .cloned()
                .unwrap_or_default();
            let items = (state.items.iter())
                .filter(|item| wanted.iter().any(|reference| reference["id"] == item["id"]))
                .cloned()
                .collect::<Vec<_>>();
            result(json!({"items":items,"next":null}))
        }
        (Some("call"), Some("engine"), Some("usage")) => result(json!({"toolCalls":0})),
        (Some("call"), Some("workflow"), Some("run")) => result(state.run.clone()),
        (Some("call"), Some("fixture"), Some("barrier")) => result(Value::Null),
        _ => vec![],
    }
}

impl TestEngine {
    /// Connects, and watches `topics` until they've loaded.
    pub async fn start(
        seeded: Seeded,
        scopes: &[(&str, Option<&str>)],
        cx: &mut TestAppContext,
    ) -> Self {
        let state = Arc::new(Mutex::new(seeded));
        let (changed, mut changes) = unbounded();
        let (server, tell) = (state.clone(), changed.clone());
        let backend = FakeBackend::start(move |frame| {
            let replies = answer(&mut server.lock().unwrap(), frame);
            let _ = tell.unbounded_send(());
            replies
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
        let observe = |cx: &mut gpui_kit::App| {
            let wake = |changed: &UnboundedSender<()>| {
                let changed = changed.clone();
                move || {
                    let _ = changed.unbounded_send(());
                }
            };
            let (first, second, third) = (wake(&changed), wake(&changed), wake(&changed));
            vec![
                cx.observe(&main, move |_, _| first()),
                cx.observe(&bulk, move |_, _| second()),
                cx.observe(&feed, move |_, _| third()),
            ]
        };
        let watching = cx.update(observe);
        until("both sockets", &mut changes, &changed, || {
            cx.update(|cx| {
                main.read(cx).connection_generation().is_some()
                    && bulk.read(cx).connection_generation().is_some()
            })
        })
        .await;
        let scopes = scopes
            .iter()
            .map(|(topic, key)| EngineScope {
                machine: MACHINE.into(),
                topic: (*topic).into(),
                key: key.map(str::to_owned),
            })
            .collect::<Vec<_>>();
        let watches = cx.update(|cx| {
            feed.update(cx, |feed, cx| {
                scopes
                    .iter()
                    .map(|scope| feed.watch(scope.clone(), cx).unwrap())
                    .collect::<Vec<_>>()
            })
        });
        until("the seeded scopes", &mut changes, &changed, || {
            cx.update(|cx| scopes.iter().all(|scope| feed.read(cx).loaded(scope)))
        })
        .await;
        Self {
            state,
            feed,
            changed,
            changes,
            _backend: backend,
            _sockets: [main, bulk],
            _watching: watching,
            _watches: watches,
        }
    }

    /// Answers once the backend has read everything already sent on the socket commands use.
    pub async fn barrier(&self, cx: &mut gpui_kit::VisualTestContext) {
        let call = cx.update(|_, cx| {
            self._sockets[0]
                .read(cx)
                .call::<Value>("fixture", "barrier", json!({}), None)
        });
        assert!(matches!(call.await, Outcome::Ok(_)));
    }

    pub fn commands(&self) -> Vec<Value> {
        self.state.lock().unwrap().commands.clone()
    }
}
