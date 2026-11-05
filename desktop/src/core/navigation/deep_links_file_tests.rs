//! Deep links spec section 4, as Feature 28 (opening any file) changed it: a file link opens through the one resolver,
//! as a session file when it's inside the selected session's checkout and by machine and path otherwise. The harness
//! here is shared with `file_link_resolve_tests`.

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

/// The selected session's checkout on the fake backend; its owner answers `remoteSessionId` "r".
pub(super) const ROOT: &str = "/work/a";
/// The fake backend's home folder, for `~` links.
pub(super) const HOME: &str = "/home/me";

type Opens = Rc<RefCell<Vec<OpenFile>>>;

/// What the backend does with `files/owner`.
#[derive(Default)]
pub(super) struct Owner {
    /// A path target's refusal: its code and message.
    pub(super) refuse: Option<(&'static str, &'static str)>,
    /// The session's owner call fails, as for a session with no folder yet.
    pub(super) refuse_session: bool,
    /// Holds the next `files/owner` answer until `fixture/release`.
    pub(super) hold: bool,
    pub(super) held: Option<Value>,
    pub(super) asked: Vec<Value>,
}

pub(super) struct Harness<'a> {
    pub(super) cx: &'a mut VisualTestContext,
    opens: Opens,
    pub(super) owner: Arc<Mutex<Owner>>,
    client: Option<Entity<SocketClient>>,
    _backend: Option<FakeBackend>,
}

/// The fake backend's answer to a path target: inside `ROOT` when `insideOf` is its session, else outside.
fn target_answer(target: &Value) -> Value {
    let path = target["path"].as_str().unwrap_or_default();
    let real = match path.strip_prefix("~/") {
        Some(rest) => format!("{HOME}/{rest}"),
        None => path.to_string(),
    };
    let inside = (target["insideOf"] == "r")
        .then(|| real.strip_prefix(&format!("{ROOT}/")).map(String::from))
        .flatten();
    if let Some(file) = inside {
        return json!({"machine": "owner", "checkoutRoot": ROOT, "remoteSessionId": "r", "file": file});
    }
    let (parent, name) = real.rsplit_once('/').unwrap_or(("", &real));
    let parent = if parent.is_empty() { "/" } else { parent };
    json!({"machine": "owner", "checkoutRoot": parent, "remoteSessionId": "", "file": name})
}

