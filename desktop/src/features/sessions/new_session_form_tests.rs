use serde_json::json;

use super::NewSessionForm;
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::client::session_client::{
    CallError, ClockSink, RowsSink, SessionClient,
};
use crate::features::session::composer::draft_config::tests::catalogue_value;
use futures_channel::oneshot;
use gpui::TestAppContext;
use gpui_kit::{App, Entity, Subscription, Task, VisualTestContext};
use serde_json::Value;
use std::{cell::RefCell, rc::Rc};

type HeldDiscovery = (Value, oneshot::Sender<Result<Value, CallError>>);
#[derive(Clone, Default)]
struct FormClient {
    fake: FakeClient,
    discoveries: Rc<RefCell<std::collections::VecDeque<HeldDiscovery>>>,
    hold_creates: Rc<std::cell::Cell<bool>>,
    creates: Rc<RefCell<std::collections::VecDeque<HeldDiscovery>>>,
    create_count: Rc<std::cell::Cell<usize>>,
    auth_machines: Rc<RefCell<std::collections::VecDeque<HeldDiscovery>>>,
}
impl SessionClient for FormClient {
    fn subscribe_rows(
        &self,
        _: &str,
        _: Option<i64>,
        _: Option<&crate::core::socket::row_source::ExpectedRowSource>,
        _: RowsSink,
        _: &App,
    ) -> Subscription {
        Subscription::new(|| {})
    }
    fn subscribe_message(
        &self,
        message: &str,
        machine: Option<&str>,
        expected_source: Option<&crate::core::socket::row_source::ExpectedRowSource>,
        sink: crate::features::session::client::session_client::MessageSink,
        cx: &App,
    ) -> Subscription {
        self.fake
            .subscribe_message(message, machine, expected_source, sink, cx)
    }
    fn clock(&self, _: ClockSink, _: &mut App) -> Subscription {
        Subscription::new(|| {})
    }
    fn call(
        &self,
        feature: &str,
        method: &str,
        args: Value,
        key: Option<&str>,
        cx: &App,
    ) -> Task<Result<Value, CallError>> {
        if (feature, method) == ("engine", "machine") {
            let (answer, receiver) = oneshot::channel();
            self.auth_machines.borrow_mut().push_back((args, answer));
            return cx
                .foreground_executor()
                .spawn(async move { receiver.await.unwrap_or(Err(CallError::Aborted)) });
        }
        if (feature, method) == ("sessions", "create") && self.hold_creates.get() {
            self.create_count.set(self.create_count.get() + 1);
            let (answer, receiver) = oneshot::channel();
            self.creates.borrow_mut().push_back((args, answer));
            return cx
                .foreground_executor()
                .spawn(async move { receiver.await.unwrap_or(Err(CallError::Aborted)) });
        }
        if (feature, method) != ("config", "discover") {
            return self.fake.call(feature, method, args, key, cx);
        }
        let (answer, receiver) = oneshot::channel();
        self.discoveries.borrow_mut().push_back((args, answer));
        cx.foreground_executor()
            .spawn(async move { receiver.await.unwrap_or(Err(CallError::Aborted)) })
    }
}
// Source-derived unit fixture: explicitly deliver a connected socket generation; no provider/native proof.
fn connected_form(
    client: Rc<dyn SessionClient>,
    window: &mut gpui_kit::Window,
    cx: &mut gpui_kit::Context<NewSessionForm>,
) -> NewSessionForm {
    let mut form = NewSessionForm::with_client(client, window, cx);
    form.observe_connection(true, Some(1), cx);
    form
}
fn opened(cx: &mut TestAppContext) -> (Entity<NewSessionForm>, FormClient, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let client = FormClient::default();
    client.fake.queue_answer(Ok(
        json!({"agents":[{"id":"codex","name":"Codex","targetId":"target-1"}]}),
    ));
    let held = client.clone();
    let (form, cx) =
        cx.add_window_view(move |window, cx| connected_form(Rc::new(held), window, cx));
    cx.run_until_parked();
    (form, client, cx)
}
fn folder(form: &Entity<NewSessionForm>, value: &str, cx: &mut VisualTestContext) {
    // Source-derived catalogue fixture: an already observed cwd, never a start-root argument.
    form.update(cx, |form, cx| {
        form.cwd = Some(value.into());
        form.discover(cx);
    });
    cx.run_until_parked();
}

