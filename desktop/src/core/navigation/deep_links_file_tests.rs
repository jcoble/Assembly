//! Spec section 4: a file link opens through the selected session, or says why it can't. Rests on an open question to
//! Jesse, so it comes last.

use std::cell::RefCell;
use std::rc::Rc;
use std::sync::{Arc, Mutex};

use gpui::TestAppContext;
use gpui_kit::{AppContext as _, EmptyView, Entity, VisualTestContext};
use serde_json::{Value, json};

use super::deep_links::{handle_route, notices};
use super::external_urls::ExternalRoute;
use crate::core::engine::SessionKey;
use crate::core::intents::open_file::OpenFile;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply};
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::selected_session::SelectedSession;

const ROOT: &str = "/work/a";

type Opens = Rc<RefCell<Vec<OpenFile>>>;

/// What the backend does with `files/owner`.
#[derive(Default)]
struct Owner {
    refuse: bool,
    hold: bool,
    held: Option<Value>,
    asked: Vec<Value>,
}

struct Harness<'a> {
    cx: &'a mut VisualTestContext,
    opens: Opens,
    owner: Arc<Mutex<Owner>>,
    client: Option<Entity<SocketClient>>,
    _backend: Option<FakeBackend>,
}

fn harness(cx: &mut TestAppContext) -> Harness<'_> {
    cx.executor().allow_parking();
    let opens = Opens::default();
    let heard = opens.clone();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        cx.on_action(move |intent: &OpenFile, _| heard.borrow_mut().push(intent.clone()));
    });
    let (_, cx) = cx.add_window_view(|window, cx| {
        let content = cx.new(|_| EmptyView);
        gpui_kit::base::Root::new(content, window, cx)
    });
    cx.update(|window, _| window.activate_window());
    cx.run_until_parked();
    Harness {
        cx,
        opens,
        owner: Arc::default(),
        client: None,
        _backend: None,
    }
}

impl Harness<'_> {
    /// Connects to a backend whose `files/owner` answers with `ROOT`, and waits for the connection.
    fn connect(&mut self) {
        let state = self.owner.clone();
        let backend = FakeBackend::start(move |frame| {
            let id = &frame["id"];
            let result =
                |value: Value| Reply::Frame(json!({"t": "result", "id": id, "result": value}));
            let mut state = state.lock().unwrap();
            match (
                frame["t"].as_str(),
                frame["feature"].as_str(),
                frame["method"].as_str(),
            ) {
                (Some("hello"), ..) => vec![Reply::Frame(
                    json!({"t": "welcome", "protocol": 1, "machine": "box", "engine": "owner"}),
                )],
                (Some("call"), Some("files"), Some("owner")) => {
                    state.asked.push(frame["args"].clone());
                    if state.refuse {
                        return vec![Reply::Frame(
                            json!({"t": "error", "id": id, "code": "failed", "message": "The reason."}),
                        )];
                    }
                    let reply = json!({"t": "result", "id": id, "result": {
                        "machine": "owner", "checkoutRoot": ROOT, "remoteSessionId": "r"}});
                    if state.hold {
                        state.held = Some(reply);
                        vec![]
                    } else {
                        vec![Reply::Frame(reply)]
                    }
                }
                (Some("call"), Some("fixture"), Some("release")) => {
                    let held = state.held.take().expect("a held answer");
                    vec![Reply::Frame(held), result(Value::Null)]
                }
                (Some("call"), Some("fixture"), Some("ping")) => vec![result(Value::Null)],
                _ => vec![],
            }
        });
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        self.client = Some(self.cx.update(|_, cx| SocketClient::start(endpoint, cx)));
        self._backend = Some(backend);
    }

    /// One round trip: everything the backend answered before it has reached the app, and its effects have run.
    async fn flush(&mut self, method: &str) {
        let client = self.client.clone().expect("connected");
        self.cx.run_until_parked();
        let call = self.cx.read(|cx| {
            client
                .read(cx)
                .call::<Value>("fixture", method, json!({}), None)
        });
        let _ = call.await;
        self.cx.run_until_parked();
    }

    fn select(&mut self, id: &str, route: Option<&str>) {
        let key = SessionKey {
            machine: "owner".into(),
            id: id.into(),
        };
        let source = ExpectedRowSource {
            machine: "owner".into(),
            generation: u64::from(route.is_some()),
            route_machine: route.map(String::from),
        };
        self.cx.update(|_, cx| {
            SelectedSession::global(cx).update(cx, |selected, cx| {
                selected.commit_open(key, source, false, cx)
            })
        });
    }

    async fn link(&mut self, path: &str, machine: Option<&str>) {
        let route = ExternalRoute::File {
            path: path.into(),
            machine: machine.map(String::from),
        };
        // As the queue does: outside any window update.
        self.cx
            .update(|_, cx| cx.defer(move |cx| handle_route(route, cx)));
        self.cx.run_until_parked();
        if self.client.is_some() {
            self.flush("ping").await;
        }
    }

    fn shown(&mut self) -> Vec<String> {
        self.cx.update(|_, cx| notices(cx))
    }

    fn opened(&self) -> Vec<OpenFile> {
        self.opens.borrow().clone()
    }
}

