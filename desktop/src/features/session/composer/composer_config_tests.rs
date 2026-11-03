//! The agent's menus and its "/" commands, on the fake client.

use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext, px};
use serde_json::{Value, json};

use super::super::Composer;
use super::super::tests::{draft, open};
use crate::features::session::client::fake_client::FakeClient;

fn config(model: &str) -> Value {
    json!({
        "options": [
            { "id": "model", "name": "Model", "category": "model", "type": "select", "currentValue": model,
              "options": [{ "value": "opus", "name": "Opus" }, { "value": "sonnet", "name": "Sonnet" }] },
            { "id": "effort", "name": "Effort", "type": "select", "currentValue": "high",
              "options": [{ "value": "high", "name": "High" }] },
            { "id": "fast", "name": "Fast", "type": "boolean", "currentValue": "false" },
            { "id": "none", "name": "None", "type": "select", "options": [] }
        ],
        "choices": { "model": model },
        "commands": [
            { "name": "review", "description": "Review the diff", "input": { "hint": "what to look at" } },
            { "name": "compact", "description": "Compact the context" },
            { "name": "pr-review", "description": "Review a pull request" },
            { "name": "__hidden", "description": "Not for people" }
        ]
    })
}

fn menus(composer: &Entity<Composer>, cx: &mut VisualTestContext) -> Vec<(String, String)> {
    cx.update(|_, cx| {
        let composer = composer.read(cx);
        let menus = composer.menus();
        menus
            .into_iter()
            .map(|(option, current)| (option.id.clone(), current.to_string()))
            .collect()
    })
}

fn listed(composer: &Entity<Composer>, cx: &mut VisualTestContext) -> Vec<String> {
    cx.update(|_, cx| {
        let commands = composer.read(cx).slash_list(cx);
        commands
            .into_iter()
            .map(|command| command.name.clone())
            .collect()
    })
}

/// The composer on session s1 with the agent's config read, and the draft focused for typing.
fn on_s1(cx: &mut TestAppContext) -> (Entity<Composer>, FakeClient, &mut VisualTestContext) {
    let (composer, client, _, cx) = open(cx);
    client.set_config(Some(config("sonnet")));
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx);
        composer
            .draft
            .update(cx, |draft, cx| draft.focus(window, cx));
    });
    cx.run_until_parked();
    (composer, client, cx)
}

// Pins: g0-session#27
#[gpui::test]
fn the_agents_menus_are_read_for_the_session_with_their_values(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    assert_eq!(
        client.config_calls(),
        [("config/get".to_string(), json!({ "session": "s1" }))]
    );
    assert_eq!(
        menus(&composer, cx),
        [
            ("model".to_string(), "sonnet".to_string()),
            ("effort".to_string(), "high".to_string())
        ],
        "select menus with choices, each with this session's value or the agent's own"
    );
}

// Pins: g0-session#27
#[gpui::test]
fn a_pick_sets_the_value_and_the_menu_shows_what_the_agent_answers(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    client.set_config(Some(config("opus")));
    composer.update(cx, |composer, cx| {
        composer.choose("model".into(), "opus".into(), cx)
    });
    cx.run_until_parked();
    assert_eq!(
        client.config_calls()[1],
        (
            "config/set".to_string(),
            json!({ "session": "s1", "configId": "model", "value": "opus" })
        )
    );
    assert_eq!(menus(&composer, cx)[0].1, "opus");
}

// Pins: g0-session#27
#[gpui::test]
fn a_pick_that_fails_says_so_and_keeps_the_value(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    client.set_config(None);
    composer.update(cx, |composer, cx| {
        composer.choose("model".into(), "opus".into(), cx)
    });
    cx.run_until_parked();
    assert_eq!(menus(&composer, cx)[0].1, "sonnet");
    let error = cx.update(|_, cx| composer.read(cx).error.clone());
    assert_eq!(error.as_deref(), Some("Couldn't change Model: no config"));
}

// Pins: g0-session#27
#[gpui::test]
fn the_menus_are_read_again_when_a_turn_ends(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    composer.update(cx, |composer, cx| {
        composer.set_running(Some("r1".into()), cx)
    });
    cx.run_until_parked();
    assert_eq!(client.config_calls().len(), 1, "not while it runs");
    client.set_config(Some(config("opus")));
    composer.update(cx, |composer, cx| composer.set_running(None, cx));
    cx.run_until_parked();
    assert_eq!(client.config_calls().len(), 2);
    assert_eq!(
        menus(&composer, cx)[0].1,
        "opus",
        "the agent changed it itself"
    );
}