pub(super) fn harness(cx: &mut TestAppContext) -> Harness<'_> {
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
    /// Connects to a backend whose `files/owner` answers a session with `ROOT` and a path target with `target_answer`.
    pub(super) fn connect(&mut self) {
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
                    let args = frame["args"].clone();
                    state.asked.push(args.clone());
                    let by_target = args.get("target").is_some();
                    let refusal = match (by_target, state.refuse) {
                        (true, Some(refusal)) => Some(refusal),
                        (false, _) if state.refuse_session => Some(("failed", "No folder yet.")),
                        _ => None,
                    };
                    if let Some((code, message)) = refusal {
                        return vec![Reply::Frame(
                            json!({"t": "error", "id": id, "code": code, "message": message}),
                        )];
                    }
                    let answer = if by_target {
                        target_answer(&args["target"])
                    } else {
                        json!({"machine": "owner", "checkoutRoot": ROOT, "remoteSessionId": "r"})
                    };
                    let reply = json!({"t": "result", "id": id, "result": answer});
                    if state.hold {
                        state.hold = false;
                        state.held = Some(reply);
                        vec![]
                    } else {
                        vec![Reply::Frame(reply)]
                    }
                }
                // A session's machine as `sessions/resolve_source` answers it: this machine's engine, or "box-engine".
                (Some("call"), Some("sessions"), Some("resolve_source")) => {
                    let route = frame["args"]["routeMachine"].clone();
                    let source = match route.is_null() {
                        true => json!({"machine": "owner", "generation": 0, "routeMachine": null}),
                        false => {
                            json!({"machine": "box-engine", "generation": 2, "routeMachine": route})
                        }
                    };
                    vec![result(source)]
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
    pub(super) async fn flush(&mut self, method: &str) {
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

    pub(super) fn select(&mut self, id: &str, route: Option<&str>) {
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

    pub(super) async fn link(&mut self, path: &str, machine: Option<&str>) {
        let route = ExternalRoute::File {
            path: path.into(),
            machine: machine.map(String::from),
        };
        // As the queue does: outside any window update.
        self.cx
            .update(|_, cx| cx.defer(move |cx| handle_route(route, cx)));
        self.settle().await;
    }

    /// Three round trips: a link's session source and session questions, then its path question, have been answered
    /// and their effects run.
    pub(super) async fn settle(&mut self) {
        self.cx.run_until_parked();
        if self.client.is_some() {
            self.flush("ping").await;
            self.flush("ping").await;
            self.flush("ping").await;
        }
    }

    pub(super) fn shown(&mut self) -> Vec<String> {
        self.cx.update(|_, cx| notices(cx))
    }

    pub(super) fn opened(&self) -> Vec<OpenFile> {
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
    assert_eq!(opened[0].outside, None);
    assert_eq!(h.shown(), Vec::<String>::new());
    let asked = h.owner.lock().unwrap().asked.clone();
    assert_eq!(asked.len(), 2, "the session's folder, then the path");
    assert_eq!(asked[0]["session"], "s1");
    assert_eq!(asked[0]["expectedSource"]["machine"], "owner");
    assert_eq!(
        asked[1]["target"],
        json!({"path": "/work/a/src/main.rs", "machine": null, "insideOf": "r"})
    );
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

// Restored from review: a relative link is only the session's when it's for the session's machine.
#[gpui::test]
async fn a_file_on_another_machine_says_which(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", None);
    h.link("x.cs", Some("Box")).await;
    assert_eq!(h.shown(), ["That file is on Box."]);
    h.select("s1", Some("Box"));
    h.link("x.cs", None).await;
    assert_eq!(
        h.shown().last().map(String::as_str),
        Some(
            format!(
                "That file is on {}.",
                super::file_link_resolve_tests::this_computer()
            )
            .as_str()
        )
    );
    h.link("x.cs", Some("Other")).await;
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
async fn a_path_outside_the_folder_opens_by_machine_and_path_comparing_whole_components(
    cx: &mut TestAppContext,
) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", None);
    for path in ["/work/ab/x.cs", "/work/x.cs"] {
        h.link(path, None).await;
    }
    let opened: Vec<_> = h
        .opened()
        .into_iter()
        .map(|open| (open.session_id, open.outside.map(|outside| outside.path)))
        .collect();
    assert_eq!(
        opened,
        [
            (String::new(), Some("/work/ab/x.cs".to_string())),
            (String::new(), Some("/work/x.cs".to_string())),
        ]
    );
    assert_eq!(h.shown(), Vec::<String>::new());
}

#[gpui::test]
async fn a_refused_folder_question_gives_the_reason(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.owner.lock().unwrap().refuse = Some(("failed", "The reason."));
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
    h.settle().await;
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

#[gpui::test]
async fn the_same_session_on_a_new_source_while_asking_says_so(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.owner.lock().unwrap().hold = true;
    h.select("s1", None);
    h.link("/work/a/x.cs", None).await;
    h.select("s1", Some("Box"));
    h.flush("release").await;
    h.flush("ping").await;
    assert!(h.opened().is_empty());
    assert_eq!(
        h.shown(),
        ["That file didn't open: The session's source changed."]
    );
}

// Follow-up, finding 4: a file call the backend never answers ends with a notice, and the links after it still open.
#[gpui::test]
async fn an_unanswered_file_call_ends_with_a_notice_and_the_next_link_opens(
    cx: &mut TestAppContext,
) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", None);
    h.owner.lock().unwrap().hold = true;
    h.link("/work/a/one.cs", None).await;
    h.owner.lock().unwrap().hold = false;
    h.link("/work/a/two.cs", None).await;
    assert!(h.opened().is_empty(), "two still waits for one");
    h.cx.executor()
        .advance_clock(std::time::Duration::from_secs(31));
    h.flush("ping").await;
    let paths: Vec<_> = h.opened().into_iter().map(|open| open.path).collect();
    assert_eq!(paths, ["two.cs"]);
    assert_eq!(
        h.shown(),
        ["That file didn't open: The backend didn't answer within 30 seconds."]
    );
}

#[gpui::test]
async fn a_link_for_a_newly_selected_session_never_waits_behind_the_old_one(
    cx: &mut TestAppContext,
) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", None);
    h.owner.lock().unwrap().hold = true;
    h.link("/work/a/one.cs", None).await;
    h.owner.lock().unwrap().hold = false;
    h.select("s2", None);
    h.link("/work/a/two.cs", None).await;
    let opened = h.opened();
    assert_eq!(opened.len(), 1, "two opens without waiting for s1's call");
    assert_eq!(opened[0].session_id, "s2");
    assert!(
        h.shown().is_empty(),
        "s1's link ends quietly: the person moved on"
    );
}
