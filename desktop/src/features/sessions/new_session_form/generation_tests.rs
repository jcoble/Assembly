use super::*;

#[gpui::test]
fn a_missing_generation_clears_the_current_agent_and_auth_owner_even_if_state_says_connected(
    cx: &mut TestAppContext,
) {
    let (form, client, cx) = opened_auth(cx);
    let (_, owner) = client.auth_machines.borrow_mut().pop_front().unwrap();
    owner
        .send(Ok(json!({"machine":"actual-owner","through":0})))
        .unwrap();
    cx.run_until_parked();
    assert!(form.read_with(cx, |form, _| form.sign_in_target().is_some()));
    folder(&form, "/kept-draft", cx);
    form.update(cx, |form, cx| form.observe_connection(true, None, cx));
    cx.run_until_parked();
    form.read_with(cx, |form, _| {
        assert!(!form.backend_connected);
        assert_eq!(form.backend_generation, None);
        assert!(form.agent.is_none());
        assert!(form.agents.is_empty());
        assert!(form.config.is_none());
        assert!(
            form.cwd.is_none(),
            "a lost generation cannot retain cwd authority"
        );
        assert!(form.auth_machine.is_none());
        assert!(form.sign_in_target().is_none());
    });
    assert!(client.auth_machines.borrow().is_empty());
}

#[gpui::test]
fn an_absent_backend_has_no_generation_and_does_not_load_a_fake_ready_agent(
    cx: &mut TestAppContext,
) {
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let client = FormClient::default();
    let held = client.clone();
    let (form, cx) = cx
        .add_window_view(move |window, cx| NewSessionForm::with_client(Rc::new(held), window, cx));
    cx.run_until_parked();
    form.read_with(cx, |form, _| {
        assert!(!form.backend_connected);
        assert_eq!(form.backend_generation, None);
        assert!(form.agent.is_none());
    });
    assert!(client.fake.calls().is_empty());
}
