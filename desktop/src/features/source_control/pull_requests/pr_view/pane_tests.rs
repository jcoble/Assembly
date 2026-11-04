use super::{PrMerged, PrPane};
use crate::core::intents::open_pull_requests::OpenPullRequests;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::palette::palette_view::PaletteView;
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use gpui_kit::component::WindowExt as _;
use gpui_kit::component::dock::DockArea;
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

const PROJECT: &str = "11111111-1111-1111-1111-111111111111";

#[gpui::test]
async fn a_row_with_no_session_reads_through_its_project_and_the_source_it_just_resolved(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        let _ = heard.unbounded_send(());
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        observed.lock().unwrap().push(frame.clone());
        let result = match frame["method"].as_str() {
            Some("list") => json!({"projects":[{
                "id":PROJECT,"machine":"lane","rootPath":"/lane/r","title":"r",
                "repoKey":"github.com/o/r","createdAt":1,
            }],"next":null}),
            Some("resolve_source") => {
                json!({"machine":"engine","generation":3,"routeMachine":"lane"})
            }
            Some("prs") => json!({"items":[],"cursor":null,"total":0}),
            _ => return vec![],
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = || Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint(), cx);
        SocketClient::start_bulk(endpoint(), cx);
    });
    let window = cx.add_empty_window();
    let pane = window.update(|window, cx| cx.new(|cx| PrPane::new(None, window, cx)));
    until("the project row", &mut changes, &changed, || {
        window.run_until_parked();
        window.update(|_, cx| {
            let projects = pane.read(cx).projects.clone();
            projects.is_some_and(|projects| !projects.read(cx).rows().is_empty())
        })
    })
    .await;
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_pr(Some("O/R".into()), 7, cx)));
    until("the project read", &mut changes, &changed, || {
        window.run_until_parked();
        seen.lock()
            .unwrap()
            .iter()
            .any(|frame| frame["method"] == "pr")
    })
    .await;
    let seen = seen.lock().unwrap();
    let methods: Vec<_> = seen.iter().map(|frame| frame["method"].clone()).collect();
    let resolved = methods.iter().position(|method| method == "resolve_source");
    let read = methods.iter().position(|method| method == "pr");
    assert!(resolved < read, "{methods:?}");
    let args = &seen[read.unwrap()]["args"];
    assert_eq!(args["session"], Value::Null);
    assert_eq!(args["projectId"], PROJECT);
    assert_eq!(args["number"], 7);
    assert_eq!(args["expectedSource"]["machine"], "engine");
    assert_eq!(args["expectedSource"]["routeMachine"], "lane");
    assert!(args.get("rootPath").is_none());
}

const LANE: &str = "22222222-2222-2222-2222-222222222222";
const FAR: &str = "33333333-3333-3333-3333-333333333333";
const HERE: &str = "44444444-4444-4444-4444-444444444444";

fn holder(id: &str, machine: &str) -> Value {
    json!({
        "id":id,"machine":machine,"rootPath":format!("/{machine}/r"),"title":"r",
        "repoKey":"github.com/o/r","createdAt":1,
    })
}

struct Fake {
    _backend: FakeBackend,
    seen: Arc<Mutex<Vec<Value>>>,
    changes: futures_channel::mpsc::UnboundedReceiver<()>,
    changed: futures_channel::mpsc::UnboundedSender<()>,
}

fn calls(seen: &Arc<Mutex<Vec<Value>>>, method: &str) -> Vec<Value> {
    let seen = seen.lock().unwrap();
    let of_method = seen.iter().filter(|frame| frame["method"] == method);
    of_method.map(|frame| frame["args"].clone()).collect()
}

/// How the first source question goes.
#[derive(PartialEq)]
enum Source {
    Answers,
    /// Answered only when the next one arrives, and first: a machine answering after the person moved on.
    HoldsFirst,
    /// Refused, as a machine that isn't there.
    RefusesFirst,
}