// Pins: g0-session#26
#[gpui::test]
fn typing_a_slash_lists_the_commands_and_enter_picks_one_into_the_draft(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    cx.simulate_input("/re");
    assert_eq!(
        listed(&composer, cx),
        ["review", "pr-review"],
        "names starting with it first"
    );
    cx.simulate_keystrokes("down");
    assert_eq!(cx.update(|_, cx| composer.read(cx).slash_selected), Some(1));
    cx.simulate_keystrokes("up enter");
    assert_eq!(draft(&composer, cx), "/review what to look at");
    let selected = cx.update(|_, cx| composer.read(cx).draft.read(cx).selected_range());
    assert_eq!(
        selected,
        8..23,
        "the hint is selected, so typing replaces it"
    );
    assert!(listed(&composer, cx).is_empty());
    assert!(client.calls().is_empty(), "a pick sends nothing");
}

// Pins: g0-session#26
#[gpui::test]
fn escape_hides_the_list_until_the_draft_changes(cx: &mut TestAppContext) {
    let (composer, _, cx) = on_s1(cx);
    cx.simulate_input("/");
    assert_eq!(listed(&composer, cx), ["review", "compact", "pr-review"]);
    cx.simulate_keystrokes("escape");
    assert!(listed(&composer, cx).is_empty());
    cx.simulate_input("c");
    assert_eq!(listed(&composer, cx), ["compact"]);
    cx.simulate_input(" now");
    assert!(
        listed(&composer, cx).is_empty(),
        "it closes once arguments begin"
    );
}

// Pins: g0-session#26
#[gpui::test]
fn enter_picks_the_last_row_when_the_list_got_shorter_under_the_lit_one(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    cx.simulate_input("/");
    cx.simulate_keystrokes("down down");
    let mut fewer = config("sonnet");
    fewer["commands"] = json!([{ "name": "compact", "description": "Compact the context" }]);
    client.set_config(Some(fewer));
    composer.update(cx, |composer, cx| {
        composer.set_running(Some("r1".into()), cx);
        composer.set_running(None, cx);
    });
    cx.run_until_parked();
    assert_eq!(listed(&composer, cx), ["compact"]);
    cx.simulate_keystrokes("enter");
    assert_eq!(draft(&composer, cx), "/compact ");
}

// Pins: g0-session#26
#[gpui::test]
fn the_lit_row_scrolls_into_sight_when_the_keys_move_past_the_list(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    let mut many = config("sonnet");
    many["commands"] = (0..40)
        .map(|at| json!({ "name": format!("c{at:02}"), "description": "A command" }))
        .collect();
    client.set_config(Some(many));
    composer.update(cx, |composer, cx| {
        composer.set_running(Some("r1".into()), cx);
        composer.set_running(None, cx);
    });
    cx.run_until_parked();
    cx.simulate_input("/");
    cx.run_until_parked();
    let offset =
        |cx: &mut VisualTestContext| cx.update(|_, cx| composer.read(cx).slash_scroll.offset().y);
    assert_eq!(offset(cx), px(0.));
    cx.simulate_keystrokes("up");
    cx.run_until_parked();
    assert!(
        offset(cx) < px(0.),
        "up from the first row wraps to the last, scrolled to"
    );
    cx.simulate_keystrokes("down");
    cx.run_until_parked();
    assert!(
        offset(cx) > px(-10.),
        "and back to the first row, just under the list's padding"
    );
}

// Pins: g0-session#27
#[gpui::test]
fn a_read_sent_while_a_pick_is_going_doesnt_put_the_old_value_back(cx: &mut TestAppContext) {
    let (composer, client, cx) = on_s1(cx);
    composer.update(cx, |composer, cx| {
        client.set_config(Some(config("opus")));
        composer.choose("model".into(), "opus".into(), cx);
        // A turn ends while the pick is going; the read answers from before it.
        client.set_config(Some(config("sonnet")));
        composer.set_running(Some("r1".into()), cx);
        composer.set_running(None, cx);
    });
    cx.run_until_parked();
    assert_eq!(client.config_calls().len(), 3, "read, pick, read");
    assert_eq!(menus(&composer, cx)[0].1, "opus");
}

// Deliberately held config calls let a previous visit answer after A → B → A.
// Writes keep running across switches; dropped reads must cancel instead.
#[derive(Clone, Default)]
struct DelayedConfigClient {
    messages: crate::features::session::client::fake_client::FakeClient,
    calls: std::rc::Rc<std::cell::RefCell<std::collections::VecDeque<ConfigCall>>>,
}