fn discover(client: &FormClient) -> HeldDiscovery {
    while let Some(held) = client.discoveries.borrow_mut().pop_front() {
        if !held.1.is_canceled() {
            return held;
        }
    }
    panic!("owned current discovery");
}

#[gpui::test]
fn changing_the_folder_cancels_old_discovery_and_does_not_install_its_error(
    cx: &mut TestAppContext,
) {
    let (form, client, cx) = opened(cx);
    folder(&form, "/repo-A", cx);
    let (old_args, old) = discover(&client);
    assert_eq!(
        old_args,
        json!({"agent":"codex","targetId":"target-1","cwd":"/repo-A","machine":null})
    );
    folder(&form, "/repo-B", cx);
    assert!(
        old.send(Err(CallError::Failed("obsolete folder error".into())))
            .is_err()
    );
    let (args, current) = discover(&client);
    assert_eq!(args["cwd"], "/repo-B");
    current.send(Ok(catalogue_value())).unwrap();
    cx.run_until_parked();
    form.read_with(cx, |form, _| {
        assert!(form.error.is_empty());
        assert_eq!(
            form.config.as_ref().unwrap().config.choices["provider-model"],
            "A"
        );
    });
}

#[gpui::test]
fn missing_advertised_target_blocks_discovery_and_absolute_folder_creation(
    cx: &mut TestAppContext,
) {
    let (form, client, cx) = opened(cx);
    form.update(cx, |form, _| form.agents[0].target_id = None);
    folder(&form, "/repo", cx);
    form.update_in(cx, |form, window, cx| {
        form.first_send("kept first prompt".into(), Vec::new(), window, cx)
    });
    cx.run_until_parked();
    assert!(client.discoveries.borrow().is_empty());
    assert!(
        !client
            .fake
            .calls()
            .iter()
            .any(|(method, _)| method == "sessions/create")
    );
    assert!(form.read_with(cx, |form, _| { !form.error.is_empty() }));
}

#[gpui::test]
fn switching_machines_cancels_discovery_and_forwards_only_the_new_advertised_target(
    cx: &mut TestAppContext,
) {
    let (form, client, cx) = opened(cx);
    folder(&form, "/repo", cx);
    let (_, old) = discover(&client);
    client.fake.queue_answer(Ok(
        json!({"agents":[{"id":"codex","name":"Remote Codex","targetId":"remote-wrapper-target"}]}),
    ));
    form.update(cx, |form, cx| {
        form.pick_machine(Some("Workbox Test".into()), cx)
    });
    cx.run_until_parked();
    folder(&form, "/repo", cx);
    assert!(
        old.send(Err(CallError::Failed("old local target".into())))
            .is_err()
    );
    let (args, current) = discover(&client);
    assert_eq!(
        args,
        json!({"agent":"codex","targetId":"remote-wrapper-target","cwd":"/repo","machine":"Workbox Test"})
    );
    current.send(Ok(catalogue_value())).unwrap();
    cx.run_until_parked();
    assert!(form.read_with(cx, |form, _| form.error.is_empty()));
}

