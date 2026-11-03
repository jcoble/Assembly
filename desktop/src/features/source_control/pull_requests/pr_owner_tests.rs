use super::{PrOwner, project_for, resolve};
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply},
    outcome::Outcome,
    row_source::ExpectedRowSource,
};
use crate::features::projects::projects::ProjectRecord;
use crate::features::source_control::git_target::OperationScope;
use gpui::TestAppContext;
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

fn project(id: &str, machine: &str, repository: &str) -> ProjectRecord {
    serde_json::from_value(json!({
        "id": id, "machine": machine, "rootPath": format!("/{id}"), "title": id,
        "repoKey": format!("github.com/{repository}"), "createdAt": 1,
    }))
    .unwrap()
}
fn source() -> ExpectedRowSource {
    ExpectedRowSource {
        machine: "engine".into(),
        generation: 3,
        route_machine: Some("lane".into()),
    }
}
fn owned() -> PrOwner {
    PrOwner::Project {
        id: "p".into(),
        source: source(),
        root: "/p".into(),
    }
}

#[test]
fn a_project_read_names_the_project_and_its_source_and_no_session_or_root() {
    let args = owned().read_args(json!({"number":7}));
    assert_eq!(
        args,
        json!({
            "number": 7, "session": null, "projectId": "p",
            "expectedSource": {"machine":"engine","generation":3,"routeMachine":"lane"},
        })
    );
}

#[test]
fn a_session_read_names_only_the_session_and_any_source_it_was_shown_through() {
    let plain = PrOwner::Session {
        id: "s".into(),
        source: None,
    };
    assert_eq!(
        plain.read_args(json!({"number":7})),
        json!({"number":7,"session":"s"})
    );
    let sourced = PrOwner::Session {
        id: "s".into(),
        source: Some(source()),
    };
    assert_eq!(
        sourced.read_args(json!({}))["expectedSource"]["routeMachine"],
        "lane"
    );
}

#[test]
fn a_project_cannot_act_until_it_has_read_and_then_uses_its_own_registry_root() {
    assert!(owned().scope(None).is_none());
    let scope = owned().scope(Some(("o/r", "/reported"))).unwrap();
    let OperationScope::Project(target) = &scope else {
        panic!("a project scope");
    };
    assert_eq!(target.repository, "o/r");
    assert_eq!(target.checkout_root, "/p");
    assert_eq!(scope.session(), None);
    assert!(scope.read_args(json!({})).get("expectedRepository").is_none());
    let start = scope.write_args(json!({}));
    assert_eq!(start["expectedRepository"], "o/r");
    assert_eq!(start["expectedCheckoutRoot"], "/p");
    assert_eq!(start["projectId"], "p");
}

#[test]
fn a_session_acts_on_the_root_its_read_reported() {
    let owner = PrOwner::Session {
        id: "s".into(),
        source: Some(source()),
    };
    let scope = owner.scope(Some(("o/r", "/root"))).unwrap();
    assert_eq!(scope.session().as_deref(), Some("s"));
    let start = scope.write_args(json!({}));
    assert_eq!(start["expectedCheckoutRoot"], "/root");
    assert!(start.get("projectId").is_none());
    assert!(start.get("expectedRepository").is_none());
}

#[test]
fn a_pull_request_opens_under_the_chosen_project_or_this_computers_copy() {
    let rows = [
        project("remote", "lane", "o/r"),
        project("here", "local", "O/R"),
        project("other", "local", "o/other"),
    ];
    let id = |chosen, repository| project_for(&rows, chosen, repository).map(|project| project.id);
    assert_eq!(id(None, Some("o/r")).as_deref(), Ok("here"));
    assert_eq!(id(Some("remote"), Some("o/r")).as_deref(), Ok("remote"));
    assert_eq!(id(Some("other"), None).as_deref(), Ok("other"));
    assert!(id(Some("other"), Some("o/r")).is_err());
    assert!(id(None, Some("missing/repo")).is_err());
    assert!(id(None, None).is_err());
}

#[test]
fn a_removed_project_never_answers() {
    let mut removed = project("gone", "local", "o/r");
    removed.removed_at = Some(1);
    assert!(project_for(&[removed], None, Some("o/r")).is_err());
}

#[gpui::test]
async fn resolving_asks_for_the_projects_route_and_refuses_another_engines_answer(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let backend = FakeBackend::start(move |frame| {
        if frame["t"] == "hello" {
            return vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"box","engine":"box"}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        let result = if frame["method"] != "resolve_source" {
            json!({})
        } else {
            observed.lock().unwrap().push(frame.clone());
            match frame["args"]["routeMachine"].as_str() {
                None => json!({"machine":"box","generation":0,"routeMachine":null}),
                Some(_) => json!({"machine":"engine","generation":3,"routeMachine":"lane"}),
            }
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    let socket = cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        )
    });
    // Any answer means the welcome, which names this computer's Engine, has arrived.
    let ready = cx.update(|cx| socket.read(cx).call::<Value>("test", "ready", json!({}), None));
    assert!(matches!(ready.await, Outcome::Ok(_)));
    let here = cx.update(|cx| resolve(&project("here", "local", "o/r"), cx)).await;
    assert_eq!(
        here,
        Ok(PrOwner::Project {
            id: "here".into(),
            source: ExpectedRowSource {
                machine: "box".into(),
                generation: 0,
                route_machine: None
            },
            root: "/here".into(),
        })
    );
    let remote = cx.update(|cx| resolve(&project("away", "lane", "o/r"), cx)).await;
    assert_eq!(
        remote,
        Ok(PrOwner::Project {
            id: "away".into(),
            source: source(),
            root: "/away".into()
        })
    );
    assert!(
        cx.update(|cx| resolve(&project("x", "another", "o/r"), cx))
            .await
            .is_err()
    );
    let seen = seen.lock().unwrap();
    assert_eq!(seen[0]["method"], "resolve_source");
    assert_eq!(seen[0]["args"]["routeMachine"], Value::Null);
    assert_eq!(seen[1]["args"]["routeMachine"], "lane");
}
