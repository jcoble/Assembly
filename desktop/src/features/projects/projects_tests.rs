use super::Projects;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};
#[gpui::test]
async fn one_collection_subscribes_before_read_and_pages_without_duplicate_records(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let sent = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        observed.lock().unwrap().push(frame.clone());
        if frame["t"] == "sub" {
            assert_eq!(frame["topic"], "projects");
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[{"key":"revision","item":{"revision":1}}]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        assert_eq!(frame["method"], "list");
        assert!(
            observed
                .lock()
                .unwrap()
                .iter()
                .any(|frame| frame["t"] == "sub")
        );
        let second = !frame["args"]["after"].is_null();
        let _ = sent.unbounded_send(());
        let record = |id: &str| json!({"id":id,"machine":"local","rootPath":format!("/owned/{id}"),"title":id,"repoKey":"github.com/o/r","pinnedAt":null,"removedAt":null,"createdAt":1,"lastUsedAt":null});
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"projects":if second{vec![record("one"),record("two")]}else{vec![record("one")]},"next":if second{Value::Null}else{json!({"pinned":0,"lastUsedAt":null,"createdAt":1,"id":"one"})}}}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let projects = cx.update(|cx| Projects::get(cx).unwrap());
    let same = cx.update(|cx| Projects::get(cx).unwrap());
    assert_eq!(projects.entity_id(), same.entity_id());
    let sent = changed.clone();
    let _observe = cx.update(|cx| {
        cx.observe(&projects, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    until("first projects", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| projects.read(cx).rows().len() == 1)
    })
    .await;
    projects.update(cx, |projects, cx| {
        projects.load_more(cx);
        projects.load_more(cx);
    });
    until("more projects", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| projects.read(cx).rows().len() == 2)
    })
    .await;
    let intent = crate::core::intents::open_project::OpenProject {
        project_id: "one".into(),
        machine: "local".into(),
        root_path: "/owned/one".into(),
    };
    cx.update(|cx| assert_eq!(projects.read(cx).resolve_open(&intent).unwrap().id, "one"));
    projects.update(cx, |projects, _| {
        projects.rows[0].root_path = "/replacement".into()
    });
    cx.update(|cx| assert!(projects.read(cx).resolve_open(&intent).is_none()));
    let changed = crate::core::intents::open_project::OpenProject {
        root_path: "/replacement".into(),
        ..intent
    };
    cx.update(|cx| assert!(projects.read(cx).resolve_open(&changed).is_some()));
    projects.update(cx, |projects, _| projects.rows[0].removed_at = Some(2));
    cx.update(|cx| assert!(projects.read(cx).resolve_open(&changed).is_none()));
    let seen = seen.lock().unwrap();
    assert_eq!(seen.iter().filter(|frame| frame["t"] == "sub").count(), 1);
    assert_eq!(seen.iter().filter(|frame| frame["t"] == "call").count(), 2);
}
#[test]
fn github_scope_comes_only_from_the_registered_repository_key() {
    let record:super::ProjectRecord=serde_json::from_value(json!({"id":"p","machine":"remote","rootPath":"/owned/project","title":"Misleading title","repoKey":"github.com/owner/repo","pinnedAt":null,"removedAt":null,"createdAt":1,"lastUsedAt":null})).unwrap();
    assert_eq!(record.github_repository(), Some("owner/repo"));
    assert_eq!(
        super::ProjectRecord {
            repo_key: Some("gitlab.com/owner/repo".into()),
            ..record
        }
        .github_repository(),
        None
    );
}