struct ConfigCall {
    args: Value,
    method: String,
    session: String,
    answer: futures_channel::oneshot::Sender<
        Result<Value, crate::features::session::client::session_client::CallError>,
    >,
}

impl DelayedConfigClient {
    fn take(&self, method: &str, session: &str) -> ConfigCall {
        let call = self.calls.borrow_mut().pop_front().expect("config call");
        assert_eq!(
            (call.method.as_str(), call.session.as_str()),
            (method, session)
        );
        call
    }
}

impl crate::features::session::client::session_client::SessionClient for DelayedConfigClient {
    fn subscribe_rows(
        &self,
        _: &str,
        _: Option<i64>,
        _: Option<&crate::core::socket::row_source::ExpectedRowSource>,
        _: crate::features::session::client::session_client::RowsSink,
        _: &gpui_kit::App,
    ) -> gpui_kit::Subscription {
        gpui_kit::Subscription::new(|| {})
    }

    fn subscribe_message(
        &self,
        message: &str,
        machine: Option<&str>,
        sink: crate::features::session::client::session_client::MessageSink,
        cx: &gpui_kit::App,
    ) -> gpui_kit::Subscription {
        crate::features::session::client::session_client::SessionClient::subscribe_message(
            &self.messages,
            message,
            machine,
            sink,
            cx,
        )
    }

    fn clock(
        &self,
        _: crate::features::session::client::session_client::ClockSink,
        _: &mut gpui_kit::App,
    ) -> gpui_kit::Subscription {
        gpui_kit::Subscription::new(|| {})
    }

    fn call(
        &self,
        feature: &str,
        method: &str,
        args: Value,
        _: Option<&str>,
        cx: &gpui_kit::App,
    ) -> gpui_kit::Task<Result<Value, crate::features::session::client::session_client::CallError>>
    {
        use crate::features::session::client::session_client::CallError;
        assert!(matches!(feature, "config" | "machines"));
        let (answer, receiver) = futures_channel::oneshot::channel();
        self.calls.borrow_mut().push_back(ConfigCall {
            method: method.into(),
            session: args["session"].as_str().unwrap_or_default().into(),
            args,
            answer,
        });
        cx.foreground_executor()
            .spawn(async move { receiver.await.unwrap_or(Err(CallError::Aborted)) })
    }
}

fn delayed_on_s1(
    cx: &mut TestAppContext,
) -> (
    Entity<Composer>,
    DelayedConfigClient,
    &mut VisualTestContext,
) {
    cx.update(gpui_kit::init);
    let client = DelayedConfigClient::default();
    let held = client.clone();
    let (composer, cx) =
        cx.add_window_view(move |window, cx| Composer::new(std::rc::Rc::new(held), window, cx));
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx);
    });
    client
        .take("get", "s1")
        .answer
        .send(Ok(config("sonnet")))
        .unwrap();
    cx.run_until_parked();
    (composer, client, cx)
}

fn leave_and_return(
    composer: &Entity<Composer>,
    client: &DelayedConfigClient,
    cx: &mut VisualTestContext,
) -> ConfigCall {
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s2".into()), window, cx);
        composer.set_session(Some("s1".into()), window, cx);
    });
    let abandoned = client.take("get", "s2");
    cx.run_until_parked();
    assert!(
        abandoned.answer.send(Ok(config("opus"))).is_err(),
        "the intervening read was cancelled"
    );
    client.take("get", "s1")
}

#[gpui::test]
fn returning_to_a_session_ignores_its_previous_visits_pick_answer(cx: &mut TestAppContext) {
    let (composer, client, cx) = delayed_on_s1(cx);
    composer.update(cx, |composer, cx| {
        composer.choose("model".into(), "opus".into(), cx)
    });
    let old_pick = client.take("set", "s1");
    let current_read = leave_and_return(&composer, &client, cx);
    current_read.answer.send(Ok(config("sonnet"))).unwrap();
    cx.run_until_parked();
    old_pick.answer.send(Ok(config("opus"))).unwrap();
    cx.run_until_parked();
    assert_eq!(
        menus(&composer, cx)[0].1,
        "sonnet",
        "a previous visit cannot replace current config"
    );
}

