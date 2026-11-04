//! Source-derived socket fixtures: what the Engine's rows name reaches the section, not native or real Engine proof.
use super::super::super::pr_owner::PrOwner;
use super::{PrThreads, Recorded, ThreadsBody, recorded_on, threads_body};
use crate::core::engine::{EngineItem, EngineKey, SessionKey};
use crate::core::intents::session_open::{SessionOpenRequest, SessionOpenTarget};
use crate::core::socket::{
    client::SocketClient,
    connection_state::ConnectionState,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply},
    row_source::ExpectedRowSource,
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{
    AppContext as _, Context, Entity, IntoElement, Modifiers, ParentElement as _, Render,
    Styled as _, Subscription, Window, div,
};
use serde_json::{Value, json};
use std::{cell::RefCell, rc::Rc};

const OWNER: &str = "actual-owner";

fn project() -> PrOwner {
    PrOwner::Project {
        id: "project".into(),
        source: ExpectedRowSource {
            machine: OWNER.into(),
            generation: 0,
            route_machine: None,
        },
    }
}
fn session_row() -> Value {
    json!({"entityKind":"session","machine":OWNER,"id":"s1","version":1,"title":" Ship it ","provider":"fixture",
        "folder":"/r","state":"idle","activityState":"idle","epoch":1})
}
fn pull_request_row(owner: Value, run: Value) -> Value {
    json!({"entityKind":"pr","machine":OWNER,"id":"pr-1","version":1,"repo":"O/R","number":7,
        "ownerSessionId":owner,"runId":run})
}
fn engine(pull_request: Value, rows: Vec<Value>) -> FakeBackend {
    FakeBackend::start(move |frame| {
        let id = frame["id"].clone();
        match (
            frame["t"].as_str(),
            frame["topic"].as_str(),
            frame["method"].as_str(),
        ) {
            (Some("hello"), ..) => vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"Friendly","engine":OWNER}),
            )],
            (Some("sub"), Some("prs"), _) if frame["stream"] == "engine" => vec![Reply::Frame(
                json!({"t":"engine_snapshot","id":id,"machine":OWNER,"through":1,"items":[pull_request.clone()],"more":false}),
            )],
            (Some("call"), _, Some("entities")) => {
                let asked = &frame["args"]["refs"][0];
                let found: Vec<_> = rows
                    .iter()
                    .filter(|row| row["entityKind"] == asked["kind"] && row["id"] == asked["id"])
                    .cloned()
                    .collect();
                vec![Reply::Frame(
                    json!({"t":"result","id":id,"result":{"items":found}}),
                )]
            }
            _ => vec![],
        }
    })
}
struct Shows {
    threads: Entity<PrThreads>,
    _heard: Subscription,
}
impl Shows {
    fn new(cx: &mut Context<Self>) -> Self {
        let threads = cx.new(|cx| PrThreads::new(&project(), Some("o/r".into()), 7, cx));
        threads.update(cx, |threads, cx| threads.set_active(true, cx));
        let heard = cx.observe(&threads, |_, _, cx| cx.notify());
        Self {
            threads,
            _heard: heard,
        }
    }
}
impl Render for Shows {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .size_full()
            .children(threads_body(self.threads.read(cx).shown(), cx))
    }
}
/// A window showing the section once its Engine's rows have named this session.
async fn showing<'a>(
    backend: &FakeBackend,
    cx: &'a mut TestAppContext,
) -> &'a mut gpui_kit::VisualTestContext {
    cx.executor().allow_parking();
    let (main, bulk) = cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "fixture".into(),
        };
        (
            SocketClient::start(endpoint.clone(), cx),
            SocketClient::start_bulk(endpoint, cx),
        )
    });
    let (changed, mut changes) = unbounded();
    let (tell_main, tell_bulk) = (changed.clone(), changed.clone());
    let _main_heard = cx.update(|cx| {
        cx.observe(&main, move |_, _| {
            let _ = tell_main.unbounded_send(());
        })
    });
    let _bulk_heard = cx.update(|cx| {
        cx.observe(&bulk, move |_, _| {
            let _ = tell_bulk.unbounded_send(());
        })
    });
    until("both connections", &mut changes, &changed, || {
        cx.update(|cx| {
            matches!(main.read(cx).state(), ConnectionState::Connected { .. })
                && matches!(bulk.read(cx).state(), ConnectionState::Connected { .. })
        })
    })
    .await;
    let (shows, visual) = cx.add_window_view(|_, cx| Shows::new(cx));
    let threads = visual.update(|_, cx| shows.read(cx).threads.clone());
    let tell = changed.clone();
    let _shown_heard = visual.update(|_, cx| {
        cx.observe(&threads, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    until("the session row", &mut changes, &changed, || {
        visual.update(|_, cx| {
            *threads.read(cx).shown()
                == ThreadsBody::One {
                    key: SessionKey {
                        machine: OWNER.into(),
                        id: "s1".into(),
                    },
                    title: "Ship it".into(),
                }
        })
    })
    .await;
    visual
}
#[gpui::test]
async fn a_recorded_session_shows_its_title_and_clicking_asks_to_open_it(cx: &mut TestAppContext) {
    let backend = engine(
        pull_request_row(json!("s1"), Value::Null),
        vec![session_row()],
    );
    let visual = showing(&backend, cx).await;
    let asked = Rc::new(RefCell::new(None::<SessionOpenRequest>));
    let heard = asked.clone();
    visual.update(|window, cx| {
        window.activate_window();
        cx.on_action(move |request: &SessionOpenRequest, _| {
            *heard.borrow_mut() = Some(request.clone());
        });
    });
    visual.run_until_parked();
    visual.update(|window, cx| window.draw(cx).clear(cx));
    let row = visual.debug_bounds("pr-thread").expect("the thread's row");
    visual.simulate_click(row.center(), Modifiers::none());
    visual.run_until_parked();
    let request = asked
        .borrow()
        .clone()
        .expect("a request to open the session");
    assert_eq!(
        request.key,
        SessionKey {
            machine: OWNER.into(),
            id: "s1".into()
        }
    );
    assert_eq!(request.target, SessionOpenTarget::Engine);
}
#[gpui::test]
async fn a_run_attached_pull_request_finds_its_session_through_the_run(cx: &mut TestAppContext) {
    let run =
        json!({"entityKind":"run","machine":OWNER,"id":"run-1","version":1,"ownerSessionId":"s1"});
    let backend = engine(
        pull_request_row(Value::Null, json!("run-1")),
        vec![run, session_row()],
    );
    showing(&backend, cx).await;
}
#[test]
fn another_repository_or_number_is_no_thread() {
    let row = |kind: &str, value: Value| EngineItem {
        key: EngineKey {
            machine: OWNER.into(),
            kind: kind.into(),
            id: "row".into(),
        },
        version: 1,
        value,
    };
    let rows = [row("pr", pull_request_row(json!("s1"), Value::Null))];
    assert_eq!(
        recorded_on(&rows, "o/r", 7),
        Some(Recorded::Session("s1".into()))
    );
    assert_eq!(recorded_on(&rows, "o/other", 7), None);
    assert_eq!(recorded_on(&rows, "o/r", 8), None);
    let observation = [row(
        "pr_observation",
        pull_request_row(json!("s1"), Value::Null),
    )];
    assert_eq!(recorded_on(&observation, "o/r", 7), None);
}
#[gpui::test]
fn a_session_with_no_known_source_says_threads_are_unavailable(cx: &mut TestAppContext) {
    let owner = PrOwner::Session {
        id: "s1".into(),
        source: None,
    };
    let threads = cx.new(|cx| {
        let mut threads = PrThreads::new(&owner, Some("o/r".into()), 7, cx);
        threads.set_active(true, cx);
        threads
    });
    cx.update(|cx| assert_eq!(*threads.read(cx).shown(), ThreadsBody::Unavailable));
}
