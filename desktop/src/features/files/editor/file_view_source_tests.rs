use super::FileView;
use crate::core::engine::SessionKey;
use crate::core::socket::{
    client::SocketClient,
    connection_state::ConnectionState,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    row_source::ExpectedRowSource,
    test_wait::until,
};
use crate::core::state::selected_session::SelectedSession;
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use serde_json::json;
use std::sync::{Arc, Mutex};

#[gpui::test]
async fn an_existing_file_sends_its_original_source_after_same_id_selection_is_replaced(
    cx: &mut TestAppContext,
) {
    let received = Arc::new(Mutex::new(Vec::new()));
    let seen = received.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return vec![];
        }
        assert_eq!(frame["feature"], "files");
        assert_eq!(frame["method"], "owner");
        seen.lock().unwrap().push(frame["args"].clone());
        vec![Reply::Frame(
            json!({"t":"error","id":frame["id"],"code":"source_changed","message":"The original source changed."}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let original = ExpectedRowSource {
        machine: "actual-owner".into(),
        generation: 7,
        route_machine: Some("route".into()),
    };
    let (view, cx) = cx.add_window_view(|_, cx| {
        let mut view = FileView::new("same-id".into(), "a.rs".into(), cx);
        view.source = Some(original.clone());
        view
    });
    let (changed, mut changes) = unbounded();
    let socket = cx.update(|_, cx| SocketClient::global(cx));
    let tell = changed.clone();
    let _socket_events = cx.update(|_, cx| {
        cx.observe(&socket, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    until("actual file socket", &mut changes, &changed, || {
        socket.read_with(cx, |socket, _| {
            matches!(socket.state(), ConnectionState::Connected { .. })
        })
    })
    .await;
    let tell = changed.clone();
    let _file_events = cx.update(|_, cx| {
        cx.observe(&view, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.commit_open(
                SessionKey {
                    machine: "actual-owner".into(),
                    id: "same-id".into(),
                },
                ExpectedRowSource {
                    generation: 8,
                    ..original.clone()
                },
                false,
                cx,
            )
        })
    });
    view.update(cx, |view, cx| {
        view.active = true;
        view.load_owner(cx);
    });
    until("source refusal", &mut changes, &changed, || {
        view.read_with(cx, |view, _| view.text.error.is_some())
    })
    .await;
    assert_eq!(
        received.lock().unwrap().as_slice(),
        [json!({"session":"same-id","expectedSource":original,"expectedOwner":null})]
    );
    view.read_with(cx, |view, _| {
        assert!(view.owner.is_none());
        assert!(view.document.is_none());
        assert_eq!(
            view.text.error.as_deref(),
            Some("The original source changed.")
        );
    });
}
