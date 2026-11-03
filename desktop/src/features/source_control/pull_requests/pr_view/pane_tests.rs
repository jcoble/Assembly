use super::PrPane;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
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
    window.update(|_, cx| {
        pane.update(cx, |pane, cx| pane.select_pr(Some("O/R".into()), 7, cx))
    });
    until("the project read", &mut changes, &changed, || {
        window.run_until_parked();
        seen.lock().unwrap().iter().any(|frame| frame["method"] == "pr")
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

/// A backend holding these projects. With `hold_first`, the first source question is answered only when the next one
/// arrives, and first: a machine answering after the person moved on.
fn fake(cx: &mut TestAppContext, projects: Value, hold_first: bool) -> Fake {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, changes) = unbounded();
    let heard = changed.clone();
    let mut held: Option<Value> = None;
    let mut holding = hold_first;
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
    Fake { _backend: backend, seen, changes, changed }
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

#[gpui::test]
async fn a_repository_on_two_machines_waits_for_the_person_to_say_which(cx: &mut TestAppContext) {
    let mut fake = fake(cx, json!([holder(LANE, "lane"), holder(FAR, "far")]), false);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_pr(Some("o/r".into()), 7, cx)));
    window.run_until_parked();
    window.update(|_, cx| {
        let pane = pane.read(cx);
        let choice = pane.choosing.as_ref().expect("the question");
        assert_eq!(choice.projects.len(), 2);
        assert!(pane.opening.is_none() && pane.detail.is_none());
    });
    assert!(calls(&fake.seen, "resolve_source").is_empty());
    assert!(calls(&fake.seen, "pr").is_empty());

    window.update(|_, cx| pane.update(cx, |pane, cx| pane.choose_project(FAR, cx)));
    window.update(|_, cx| assert!(pane.read(cx).choosing.is_none()));
    until("the chosen read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    let read = calls(&fake.seen, "pr").remove(0);
    assert_eq!(read["projectId"], FAR);
    assert_eq!(read["expectedSource"]["routeMachine"], "far");
}

#[gpui::test]
async fn cancelling_forgets_the_question_and_a_late_answer_opens_nothing(cx: &mut TestAppContext) {
    let mut fake = fake(cx, json!([holder(LANE, "lane"), holder(FAR, "far")]), true);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| {
        pane.update(cx, |pane, cx| {
            pane.select_project(Some(LANE.into()), cx);
            pane.select_pr(Some("o/r".into()), 7, cx);
        })
    });
    window.update(|_, cx| assert!(pane.read(cx).opening.is_some()));
    until("the first question", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "resolve_source").is_empty()
    })
    .await;
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.cancel_open(cx)));
    window.update(|_, cx| assert!(pane.read(cx).opening.is_none()));

    window.update(|_, cx| {
        pane.update(cx, |pane, cx| {
            pane.select_project(Some(FAR.into()), cx);
            pane.select_pr(Some("o/r".into()), 8, cx);
        })
    });
    until("the second read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    let reads = calls(&fake.seen, "pr");
    assert_eq!(reads.len(), 1, "{reads:?}");
    assert_eq!(reads[0]["number"], 8);
    assert_eq!(reads[0]["projectId"], FAR);
}

#[gpui::test]
async fn a_row_under_all_local_projects_opens_this_computers_copy_without_asking(
    cx: &mut TestAppContext,
) {
    let mut fake = fake(cx, json!([holder(LANE, "lane"), holder(HERE, "local")]), false);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    let row = serde_json::from_value(json!({"repository":"o/r","number":7,"title":"The row"})).unwrap();
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_row(row, cx)));
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert_eq!(pane.selected.as_ref().and_then(|wanted| wanted.row.as_ref()).unwrap().title, "The row");
    });
    window.update(|_, cx| assert!(pane.read(cx).choosing.is_none()));
    until("the local read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert!(pane.selected.is_none());
        let detail = pane.detail.as_ref().expect("the page").read(cx);
        assert_eq!(detail.row().unwrap().title, "The row");
    });
    let read = calls(&fake.seen, "pr").remove(0);
    assert_eq!(read["projectId"], HERE);
    assert_eq!(read["expectedSource"]["machine"], "box");
    assert_eq!(read["expectedSource"]["routeMachine"], Value::Null);
}

#[gpui::test]
async fn the_all_list_searches_local_projects_and_a_picked_remote_searches_only_itself(
    cx: &mut TestAppContext,
) {
    let far = json!({
        "id":FAR,"machine":"far","rootPath":"/far/x","title":"x",
        "repoKey":"github.com/o/far","createdAt":1,
    });
    let mut fake = fake(cx, json!([holder(HERE, "local"), far]), false);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.sync_scope(cx)));
    window.update(|_, cx| assert_eq!(pane.read(cx).scope, vec!["o/r".to_string()]));
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_project(Some(FAR.into()), cx)));
    window.update(|_, cx| assert_eq!(pane.read(cx).scope, vec!["o/far".to_string()]));
}

#[gpui::test]
async fn moving_along_a_stack_stays_with_the_project_already_answering(cx: &mut TestAppContext) {
    let mut fake = fake(cx, json!([holder(LANE, "lane"), holder(FAR, "far")]), false);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| {
        pane.update(cx, |pane, cx| {
            pane.select_pr(Some("o/r".into()), 7, cx);
            pane.choose_project(LANE, cx);
        })
    });
    until("the first read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    let shown: crate::features::source_control::pull_requests::pull_request::PullRequestDetail =
        serde_json::from_value(json!({"repository":"o/r","localRoot":"/lane/r","number":7,"title":"PR","body":"","bodyBlocks":[],"url":"https://github.com/o/r/pull/7","author":"author","headBranch":"feature","baseBranch":"main","headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"","createdAt":"","mergedAt":"","mergedBy":"","closedAt":"","commits":[],"comments":[],"reviews":[],"reviewers":[],"checks":[],"files":[],"moreFiles":false,"timeline":[]})).unwrap();
    window.update(|_, cx| {
        let detail = pane.read(cx).detail.clone().expect("the page");
        detail.update(cx, |detail, _| detail.detail = Some(shown));
        pane.update(cx, |pane, cx| pane.select_pr(Some("O/R".into()), 8, cx));
        assert!(pane.read(cx).choosing.is_none());
    });
    until("the next read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        calls(&fake.seen, "pr").len() == 2
    })
    .await;
    let reads = calls(&fake.seen, "pr");
    assert_eq!(reads[1]["number"], 8);
    assert_eq!(reads[1]["projectId"], LANE);
    assert_eq!(calls(&fake.seen, "resolve_source").len(), 1);
}

#[gpui::test]
async fn going_back_from_a_page_still_opening_returns_to_the_list(cx: &mut TestAppContext) {
    let mut fake = fake(cx, json!([holder(LANE, "lane"), holder(FAR, "far")]), false);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_pr(Some("o/r".into()), 7, cx)));
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert_eq!(pane.selected.as_ref().map(|wanted| wanted.number), Some(7));
        assert!(pane.choosing.is_some());
    });
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.close_detail(cx)));
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert!(pane.selected.is_none() && pane.choosing.is_none() && pane.opening.is_none());
    });
    assert!(calls(&fake.seen, "resolve_source").is_empty());
}