#[gpui::test]
async fn a_file_inside_the_selected_sessions_folder_opens_by_its_relative_path(
    cx: &mut TestAppContext,
) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", None);
    h.link("/work/a/src/main.rs", None).await;
    let opened = h.opened();
    assert_eq!(opened.len(), 1);
    assert_eq!(opened[0].session_id, "s1");
    assert_eq!(opened[0].path, "src/main.rs");
    assert_eq!(opened[0].line, None);
    assert_eq!(opened[0].column, None);
    assert!(opened[0].pinned);
    assert_eq!(
        opened[0].expected_source,
        Some(ExpectedRowSource {
            machine: "owner".into(),
            generation: 0,
            route_machine: None,
        })
    );
    assert_eq!(h.shown(), Vec::<String>::new());
    let asked = h.owner.lock().unwrap().asked.clone();
    assert_eq!(asked.len(), 1);
    assert_eq!(asked[0]["session"], "s1");
    assert_eq!(asked[0]["expectedSource"]["machine"], "owner");
}

#[gpui::test]
async fn a_remote_file_opens_on_a_session_on_that_machine(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", Some("Box"));
    h.link("/work/a/x.cs", Some("Box")).await;
    let opened = h.opened();
    assert_eq!(opened.len(), 1);
    assert_eq!(opened[0].path, "x.cs");
    assert_eq!(
        opened[0]
            .expected_source
            .as_ref()
            .and_then(|source| source.route_machine.as_deref()),
        Some("Box")
    );
}

#[gpui::test]
async fn no_selected_session_says_to_select_one(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.link("/work/a/x.cs", None).await;
    assert_eq!(h.shown(), ["Select a session to open this file."]);
    assert!(h.opened().is_empty());
}

#[gpui::test]
async fn a_file_on_another_machine_says_which(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", None);
    h.link("/work/a/x.cs", Some("Box")).await;
    assert_eq!(h.shown(), ["That file is on Box."]);
    h.select("s1", Some("Box"));
    h.link("/work/a/x.cs", None).await;
    assert_eq!(
        h.shown().last().map(String::as_str),
        Some("That file is on this Mac.")
    );
    h.link("/work/a/x.cs", Some("Other")).await;
    assert_eq!(
        h.shown().last().map(String::as_str),
        Some("That file is on Other.")
    );
    assert!(h.opened().is_empty());
    assert!(
        h.owner.lock().unwrap().asked.is_empty(),
        "the machine is checked before asking"
    );
}

