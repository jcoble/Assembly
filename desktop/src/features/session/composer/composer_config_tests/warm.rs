use super::*;

fn begin_warm(
    composer: &Entity<Composer>,
    client: &DelayedConfigClient,
    cx: &mut VisualTestContext,
) -> ConfigCall {
    composer.update(cx, |composer, cx| {
        composer.set_owner(
            Some("codex".into()),
            Some("Workbox Test".into()),
            "profile-v1".into(),
            cx,
        )
    });
    let cached = client.take("get", "s1");
    cached.answer.send(Ok(config("sonnet"))).unwrap();
    client
        .take("agents", "")
        .answer
        .send(Ok(
            json!({"agents":[{"id":"codex","name":"Codex","targetId":"advertised-owner-1"}]}),
        ))
        .unwrap();
    cx.run_until_parked();
    let warm = client.take("warm", "s1");
    assert_eq!(
        warm.args,
        json!({"session":"s1","targetId":"advertised-owner-1","machine":"Workbox Test","expectedSource":{"machine":"owner","generation":7,"routeMachine":"Workbox Test"}})
    );
    warm
}

#[gpui::test]
fn a_nonnull_fallback_cache_still_warms_once_and_valid_null_warm_does_not_loop(
    cx: &mut TestAppContext,
) {
    let (composer, client, cx) = delayed_on_s1(cx);
    let warm = begin_warm(&composer, &client, cx);
    assert_eq!(
        menus(&composer, cx)[0].1,
        "sonnet",
        "cached fallback remains readable before actual warm"
    );
    warm.answer
        .send(Ok(json!({"options":null,"choices":{},"commands":null})))
        .unwrap();
    cx.run_until_parked();
    assert!(
        menus(&composer, cx).is_empty(),
        "the actual provider may legitimately advertise no menus"
    );
    composer.update(cx, |composer, cx| {
        composer.set_owner(
            Some("codex".into()),
            Some("Workbox Test".into()),
            "profile-v1".into(),
            cx,
        );
        composer.read_config(cx);
        cx.notify();
    });
    client
        .take("get", "s1")
        .answer
        .send(Ok(json!({"options":null,"choices":{},"commands":null})))
        .unwrap();
    cx.run_until_parked();
    assert!(
        client.calls.borrow().is_empty(),
        "get completion and rendering do not schedule warm"
    );
}

#[gpui::test]
fn a_delayed_cached_get_cannot_replace_warmed_options_or_commands(cx: &mut TestAppContext) {
    let (composer, client, cx) = delayed_on_s1(cx);
    composer.update(cx, |composer, cx| {
        composer.set_owner(
            Some("codex".into()),
            Some("Workbox Test".into()),
            "profile-v1".into(),
            cx,
        )
    });
    let cached = client.take("get", "s1");
    client
        .take("agents", "")
        .answer
        .send(Ok(json!({
            "agents":[{"id":"codex","name":"Codex","targetId":"advertised-owner-1"}]
        })))
        .unwrap();
    cx.run_until_parked();
    let mut actual = config("opus");
    actual["options"][1]["name"] = json!("Actual provider effort");
    actual["commands"] = json!([{ "name":"actual-command", "description":"From owning target" }]);
    client
        .take("warm", "s1")
        .answer
        .send(Ok(actual.clone()))
        .unwrap();
    cx.run_until_parked();
    cached.answer.send(Ok(config("sonnet"))).unwrap();
    cx.run_until_parked();
    assert_eq!(menus(&composer, cx)[0].1, "opus");
    cx.update(|_, cx| {
        let expected = serde_json::from_value::<
            crate::features::session::composer::agent_config::AgentConfig,
        >(actual)
        .unwrap();
        assert_eq!(composer.read(cx).config.as_ref(), Some(&expected));
        assert!(composer.read(cx).error.is_none());
    });
    assert!(
        client.calls.borrow().is_empty(),
        "late cache does not trigger another warm"
    );
}

#[gpui::test]
fn a_failed_warm_keeps_readable_cache_and_reconnect_allows_exactly_one_new_attempt(
    cx: &mut TestAppContext,
) {
    use crate::features::session::client::session_client::CallError;
    let (composer, client, cx) = delayed_on_s1(cx);
    begin_warm(&composer, &client, cx)
        .answer
        .send(Err(CallError::Failed("provider setup required".into())))
        .unwrap();
    cx.run_until_parked();
    assert_eq!(menus(&composer, cx)[0].1, "sonnet");
    assert!(cx.update(|_, cx| {
        composer
            .read(cx)
            .error
            .as_ref()
            .unwrap()
            .contains("provider setup required")
    }));
    composer.update(cx, |composer, cx| {
        composer.set_owner(
            Some("codex".into()),
            Some("Workbox Test".into()),
            "profile-v1".into(),
            cx,
        )
    });
    cx.run_until_parked();
    assert!(
        client.calls.borrow().is_empty(),
        "failure is not automatically retried"
    );
    let (key, source) = composer.read_with(cx, |composer, _| {
        (
            composer.session_key.clone(),
            composer.expected_source.clone(),
        )
    });
    composer.update(cx, |composer, cx| composer.read_config(cx));
    let disconnected = client.take("get", "s1");
    composer.update(cx, |composer, cx| composer.connection_changed(false, cx));
    composer.update(cx, |composer, cx| composer.connection_changed(true, cx));
    cx.run_until_parked();
    assert!(disconnected.answer.send(Ok(config("opus"))).is_err());
    assert!(
        client.calls.borrow().is_empty(),
        "reconnect cannot revive an invalidated source or retry provider setup"
    );
    // Simulate the navigator's newly checked descriptor commit, never a reconnect readiness flag.
    composer.update_in(cx, |composer, window, cx| {
        composer.set_target(key, source, window, cx)
    });
    client
        .take("get", "s1")
        .answer
        .send(Ok(config("sonnet")))
        .unwrap();
    cx.run_until_parked();
    composer.update(cx, |composer, cx| {
        composer.set_owner(
            Some("codex".into()),
            Some("Workbox Test".into()),
            "profile-v1".into(),
            cx,
        )
    });
    client
        .take("get", "s1")
        .answer
        .send(Ok(config("sonnet")))
        .unwrap();
    client
        .take("agents", "")
        .answer
        .send(Ok(
            json!({"agents":[{"id":"codex","name":"Codex","targetId":"advertised-owner-1"}]}),
        ))
        .unwrap();
    cx.run_until_parked();
    client
        .take("warm", "s1")
        .answer
        .send(Ok(config("opus")))
        .unwrap();
    cx.run_until_parked();
    assert_eq!(menus(&composer, cx)[0].1, "opus");
    assert!(client.calls.borrow().is_empty());
}

