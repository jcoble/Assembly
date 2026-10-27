//! The new-session form against the real desktop backend (`RealBackend`, whose agents file is empty): what Start
//! session says when the agent or folder is missing, and that a second click waits for the first create.

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};

use super::NewSessionForm;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::real_backend::RealBackend;
use crate::core::state::selected_session::SelectedSession;

/// A form on a client connected to `backend`, once its agents have loaded.
async fn opened<'a>(
    backend: &RealBackend,
    cx: &'a mut TestAppContext,
) -> (Entity<NewSessionForm>, &'a mut VisualTestContext) {
    // The client's thread and the backend answer from outside the test's executor.
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    let (states, mut changes) = unbounded();
    let _heard = cx.update(|cx| {
        SelectedSession::init(cx);
        let socket = SocketClient::start(backend.endpoint(), cx);
        cx.subscribe(&socket, move |_, state: &ConnectionState, _| {
            _ = states.unbounded_send(state.clone())
        })
    });
    while let Ok(state) = changes.recv().await {
        if matches!(state, ConnectionState::Connected { .. }) {
            break;
        }
    }
    let (form, cx) = cx.add_window_view(NewSessionForm::new);
    until(&form, cx, |form| !form.loading).await;
    (form, cx)
}

/// Waits for the form to change until `done` holds.
async fn until(
    form: &Entity<NewSessionForm>,
    cx: &mut VisualTestContext,
    done: impl Fn(&NewSessionForm) -> bool,
) {
    let (changed, mut changes) = unbounded();
    let _heard = cx.update(|_, cx| cx.observe(form, move |_, _| _ = changed.unbounded_send(())));
    while !form.read_with(cx, |form, _| done(form)) {
        changes.recv().await.expect("the form is still there");
    }
}

fn type_folder(form: &Entity<NewSessionForm>, folder: &str, cx: &mut VisualTestContext) {
    form.update_in(cx, |form, window, cx| {
        form.folder
            .update(cx, |state, cx| state.set_value(folder, window, cx))
    });
}

// Pins: g0-session#31
#[gpui::test]
async fn start_session_says_what_is_missing(cx: &mut TestAppContext) {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (form, cx) = opened(&backend, cx).await;
    form.read_with(cx, |form, _| {
        assert!(form.agents.is_empty(), "the backend's agents file is empty");
        assert!(form.error.is_empty(), "{}", form.error);
    });
    type_folder(&form, "/repo", cx);
    form.update(cx, |form, cx| form.create(cx));
    form.read_with(cx, |form, _| {
        assert_eq!(form.error.as_ref(), "Pick an agent and type a folder.");
        assert!(!form.starting);
    });
    type_folder(&form, "  ", cx);
    form.update(cx, |form, cx| {
        form.agent = Some("claude".into());
        form.create(cx);
    });
    form.read_with(cx, |form, _| {
        assert_eq!(form.error.as_ref(), "Pick an agent and type a folder.")
    });
}

// The second click picks another agent first: had it sent a create of its own, the answer would name that agent.
// Pins: g0-session#31
#[gpui::test]
async fn a_second_click_waits_for_the_first_create(cx: &mut TestAppContext) {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (form, cx) = opened(&backend, cx).await;
    type_folder(&form, "/repo", cx);
    form.update(cx, |form, cx| {
        form.agent = Some("first".into());
        form.create(cx);
        form.agent = Some("second".into());
        form.create(cx);
        assert!(form.starting, "Start session waits for the first create");
    });
    until(&form, cx, |form| !form.starting).await;
    form.read_with(cx, |form, _| {
        assert_eq!(
            form.error.as_ref(),
            "Couldn't start the session: this machine has no agent first"
        )
    });
}