fn auth_agents() -> Value {
    json!({"agents":[{"id":"codex","name":"Codex","targetId":"configuration-hash","accountContext":"","providerTargetId":"codex/"}]})
}
fn opened_auth(
    cx: &mut TestAppContext,
) -> (Entity<NewSessionForm>, FormClient, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let client = FormClient::default();
    client.fake.queue_answer(Ok(auth_agents()));
    let held = client.clone();
    let (form, cx) =
        cx.add_window_view(move |window, cx| connected_form(Rc::new(held), window, cx));
    cx.run_until_parked();
    (form, client, cx)
}
#[gpui::test]
fn auth_entry_uses_the_actual_engine_identity_without_needing_a_folder_or_session(
    cx: &mut TestAppContext,
) {
    let (form, client, cx) = opened_auth(cx);
    assert!(
        form.read_with(cx, |form, _| form.sign_in_target())
            .is_none()
    );
    let (_, answer) = client.auth_machines.borrow_mut().pop_front().unwrap();
    answer
        .send(Ok(json!({"machine":"stable-engine","through":0})))
        .unwrap();
    cx.run_until_parked();
    form.read_with(cx, |form, _| {
        let target = form.sign_in_target().unwrap();
        assert_eq!(target.machine, "stable-engine");
        assert_eq!(target.provider_target_id, "codex/");
        assert_eq!(target.configuration_target_id, "configuration-hash");
        assert!(form.cwd.is_none());
    });
    assert!(
        client
            .fake
            .calls()
            .iter()
            .all(|(method, _)| method != "sessions/create" && method != "engine/command")
    );
    form.update(cx, |form, _| form.machine = Some("remote".into()));
    assert!(
        form.read_with(cx, |form, _| form.sign_in_target())
            .is_none()
    );
}
#[gpui::test]
fn a_replaced_agents_load_cannot_install_an_obsolete_auth_machine_reply(cx: &mut TestAppContext) {
    let (form, client, cx) = opened_auth(cx);
    let (_, old) = client.auth_machines.borrow_mut().pop_front().unwrap();
    client.fake.queue_answer(Ok(auth_agents()));
    form.update(cx, |form, cx| form.load_agents(cx));
    cx.run_until_parked();
    let _ = old.send(Ok(json!({"machine":"obsolete-owner","through":0})));
    assert!(
        form.read_with(cx, |form, _| form.sign_in_target())
            .is_none()
    );
    let (_, fresh) = client.auth_machines.borrow_mut().pop_front().unwrap();
    fresh
        .send(Ok(json!({"machine":"current-owner","through":0})))
        .unwrap();
    cx.run_until_parked();
    assert_eq!(
        form.read_with(cx, |form, _| form.sign_in_target().unwrap().machine),
        "current-owner"
    );
}

#[gpui::test]
fn actual_signed_in_refreshes_failed_discovery_only_for_the_still_selected_target(
    cx: &mut TestAppContext,
) {
    let (form, client, cx) = opened_auth(cx);
    let (_, answer) = client.auth_machines.borrow_mut().pop_front().unwrap();
    answer
        .send(Ok(json!({"machine":"owner-engine","through":0})))
        .unwrap();
    cx.run_until_parked();
    folder(&form, "/actual-project", cx);
    let (original, answer) = discover(&client);
    answer
        .send(Err(CallError::Failed("Authentication required".into())))
        .unwrap();
    cx.run_until_parked();
    let (target, revision) = form.read_with(cx, |form, _| {
        (form.sign_in_target().unwrap(), form.list_generation)
    });
    let mut wrong = target.clone();
    wrong.configuration_target_id = "obsolete".into();
    form.update(cx, |form, cx| form.signed_in(&wrong, revision, cx));
    cx.run_until_parked();
    assert!(client.discoveries.borrow().is_empty());
    form.update(cx, |form, cx| form.signed_in(&target, revision, cx));
    cx.run_until_parked();
    let (fresh, _) = discover(&client);
    assert_eq!(fresh, original);
    assert!(
        client
            .fake
            .calls()
            .iter()
            .all(|(method, _)| method != "sessions/create" && method != "send/send")
    );
}

#[gpui::test]
fn a_coalesced_reconnect_replaces_the_auth_owner_even_while_still_connected(
    cx: &mut TestAppContext,
) {
    let (form, client, cx) = opened_auth(cx);
    let (_, obsolete) = client.auth_machines.borrow_mut().pop_front().unwrap();
    client.fake.queue_answer(Ok(auth_agents()));
    form.update(cx, |form, cx| form.observe_connection(true, Some(2), cx));
    cx.run_until_parked();
    let _ = obsolete.send(Ok(json!({"machine":"obsolete-owner","through":0})));
    cx.run_until_parked();
    assert!(
        form.read_with(cx, |form, _| form.sign_in_target())
            .is_none()
    );
    let (_, fresh) = client.auth_machines.borrow_mut().pop_front().unwrap();
    fresh
        .send(Ok(json!({"machine":"fresh-owner","through":0})))
        .unwrap();
    cx.run_until_parked();
    assert_eq!(
        form.read_with(cx, |form, _| form.sign_in_target().unwrap().machine),
        "fresh-owner"
    );
    form.update(cx, |form, cx| form.observe_connection(true, Some(2), cx));
    cx.run_until_parked();
    assert!(
        client.auth_machines.borrow().is_empty(),
        "same generation must not reload"
    );
    assert_eq!(
        form.read_with(cx, |form, _| form.sign_in_target().unwrap().machine),
        "fresh-owner"
    );
}

