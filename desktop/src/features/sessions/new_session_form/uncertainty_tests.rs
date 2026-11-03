#[gpui::test]
fn an_uncertain_create_preserves_the_backend_message_and_never_replays_it(cx: &mut TestAppContext) {
    let (form, client, cx) = opened(cx);
    folder(&form, "/repo", cx);
    discover(&client).1.send(Ok(catalogue_value())).unwrap();
    cx.run_until_parked();
    client.fake.queue_answer(Err(CallError::Uncertain {
        machine: "actual-machine".into(),
        message: "The owning backend may have started this session. Refresh its sessions.".into(),
    }));
    form.update(cx, |form, cx| {
        form.create(cx);
        form.create(cx);
    });
    cx.run_until_parked();
    form.read_with(cx, |form, _| {
        assert_eq!(
            form.error.as_ref(),
            "The owning backend may have started this session. Refresh its sessions."
        )
    });
    cx.run_until_parked();
    assert_eq!(
        client
            .fake
            .calls()
            .iter()
            .filter(|(method, _)| method == "sessions/create")
            .count(),
        1
    );
}
