//! The new-session form against the real desktop backend (`RealBackend`, whose agents file is empty): what Start
//! session says when the agent or folder is missing, that a second click waits for the first create, and when the
//! agents load again.

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};

use super::NewSessionForm;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::real_backend::RealBackend;
use crate::core::socket::test_wait::until;
use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;

/// A form on a client connected to `backend`, with the machines list started, once its agents have loaded.
async fn opened<'a>(
    backend: &RealBackend,
    cx: &'a mut TestAppContext,
) -> (Entity<NewSessionForm>, &'a mut VisualTestContext) {
    // The client's thread and the backend answer from outside the test's executor.
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let (socket, _heard) = cx.update(|cx| {
        SelectedSession::init(cx);
        let socket = SocketClient::start(backend.endpoint(), cx);
        Machines::start(cx);
        let heard = cx.observe(&socket, move |_, _| _ = tell.unbounded_send(()));
        (socket, heard)
    });
    until("connected", &mut changes, &changed, || {
        cx.read(|cx| matches!(socket.read(cx).state(), ConnectionState::Connected { .. }))
    })
    .await;
    let (form, cx) = cx.add_window_view(NewSessionForm::new);
    settled(&form, cx, |form| !form.loading).await;
    (form, cx)
}

/// Waits for the form to change until `done` holds.
async fn settled(
    form: &Entity<NewSessionForm>,
    cx: &mut VisualTestContext,
    done: impl Fn(&NewSessionForm) -> bool,
) {
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|_, cx| cx.observe(form, move |_, _| _ = tell.unbounded_send(())));
    until("the form", &mut changes, &changed, || {
        form.read_with(cx, |form, _| done(form))
    })
    .await;
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
    form.update(cx, |form, _| {
        assert_eq!(form.error.as_ref(), "Pick an agent and type a folder.");
        assert!(!form.starting);
        form.error = Default::default();
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
    settled(&form, cx, |form| !form.starting).await;
    form.read_with(cx, |form, _| {
        assert_eq!(
            form.error.as_ref(),
            "Couldn't start the session: this machine has no agent first"
        )
    });
}

// A machine with no agents isn't asked again on every machines change; a load the connection dropped is.
// Pins: g0-session#31
#[gpui::test]
async fn the_agents_load_again_on_a_machines_change_only_after_a_dropped_load(
    cx: &mut TestAppContext,
) {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (form, cx) = opened(&backend, cx).await;
    let machines = cx.update(|_, cx| Machines::try_global(cx).expect("the machines list"));
    machines.update(cx, |_, cx| cx.notify());
    form.read_with(cx, |form, _| {
        assert!(!form.loading, "an empty list was asked for again")
    });
    form.update(cx, |form, _| form.retry_agents = true);
    machines.update(cx, |_, cx| cx.notify());
    form.read_with(cx, |form, _| assert!(form.loading && !form.retry_agents));
    settled(&form, cx, |form| !form.loading).await;
}

// A project header's + fills the form with its folder and machine; the machine's agents load for it.
// Pins: sessions-list#8
#[gpui::test]
async fn a_projects_new_session_fills_its_folder_and_machine(cx: &mut TestAppContext) {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (form, cx) = opened(&backend, cx).await;
    form.update_in(cx, |form, window, cx| {
        form.fill("/srv/app", Some("Workbox Test".into()), window, cx)
    });
    form.read_with(cx, |form, cx| {
        assert_eq!(form.folder.read(cx).value(), "/srv/app");
        assert_eq!(form.machine.as_deref(), Some("Workbox Test"));
    });
    // This backend has no such machine, so its agents load fails and says so.
    settled(&form, cx, |form| !form.loading).await;
    form.read_with(cx, |form, _| {
        assert!(form.error.contains("unknown machine"), "{}", form.error)
    });
}
