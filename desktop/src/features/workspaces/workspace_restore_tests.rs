use super::tests::{Host, key};
use super::{
    SocketClient, WorkspaceActivated, WorkspaceCoordinator, WorkspaceKey, WorkspacePanes,
    WorkspaceTarget,
};
use crate::core::{
    socket::{
        endpoint::Endpoint,
        fake_backend::{FakeBackend, Reply, hello},
    },
    state::workspace_panes::PaneCodec,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{App, AppContext as _, Window};
use serde_json::{Value, json};
use std::{
    cell::RefCell,
    rc::Rc,
    sync::{Arc, Mutex},
};
struct FailingFiles {
    visible: Rc<RefCell<Option<WorkspaceTarget>>>,
    restores: Rc<RefCell<usize>>,
}
impl PaneCodec for FailingFiles {
    fn name(&self) -> &'static str {
        "files"
    }
    fn capture(&self, _: &Window, _: &App) -> Result<Value, String> {
        panic!("a failed hydration must never overwrite its stored snapshot")
    }
    fn validate(&self, _: &Value, _: &WorkspaceKey, _: &Value, _: &App) -> Result<(), String> {
        Ok(())
    }
    fn restore(&self, _: &Value, _: &mut Window, cx: &mut App) {
        assert_eq!(
            self.visible.borrow().as_ref(),
            Some(&WorkspaceTarget::Checkout(key("/target"))),
            "transcript already committed"
        );
        *self.restores.borrow_mut() += 1;
        WorkspacePanes::refuse_restore("The file draft budget changed after preflight.".into(), cx);
    }
}
#[gpui::test]
async fn postcommit_restore_failure_reports_the_actual_error_without_fake_rollback_or_overwriting_the_snapshot(
    cx: &mut TestAppContext,
) {
    let calls = Arc::new(Mutex::new(Vec::new()));
    let seen = calls.clone();
    let snapshot = json!({"version":1,"files":[{"path":"stored.rs","draft":"keep these bytes","baseRevision":"base"}],"activePath":"stored.rs"});
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        seen.lock().unwrap().push(frame.clone());
        assert_eq!(
            frame["method"], "read",
            "no save or retry after failed hydration"
        );
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"revision":7,"snapshot":snapshot}}),
        )]
    });
    cx.executor().allow_parking();
    let visible = Rc::new(RefCell::new(None));
    let restores = Rc::new(RefCell::new(0));
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start_bulk(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        WorkspacePanes::register(
            Box::new(FailingFiles {
                visible: visible.clone(),
                restores: restores.clone(),
            }),
            cx,
        )
        .unwrap();
        WorkspaceCoordinator::init(cx)
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let (sent, mut events) = unbounded();
    let _terminal = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceActivated, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    let callback = visible.clone();
    let target = WorkspaceTarget::Checkout(key("/target"));
    let generation = cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                target.clone(),
                Box::new(move |target, _, _| {
                    *callback.borrow_mut() = Some(target.clone());
                    Ok(())
                }),
                window,
                cx,
            )
        })
    });
    let event = events.recv().await.unwrap();
    assert_eq!(event.generation, generation);
    assert_eq!(event.target, target);
    assert!(!event.restored);
    assert_eq!(*visible.borrow(), Some(target.clone()));
    assert_eq!(*restores.borrow(), 1);
    coordinator.read_with(cx, |view, _| {
        assert!(
            view.status
                .as_ref()
                .unwrap()
                .contains("The file draft budget changed after preflight.")
        );
        let current = view.current.as_ref().unwrap();
        assert!(!current.writable);
        assert_eq!(current.revision, 7);
    });
    cx.update(|_, cx| assert_eq!(WorkspacePanes::key(cx), Some(&key("/target"))));
    let history = WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
        machine: "actual-source".into(),
        id: "history".into(),
    });
    let callback = visible.clone();
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                history.clone(),
                Box::new(move |target, _, _| {
                    *callback.borrow_mut() = Some(target.clone());
                    Ok(())
                }),
                window,
                cx,
            );
        })
    });
    let event = events.recv().await.unwrap();
    assert_eq!(event.target, history);
    assert!(!event.restored, "rootless success carries no hydration");
    assert_eq!(*visible.borrow(), Some(history));
    let calls = calls.lock().unwrap();
    assert_eq!(calls.len(), 1);
    assert_eq!(calls[0]["method"], "read");
}