#[gpui::test]
fn returning_to_a_session_ignores_its_previous_visits_pick_error(cx: &mut TestAppContext) {
    use crate::features::session::client::session_client::CallError;
    let (composer, client, cx) = delayed_on_s1(cx);
    composer.update(cx, |composer, cx| {
        composer.choose("model".into(), "opus".into(), cx)
    });
    let old_pick = client.take("set", "s1");
    let current_read = leave_and_return(&composer, &client, cx);
    current_read.answer.send(Ok(config("sonnet"))).unwrap();
    cx.run_until_parked();
    old_pick
        .answer
        .send(Err(CallError::Failed("old visit failed".into())))
        .unwrap();
    cx.run_until_parked();
    assert!(
        cx.update(|_, cx| composer.read(cx).error.is_none()),
        "a previous visit cannot show an error here"
    );
    assert_eq!(menus(&composer, cx)[0].1, "sonnet");
}

#[gpui::test]
fn an_obsolete_pick_answer_does_not_invalidate_the_return_visits_pending_read(
    cx: &mut TestAppContext,
) {
    let (composer, client, cx) = delayed_on_s1(cx);
    composer.update(cx, |composer, cx| {
        composer.choose("model".into(), "opus".into(), cx)
    });
    let old_pick = client.take("set", "s1");
    let current_read = leave_and_return(&composer, &client, cx);
    old_pick.answer.send(Ok(config("opus"))).unwrap();
    cx.run_until_parked();
    current_read.answer.send(Ok(config("sonnet"))).unwrap();
    cx.run_until_parked();
    assert_eq!(
        menus(&composer, cx)[0].1,
        "sonnet",
        "the old pick must not stale the current read"
    );
}

#[gpui::test]
fn returning_to_a_session_cancels_its_previous_visits_read(cx: &mut TestAppContext) {
    let (composer, client, cx) = delayed_on_s1(cx);
    composer.update(cx, |composer, cx| composer.read_config(cx));
    let old_read = client.take("get", "s1");
    let current_read = leave_and_return(&composer, &client, cx);
    assert!(
        old_read.answer.send(Ok(config("opus"))).is_err(),
        "the old read must be cancelled"
    );
    current_read.answer.send(Ok(config("sonnet"))).unwrap();
    cx.run_until_parked();
    assert_eq!(menus(&composer, cx)[0].1, "sonnet");
}

#[gpui::test]
fn only_models_stay_inline_while_effort_and_other_provider_selects_use_settings(
    cx: &mut TestAppContext,
) {
    use crate::features::session::composer::config_menu::inline;
    let (composer, client, cx) = on_s1(cx);
    let mut offered = config("sonnet");
    // Providers can choose their own model option ID; category still identifies it.
    offered["options"][0]["id"] = json!("provider-model");
    offered["options"][0]["name"] = json!("Provider model 🦀");
    offered["options"][1]["name"] = json!("Reasoning effort");
    offered["choices"] = json!({ "provider-model": "sonnet", "effort": "high", "access": "ask" });
    offered["options"].as_array_mut().unwrap().push(json!({
        "id": "access", "name": "Provider access", "type": "select", "currentValue": "full",
        "options": [{ "value": "ask", "name": "Ask first" }, { "value": "full", "name": "Full access" }]
    }));
    client.set_config(Some(offered));
    composer.update(cx, |composer, cx| composer.read_config(cx));
    cx.run_until_parked();
    let (models, settings): (Vec<_>, Vec<_>) = cx.update(|_, cx| {
        composer
            .read(cx)
            .menus()
            .into_iter()
            .map(|(option, current)| {
                (
                    inline(option),
                    option.id.clone(),
                    option.name.clone(),
                    current.to_string(),
                )
            })
            .partition(|(inline, _, _, _)| *inline)
    });
    assert_eq!(
        models,
        [(
            true,
            "provider-model".into(),
            "Provider model 🦀".into(),
            "sonnet".into()
        )]
    );
    assert_eq!(
        settings,
        [
            (
                false,
                "effort".into(),
                "Reasoning effort".into(),
                "high".into()
            ),
            (
                false,
                "access".into(),
                "Provider access".into(),
                "full".into()
            ),
        ]
    );
    let mut legacy_model = cx.update(|_, cx| {
        composer
            .read(cx)
            .config
            .as_ref()
            .unwrap()
            .options
            .as_ref()
            .unwrap()[0]
            .clone()
    });
    legacy_model.id = "model".into();
    legacy_model.category = None;
    assert!(
        inline(&legacy_model),
        "the actual model ID also stays inline without a category"
    );
}

#[path = "composer_config_tests/warm.rs"]
mod warm;