#[gpui::test]
async fn a_path_outside_the_folder_says_so_comparing_whole_components(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", None);
    for (at, path) in ["/work/ab/x.cs", "/work/x.cs", "/", ROOT]
        .into_iter()
        .enumerate()
    {
        h.link(path, None).await;
        let shown = h.shown();
        assert_eq!(shown.len(), at + 1, "{path}");
        assert_eq!(
            shown[at], "That file isn't in this session's folder.",
            "{path}"
        );
    }
    assert!(h.opened().is_empty());
}

#[gpui::test]
async fn a_refused_folder_question_gives_the_reason(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.owner.lock().unwrap().refuse = true;
    h.select("s1", None);
    h.link("/work/a/x.cs", None).await;
    assert_eq!(h.shown(), ["That file didn't open: The reason."]);
    assert!(h.opened().is_empty());
}

#[gpui::test]
async fn a_changed_selection_while_asking_opens_nothing_and_says_nothing(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.owner.lock().unwrap().hold = true;
    h.select("s1", None);
    h.link("/work/a/x.cs", None).await;
    h.select("s2", None);
    h.flush("release").await;
    h.flush("ping").await;
    assert!(h.opened().is_empty());
    assert_eq!(h.shown(), Vec::<String>::new());
    assert!(h.owner.lock().unwrap().held.is_none());
}

#[gpui::test]
async fn the_selection_does_not_change(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", None);
    h.link("/work/a/x.cs", None).await;
    h.link("/elsewhere/x.cs", None).await;
    let selected = h.cx.update(|_, cx| {
        SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from)
    });
    assert_eq!(selected.as_deref(), Some("s1"));
}

#[gpui::test]
async fn a_file_link_does_not_wait_for_a_connection(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.select("s1", None);
    h.link("/work/a/x.cs", None).await;
    let shown = h.shown();
    assert_eq!(shown.len(), 1);
    assert!(shown[0].starts_with("That file didn't open: "), "{shown:?}");
    assert!(h.opened().is_empty());
}

// From review: Finder hands over a multi-select as several links in one go, and each opens.
#[gpui::test]
async fn several_files_at_once_all_open(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", None);
    let routes: Vec<_> = ["/work/a/one.cs", "/work/a/two.cs", "/work/a/three.cs"]
        .into_iter()
        .map(|path| ExternalRoute::File {
            path: path.into(),
            machine: None,
        })
        .collect();
    h.cx.update(|_, cx| {
        cx.defer(move |cx| {
            for route in routes {
                handle_route(route, cx);
            }
        })
    });
    h.cx.run_until_parked();
    h.flush("ping").await;
    let paths: Vec<_> = h.opened().into_iter().map(|open| open.path).collect();
    assert_eq!(paths, ["one.cs", "two.cs", "three.cs"]);
}

#[gpui::test]
async fn files_open_in_the_order_they_came_whatever_order_the_answers_come(
    cx: &mut TestAppContext,
) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", None);
    h.owner.lock().unwrap().hold = true;
    h.link("/work/a/one.cs", None).await;
    h.owner.lock().unwrap().hold = false;
    h.link("/work/a/two.cs", None).await;
    assert!(h.opened().is_empty(), "two waits for one");
    h.flush("release").await;
    h.flush("ping").await;
    let paths: Vec<_> = h.opened().into_iter().map(|open| open.path).collect();
    assert_eq!(paths, ["one.cs", "two.cs"]);
}

#[gpui::test]
async fn a_changed_selection_drops_every_link_it_catches(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", None);
    h.owner.lock().unwrap().hold = true;
    h.link("/work/a/one.cs", None).await;
    h.owner.lock().unwrap().hold = false;
    h.link("/work/a/two.cs", None).await;
    h.select("s2", None);
    h.link("/work/a/three.cs", None).await;
    h.flush("release").await;
    h.flush("ping").await;
    let opened = h.opened();
    assert_eq!(opened.len(), 1);
    assert_eq!(opened[0].session_id, "s2");
    assert_eq!(opened[0].path, "three.cs");
    assert_eq!(h.shown(), Vec::<String>::new());
}