/// A backend holding these projects, listing one pull request.
fn fake(cx: &mut TestAppContext, projects: Value, first: Source) -> Fake {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, changes) = unbounded();
    let heard = changed.clone();
    let mut held: Option<Value> = None;
    let mut holding = first == Source::HoldsFirst;
    let mut refusing = first == Source::RefusesFirst;
    let backend = FakeBackend::start(move |frame| {
        if frame["t"] == "hello" {
            return vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"box","engine":"box"}),
            )];
        }
        let _ = heard.unbounded_send(());
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        observed.lock().unwrap().push(frame.clone());
        let result = match frame["method"].as_str() {
            Some("list") => json!({"projects":projects.clone(),"next":null}),
            Some("resolve_source") => {
                if std::mem::take(&mut refusing) {
                    return vec![Reply::Frame(json!({
                        "t":"error","id":frame["id"],"code":"bad_args","message":"The machine isn't there.",
                    }))];
                }
                let route = frame["args"]["routeMachine"].clone();
                let source = if route.is_null() {
                    json!({"machine":"box","generation":0,"routeMachine":null})
                } else {
                    json!({"machine":"engine","generation":3,"routeMachine":route})
                };
                let reply = json!({"t":"result","id":frame["id"],"result":source});
                if std::mem::take(&mut holding) {
                    held = Some(reply);
                    return vec![];
                }
                let late = held.take().map(Reply::Frame);
                return late.into_iter().chain([Reply::Frame(reply)]).collect();
            }
            Some("prs") => {
                json!({"items":[{"repository":"o/r","number":7,"title":"Seven"}],"cursor":null,"total":1})
            }
            _ => return vec![],
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = || Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint(), cx);
        SocketClient::start_bulk(endpoint(), cx);
    });
    Fake {
        _backend: backend,
        seen,
        changes,
        changed,
    }
}

async fn pane_with_projects(
    fake: &mut Fake,
    window: &mut gpui::VisualTestContext,
) -> gpui::Entity<PrPane> {
    let pane = window.update(|window, cx| cx.new(|cx| PrPane::new(None, window, cx)));
    until("the project rows", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        window.update(|_, cx| {
            let projects = pane.read(cx).projects.clone();
            projects.is_some_and(|projects| !projects.read(cx).rows().is_empty())
        })
    })
    .await;
    pane
}

/// The window as the app has it: the kit's root over the dock, in front, with the palette and this pane registered.
fn registered_window(
    cx: &mut TestAppContext,
) -> (gpui_kit::Entity<DockArea>, &mut gpui_kit::VisualTestContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        PaletteView::init(cx);
        PrPane::register(cx);
        crate::core::intents::open_url::register(cx);
    });
    let dock = std::rc::Rc::new(std::cell::OnceCell::new());
    let holder = dock.clone();
    let (_, cx) = cx.add_window_view(move |window, cx| {
        let area = cx.new(|cx| DockArea::new("pull-requests", None, window, cx));
        CenterPanes::init(&area, cx);
        let _ = holder.set(area.clone());
        gpui_kit::base::Root::new(area, window, cx)
    });
    cx.simulate_resize(gpui_kit::size(gpui_kit::px(1000.), gpui_kit::px(800.)));
    cx.update(|window, _| window.activate_window());
    settle(cx);
    (dock.get().expect("the window's dock").clone(), cx)
}

fn settle(cx: &mut gpui_kit::VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

#[gpui::test]
fn the_palette_command_opens_the_tab_with_no_session_or_project(cx: &mut TestAppContext) {
    let (_dock, cx) = registered_window(cx);
    cx.simulate_keystrokes("secondary-k");
    settle(cx);
    cx.simulate_input("Pull requests");
    settle(cx);
    cx.simulate_keystrokes("enter");
    settle(cx);
    cx.update(|window, cx| {
        assert!(
            !window.has_active_dialog(cx),
            "choosing the command closes the palette"
        );
        let pane = CenterPanes::find::<PrPane>(super::KEY, cx).expect("the Pull requests tab");
        assert!(pane.read(cx).active, "the tab is in front");
        assert!(
            pane.read(cx).selected.is_none(),
            "it shows the list, not an opened page"
        );
    });
}

#[gpui::test]
fn a_link_clicked_on_the_page_opens_its_address(cx: &mut TestAppContext) {
    let (_dock, cx) = registered_window(cx);
    cx.update(|window, cx| {
        window.dispatch_action(
            Box::new(OpenPullRequests {
                session_id: None,
                repo: None,
                number: None,
            }),
            cx,
        )
    });
    settle(cx);
    let pane = cx.update(|_, cx| CenterPanes::find::<PrPane>(super::KEY, cx).expect("the tab"));
    let url = "https://github.com/o/r/pull/7";
    pane.update(cx, |pane, cx| {
        pane.missing_link = Some(("Not a project.".into(), url.into()));
        cx.notify();
    });
    settle(cx);
    let at = cx
        .debug_bounds("pr-missing-open")
        .expect("the link is drawn");
    cx.simulate_click(at.center(), gpui_kit::Modifiers::none());
    settle(cx);
    assert_eq!(cx.opened_url().as_deref(), Some(url));
}

#[path = "pane_opening_tests.rs"]
mod opening_tests;
#[path = "pane_project_tests.rs"]
mod project_tests;