#[gpui::test]
fn selecting_a_valid_agent_replaces_the_previous_agents_identity_error(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let client = FormClient::default();
    client.fake.queue_answer(Ok(json!({"agents":[
        {"id":"invalid","name":"Invalid","targetId":"invalid-config","accountContext":"","providerTargetId":"wrong/"},
        {"id":"codex","name":"Codex","targetId":"configuration-hash","accountContext":"","providerTargetId":"codex/"}
    ]})));
    let held = client.clone();
    let (form, cx) =
        cx.add_window_view(move |window, cx| connected_form(Rc::new(held), window, cx));
    cx.run_until_parked();
    let (_, answer) = client.auth_machines.borrow_mut().pop_front().unwrap();
    answer
        .send(Ok(json!({"machine":"stable-engine","through":0})))
        .unwrap();
    cx.run_until_parked();
    form.read_with(cx, |form, _| {
        assert!(form.sign_in_target().is_none());
        assert!(form.auth_error.contains("identity did not match"));
    });
    form.update(cx, |form, cx| form.pick_agent("codex", cx));
    cx.run_until_parked();
    form.read_with(cx, |form, _| {
        assert_eq!(form.sign_in_target().unwrap().provider, "codex");
        assert!(form.auth_error.is_empty(), "{}", form.auth_error);
    });
    assert!(
        client.auth_machines.borrow().is_empty(),
        "reuse the owning machine identity"
    );
    form.update(cx, |form, cx| form.pick_agent("invalid", cx));
    cx.run_until_parked();
    form.read_with(cx, |form, _| {
        assert!(form.sign_in_target().is_none());
        assert!(form.auth_error.contains("identity did not match"));
    });
}

#[gpui::test]
fn a_failed_start_asks_to_show_again_so_its_text_and_error_are_seen(cx: &mut TestAppContext) {
    use super::DraftEvent;
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let client = FormClient::default();
    let (form, cx) = cx.add_window_view(move |window, cx| {
        NewSessionForm::with_client(Rc::new(client), window, cx)
    });
    cx.run_until_parked();
    assert!(
        form.read_with(cx, |form, _| !form.centered),
        "a project's dialog stays compact"
    );
    let failed = Rc::new(RefCell::new(0));
    let count = failed.clone();
    let _failed = cx.update(|_, cx| {
        cx.subscribe(&form, move |_, event: &DraftEvent, _| {
            if matches!(event, DraftEvent::Failed) {
                *count.borrow_mut() += 1;
            }
        })
    });
    form.update(cx, |form, cx| form.start_error("Couldn't start.", cx));
    cx.run_until_parked();
    assert_eq!(*failed.borrow(), 1);
    form.read_with(cx, |form, _| {
        assert_eq!(form.error.as_ref(), "Couldn't start.")
    });
}

#[path = "new_session_form/generation_tests.rs"]
mod generation_tests;

#[gpui::test]
fn an_unselected_observed_model_requires_a_pick_and_is_retained_in_the_first_send_configuration(
    cx: &mut TestAppContext,
) {
    let (form, client, cx) = opened(cx);
    folder(&form, "/observed-project", cx);
    let mut catalogue = catalogue_value();
    catalogue["options"][0]["currentValue"] = Value::Null;
    discover(&client).1.send(Ok(catalogue)).unwrap();
    cx.run_until_parked();
    form.read_with(cx, |form, _| {
        let config = form.config.as_ref().unwrap();
        assert!(!config.config.choices.contains_key("provider-model"));
        assert!(!config.ready());
    });
    form.update_in(cx, |form, window, cx| {
        form.first_send("retained text".into(), Vec::new(), window, cx)
    });
    assert!(
        !client
            .fake
            .calls()
            .iter()
            .any(|(method, _)| method == "sessions/create")
    );
    form.update(cx, |form, cx| form.choose("provider-model", "B", cx));
    form.read_with(cx, |form, _| {
        let config = form.config.as_ref().unwrap();
        assert!(config.ready());
        assert_eq!(config.config.choices["provider-model"], "B");
        assert_eq!(config.config.choices["reasoning_effort"], "minimal");
        assert_eq!(config.config.choices["permission"], "ask");
        assert!(form.attempt.is_none());
    });
}
#[path = "new_session_form/project_tests.rs"]
mod project_tests;