#[gpui::test]
fn a_menu_pick_cancels_late_warm_and_sends_the_advertised_target(cx: &mut TestAppContext) {
    let (composer, client, cx) = delayed_on_s1(cx);
    let warm = begin_warm(&composer, &client, cx);
    composer.update(cx, |composer, cx| {
        composer.choose("model".into(), "opus".into(), cx)
    });
    cx.run_until_parked();
    assert!(
        warm.answer.send(Ok(config("sonnet"))).is_err(),
        "late warm cannot overwrite a newer pick"
    );
    let pick = client.take("set", "s1");
    assert_eq!(
        pick.args,
        json!({"session":"s1","configId":"model","value":"opus","targetId":"advertised-owner-1","expectedSource":{"machine":"owner","generation":7,"routeMachine":"Workbox Test"}})
    );
    pick.answer.send(Ok(config("opus"))).unwrap();
    cx.run_until_parked();
    assert_eq!(menus(&composer, cx)[0].1, "opus");
}

#[gpui::test]
fn replacing_the_owning_profile_cancels_old_warm_errors_and_uses_new_target(
    cx: &mut TestAppContext,
) {
    use crate::features::session::client::session_client::CallError;
    let (composer, client, cx) = delayed_on_s1(cx);
    let old = begin_warm(&composer, &client, cx);
    composer.update(cx, |composer, cx| {
        composer.set_owner(
            Some("codex".into()),
            Some("Workbox Test".into()),
            "profile-v2".into(),
            cx,
        )
    });
    cx.run_until_parked();
    assert!(
        old.answer
            .send(Err(CallError::Failed("obsolete credentials error".into())))
            .is_err()
    );
    client
        .take("get", "s1")
        .answer
        .send(Ok(config("sonnet")))
        .unwrap();
    client
        .take("agents", "")
        .answer
        .send(Ok(
            json!({"agents":[{"id":"codex","name":"Codex","targetId":"advertised-owner-2"}]}),
        ))
        .unwrap();
    cx.run_until_parked();
    let warm = client.take("warm", "s1");
    assert_eq!(warm.args["targetId"], "advertised-owner-2");
    warm.answer.send(Ok(config("opus"))).unwrap();
    cx.run_until_parked();
    assert!(cx.update(|_, cx| composer.read(cx).error.is_none()));
    assert_eq!(menus(&composer, cx)[0].1, "opus");
}

#[gpui::test]
fn an_unknown_archive_provider_never_lists_targets_or_warms_its_native_handle(
    cx: &mut TestAppContext,
) {
    let (composer, client, cx) = delayed_on_s1(cx);
    composer.update(cx, |composer, cx| {
        composer.set_owner(Some("unknown".into()), None, String::new(), cx)
    });
    client
        .take("get", "s1")
        .answer
        .send(Ok(config("sonnet")))
        .unwrap();
    cx.run_until_parked();
    assert!(client.calls.borrow().is_empty());
    assert!(cx.update(|_, cx| {
        composer
            .read(cx)
            .error
            .as_ref()
            .unwrap()
            .contains("no proven configured provider")
    }));
}

fn observed_effort(
    current: Value,
    choice: &str,
    offered: &[&str],
) -> crate::features::session::composer::agent_config::AgentConfig {
    serde_json::from_value(json!({
        "options":[{"id":"effort","name":"Reasoning effort","type":"select","currentValue":current,
          "options":offered.iter().map(|value|json!({"value":value,"name":value})).collect::<Vec<_>>() }],
        "choices":{"effort":choice}
    })).unwrap()
}
#[test]
fn actual_model_effort_replaces_a_stale_unoffered_durable_choice() {
    let config = observed_effort(json!("minimal"), "high", &["minimal", "medium"]);
    assert_eq!(config.menus()[0].1, "minimal");
}
#[test]
fn actual_model_effort_wins_even_when_the_old_durable_effort_is_still_offered() {
    let config = observed_effort(json!("minimal"), "high", &["minimal", "high"]);
    assert_eq!(config.menus()[0].1, "minimal");
}
#[test]
fn unknown_actual_effort_uses_only_an_offered_explicit_choice_and_never_a_first_default() {
    for current in [Value::Null, json!("invalid-current")] {
        let unknown = observed_effort(current.clone(), "invalid-choice", &["minimal", "medium"]);
        assert_eq!(unknown.menus()[0].1, "");
        let explicit = observed_effort(current, "medium", &["minimal", "medium"]);
        assert_eq!(explicit.menus()[0].1, "medium");
    }
}
