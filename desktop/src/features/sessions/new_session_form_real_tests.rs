//! Real-backend empty-agent refusal and registry draft selection. Valid first-send ownership is exercised in first_send_tests.

use std::cell::Cell;
use std::rc::Rc;

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

#[gpui::test]
async fn first_send_without_an_advertised_agent_preserves_the_draft_and_selects_no_session(
    cx: &mut TestAppContext,
) {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (form, cx) = opened(&backend, cx).await;
    form.read_with(cx, |form, _| assert!(form.agents.is_empty()));
    form.update_in(cx, |form, window, cx| {
        form.first_send("unsent text".into(), Vec::new(), window, cx)
    });
    form.read_with(cx, |form, _| {
        assert_eq!(
            form.error.as_ref(),
            "Choose ready settings advertised by this machine."
        );
        assert!(!form.starting);
        assert!(form.attempt.is_none());
    });
    assert!(cx.update(|_, cx| SelectedSession::global(cx).read(cx).key().is_none()));
}
#[gpui::test]
async fn repeated_first_send_cannot_fabricate_a_ready_agent_or_create_an_empty_session(
    cx: &mut TestAppContext,
) {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (form, cx) = opened(&backend, cx).await;
    form.update_in(cx, |form, window, cx| {
        form.agent = Some("not-advertised".into());
        form.first_send("same text".into(), Vec::new(), window, cx);
        form.first_send("same text".into(), Vec::new(), window, cx);
        assert!(form.attempt.is_none());
        assert!(!form.starting);
    });
    assert!(cx.update(|_, cx| SelectedSession::global(cx).read(cx).key().is_none()));
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

#[gpui::test]
async fn a_project_selection_keeps_the_registry_copy_and_does_not_invent_remote_ownership(
    cx: &mut TestAppContext,
) {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (form, cx) = opened(&backend, cx).await;
    // The agents load and the location read each write the form's one error and either can land last, so the load's
    // refusal is caught as it lands.
    let refused = Rc::new(Cell::new(false));
    let (changed, mut changes) = unbounded();
    let (seen, tell) = (refused.clone(), changed.clone());
    let _heard = cx.update(|_, cx| {
        cx.observe(&form, move |form, cx| {
            seen.set(seen.get() || form.read(cx).error.contains("unknown machine"));
            _ = tell.unbounded_send(());
        })
    });
    let project=serde_json::from_value(serde_json::json!({"id":"actual-copy","machine":"Workbox Test","rootPath":"/srv/app","title":"App","repoKey":null,"pinnedAt":null,"removedAt":null,"createdAt":1,"lastUsedAt":null})).unwrap();
    form.update_in(cx, |form, window, cx| {
        form.fill_project(project, window, cx)
    });
    form.read_with(cx, |form, _| {
        assert_eq!(form.selected_project.as_ref().unwrap().id, "actual-copy");
        assert_eq!(form.machine.as_deref(), Some("Workbox Test"));
        assert!(form.location_source.is_none());
        assert!(form.attempt.is_none());
    });
    until("the agents load's refusal", &mut changes, &changed, || {
        refused.get()
    })
    .await;
}
