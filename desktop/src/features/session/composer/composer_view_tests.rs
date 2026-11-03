//! The composer on the fake client: a send, and each session's draft.

use std::cell::RefCell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::{Entity, Subscription, VisualTestContext};
use serde_json::{Value, json};

use super::{Composer, ComposerEvent};
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::client::session_client::CallError;

/// What the composer told the pane, and the subscription that hears it.
pub(super) type Events = (Rc<RefCell<Vec<String>>>, Subscription);

pub(super) fn open(
    cx: &mut TestAppContext,
) -> (Entity<Composer>, FakeClient, Events, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let client = FakeClient::default();
    let fake = client.clone();
    let (composer, cx) =
        cx.add_window_view(move |window, cx| Composer::new(Rc::new(fake), window, cx));
    let events = Rc::new(RefCell::new(Vec::new()));
    let seen = events.clone();
    let subscription = cx.update(|_, cx| {
        cx.subscribe(&composer, move |_, event: &ComposerEvent, _| {
            seen.borrow_mut().push(match event {
                ComposerEvent::StartRequested{..} => "start requested".to_string(),
                ComposerEvent::Sending => "sending".to_string(),
                ComposerEvent::Accepted{receipt,..} => format!("queued {}", receipt.session_id),
                ComposerEvent::PendingChanged => "pending".into(),
            })
        })
    });
    (composer, client, (events, subscription), cx)
}

fn type_and_send(composer: &Entity<Composer>, text: &str, cx: &mut VisualTestContext) {
    composer.update_in(cx, |composer, window, cx| {
        composer.draft.update(cx, |draft, cx| {
            draft.set_value(text.to_string(), window, cx)
        });
        composer.send(window, cx);
    });
    cx.run_until_parked();
}

#[gpui::test]
fn reopening_recovers_exact_payload_and_original_nullable_epoch(cx: &mut TestAppContext) {
    use base64::Engine as _;
    let (composer, client, _, cx) = open(cx);
    let digest = "a".repeat(64);
    let attachments = br#"["/box/second.png","/box/first.png"]"#;
    client.queue_engine_answer(Ok(json!({"engineMachine":"owner","items":[{
        "clientId":"original","contentSha256":digest,"textPreview":"exact text",
        "textComplete":true,"textUtf8Bytes":10,"attachmentJsonUtf8Bytes":attachments.len(),
        "targetEpoch":null,"state":"held","version":9}],"nextCreatedMs":null,"nextClientId":null})));
    client.queue_engine_answer(Ok(json!({"contentSha256":digest,"part":"attachments","offset":0,
        "totalBytes":attachments.len(),"data":base64::engine::general_purpose::STANDARD.encode(attachments),"nextOffset":null})));
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    cx.run_until_parked();
    cx.update(|_, cx| {
        let pending = &composer.read(cx).pending("s1")[0];
        assert_eq!(pending.client_id(), "original");
        assert_eq!(pending.text(), "exact text");
        assert_eq!(pending.request.epoch, None);
        assert_eq!(
            pending.request.attachment_ids,
            ["/box/second.png", "/box/first.png"]
        );
        assert_eq!(pending.state, "held");
    });
    assert!(client.calls().is_empty(), "recovery never sends a prompt");
    assert_eq!(client.engine_calls()[1].1["machine"], json!("owner"));
}

#[gpui::test]
fn an_old_run_response_is_rejected_and_preserves_retry_payload(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    client.queue_answer(Ok(json!({"runId":"invented"})));
    type_and_send(&composer, "keep me", cx);
    assert_eq!(draft(&composer, cx), "keep me");
    client.queue_owner_accepted();
    composer.update_in(cx, |composer, window, cx| composer.send(window, cx));
    cx.run_until_parked();
    assert_eq!(client.calls()[0].1, client.calls()[1].1);
}

#[gpui::test]
fn failed_prompt_retry_preserves_original_epoch_and_new_typing(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx);
        composer.retry_ids.insert(
            crate::core::engine::SessionKey{machine:"owner".into(),id:"s1".into()},
            super::PromptRequest {
                text: "original".into(),
                client_id: "stable".into(),
                attachment_ids: vec![],
                epoch: Some(7),
            },
        );
    });
    client.queue_answer(Err(CallError::Aborted));
    type_and_send(&composer, "original", cx);
    composer.update_in(cx, |composer, window, cx| {
        composer
            .draft
            .update(cx, |draft, cx| draft.set_value("new typing", window, cx));
    });
    client.queue_owner_accepted();
    composer.update_in(cx, |composer, window, cx| {
        composer.retry_pending("stable", window, cx)
    });
    cx.run_until_parked();
    assert_eq!(client.calls()[0].1, client.calls()[1].1);
    assert_eq!(client.calls()[1].1["epoch"], json!(7));
    assert_eq!(draft(&composer, cx), "new typing");
    assert_eq!(cx.update(|_, cx| composer.read(cx).pending("s1").len()), 1);
}

pub(super) fn draft(composer: &Entity<Composer>, cx: &mut VisualTestContext) -> String {
    cx.update(|_, cx| composer.read(cx).draft.read(cx).value().to_string())
}

// Pins: g0-session#22
#[gpui::test]
fn a_send_goes_out_for_the_session_clears_the_draft_and_accepts_without_a_run(
    cx: &mut TestAppContext,
) {
    let (composer, client, (events, _heard), cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    client.queue_owner_accepted();
    type_and_send(&composer, " hello \n", cx);
    let calls = client.calls();
    assert_eq!(calls.len(), 1);
    let (call, args) = &calls[0];
    assert_eq!(call, "send/send");
    assert_eq!(
        (&args["session"], &args["text"]),
        (&json!("s1"), &json!("hello"))
    );
    assert!(args["clientId"].as_str().is_some_and(|id| !id.is_empty()));
    assert_eq!(draft(&composer, cx), "");
    assert!(events.borrow().iter().any(|event| event == "queued s1"));
    assert_eq!(args["attachmentIds"], json!([]));
    assert_eq!(args["epoch"], Value::Null);
    assert_eq!(
        cx.update(|_, cx| composer.read(cx).pending("s1")[0].state.clone()),
        "queued"
    );
}

// Pins: g0-session#22
#[gpui::test]
fn an_empty_draft_sends_nothing_and_a_failed_send_puts_the_draft_back(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    type_and_send(&composer, "  ", cx);
    assert!(client.calls().is_empty());
    composer.update_in(cx, |composer, window, cx| {
        composer
            .draft
            .update(cx, |draft, cx| draft.set_value("hello", window, cx));
        composer.send(window, cx);
    });
    assert_eq!(
        draft(&composer, cx),
        "",
        "the draft clears as the prompt goes out"
    );
    cx.run_until_parked();
    assert_eq!(
        draft(&composer, cx),
        "hello",
        "and comes back when it didn't"
    );
}

#[gpui::test]
fn an_unreachable_machine_shows_a_send_error_and_keeps_the_draft(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("kept-remote-session".into()), window, cx)
    });
    client.queue_answer(Err(CallError::Unreachable {
        machine: "Workbox Test".into(),
    }));
    type_and_send(&composer, "Reply OK.", cx);
    assert_eq!(draft(&composer, cx), "Reply OK.");
    assert_eq!(
        cx.update(|_, cx| composer.read(cx).error.clone())
            .as_deref(),
        Some("Couldn't send: Workbox Test is offline. Check Settings > Connections.")
    );
}

// Pins: g0-session#8, g0-session#22
#[gpui::test]
fn a_send_that_fails_after_switching_sessions_goes_back_to_its_own_draft(cx: &mut TestAppContext) {
    let (composer, _, (events, _heard), cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx);
        composer
            .draft
            .update(cx, |draft, cx| draft.set_value("for one", window, cx));
        composer.send(window, cx);
        composer.set_session(Some("s2".into()), window, cx);
    });
    cx.run_until_parked();
    assert_eq!(draft(&composer, cx), "");
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    assert_eq!(draft(&composer, cx), "for one");
    assert!(events.borrow().iter().any(|event| event == "sending"));
}

// Pins: g0-session#8
#[gpui::test]
fn each_session_keeps_its_unsent_draft(cx: &mut TestAppContext) {
    let (composer, _, _, cx) = open(cx);
    let set = |session: &str, composer: &Entity<Composer>, cx: &mut VisualTestContext| {
        composer.update_in(cx, |composer, window, cx| {
            composer.set_session(Some(session.into()), window, cx)
        });
    };
    set("s1", &composer, cx);
    composer.update_in(cx, |composer, window, cx| {
        composer
            .draft
            .update(cx, |draft, cx| draft.set_value("for one", window, cx))
    });
    set("s2", &composer, cx);
    assert_eq!(draft(&composer, cx), "");
    set("s1", &composer, cx);
    assert_eq!(draft(&composer, cx), "for one");
}

// Pins: g0-session#22
#[gpui::test]
fn stop_stops_the_running_turn_and_does_nothing_while_idle(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx);
        composer.stop(cx);
    });
    cx.run_until_parked();
    assert!(client.calls().is_empty(), "nothing to stop while idle");
    client.queue_answer(Ok(json!({ "ok": true })));
    composer.update(cx, |composer, cx| {
        composer.set_running(Some("r7".into()), cx);
        composer.stop(cx);
    });
    cx.run_until_parked();
    assert_eq!(
        client.calls(),
        [("send/stop".to_string(), json!({ "runId": "r7","session":"s1","expectedSource":{"machine":"owner","generation":0,"routeMachine":null} }))]
    );
}

// Pins: g0-session#22
#[gpui::test]
fn a_retried_send_reuses_its_client_id_and_a_new_one_gets_its_own(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    // The socket closed before it answered: it may have landed.
    client.queue_answer(Err(CallError::Aborted));
    type_and_send(&composer, "hello", cx);
    assert_eq!(
        draft(&composer, cx),
        "hello",
        "the unanswered send comes back"
    );
    client.queue_owner_accepted();
    composer.update_in(cx, |composer, window, cx| composer.send(window, cx));
    cx.run_until_parked();
    client.queue_owner_accepted();
    type_and_send(&composer, "hello", cx);
    let ids: Vec<Value> = client
        .calls()
        .into_iter()
        .map(|(_, args)| args["clientId"].clone())
        .collect();
    assert_eq!(ids[0], ids[1], "the retry names the same message");
    assert_ne!(
        ids[1], ids[2],
        "a later send of the same words is a new message"
    );
}

// Pins: g0-session#22
#[gpui::test]
fn finished_sends_are_let_go(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    for text in ["one", "two", "three"] {
        client.queue_owner_accepted();
        type_and_send(&composer, text, cx);
    }
    assert_eq!(cx.update(|_, cx| composer.read(cx)._tasks.len()), 1);
}

// Pins: sessions-list#11
#[gpui::test]
fn while_its_machine_is_offline_nothing_is_sent_or_stopped(cx: &mut TestAppContext) {
    let (composer, client, _events, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx);
        composer.set_running(Some("r1".into()), cx);
        composer.set_offline(Some("box".into()), cx);
        composer.stop(cx);
    });
    type_and_send(&composer, "hello", cx);
    assert!(client.calls().is_empty(), "{:?}", client.calls());
    assert_eq!(
        draft(&composer, cx),
        "hello",
        "the draft waits for the machine"
    );
    client.queue_owner_accepted();
    composer.update(cx, |composer, cx| composer.set_offline(None, cx));
    type_and_send(&composer, "hello", cx);
    assert_eq!(client.calls()[0].0, "send/send");
}

// Pins: sessions-list#12
#[gpui::test]
fn connect_asks_the_backend_to_reach_the_offline_machine_once_at_a_time(cx: &mut TestAppContext) {
    let (composer, client, _events, cx) = open(cx);
    composer.update(cx, |composer, cx| {
        composer.connect(cx);
        composer.set_offline(Some("box".into()), cx);
        composer.connect(cx);
        composer.connect(cx);
    });
    assert!(cx.update(|_, cx| composer.read(cx).connecting.is_some()));
    cx.run_until_parked();
    assert_eq!(
        client.calls(),
        [("machines/connect".to_string(), json!({ "machine": "box" }))],
        "nothing while online, and one while it goes"
    );
    assert!(
        cx.update(|_, cx| composer.read(cx).still_offline),
        "it didn't answer, and the line says so"
    );
    composer.update(cx, |composer, cx| composer.set_offline(None, cx));
    assert!(!cx.update(|_, cx| composer.read(cx).still_offline));
}

// Pins: g0-session#23
#[test]
fn the_draft_asks_for_follow_up_changes_once_there_are_messages() {
    assert_eq!(super::placeholder(false), "Message");
    assert_eq!(super::placeholder(true), "Ask for follow-up changes…");
}

#[gpui::test]
fn queued_prompt_consumes_pushed_outcome_and_releases_subscription_on_switch(
    cx: &mut TestAppContext,
) {
    let (composer, client, _, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s".into()), window, cx)
    });
    client.queue_owner_accepted();
    type_and_send(&composer, "original draft", cx);
    let sent = client
        .calls()
        .into_iter()
        .find(|(method, _)| method == "send/send")
        .unwrap()
        .1;
    let client_id = sent["clientId"].as_str().unwrap();
    let message = format!("prompt/s/{client_id}");
    assert_eq!(client.message_subscriptions(), 1);
    cx.update(|_, cx| {
        client.push_message(
            &message,
            Some("owner"),
            json!({"machine":"owner", "entityKind":"message", "entityId":message,
        "version":3, "data":{"sessionId":"s", "state":"held", "reason":"needs_me"}}),
            cx,
        )
    });
    composer.read_with(cx, |composer, _| {
        assert_eq!(composer.pending("s")[0].state, "held: needs_me");
        assert_eq!(composer.pending("s")[0].text(), "original draft");
    });
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("other".into()), window, cx)
    });
    assert_eq!(client.message_subscriptions(), 0);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s".into()), window, cx)
    });
    assert_eq!(client.message_subscriptions(), 1);
    cx.update(|_, cx| {
        client.push_message(
            &message,
            Some("owner"),
            json!({"machine":"owner", "entityKind":"message", "entityId":message,
        "version":4, "data":{"sessionId":"s", "state":"failed", "reason":"model_unavailable"}}),
            cx,
        )
    });
    composer.read_with(cx, |composer, _| {
        assert_eq!(composer.pending("s")[0].state, "failed: model_unavailable");
        assert_eq!(composer.pending("s")[0].request.epoch, None);
    });
    assert_eq!(client.message_subscriptions(), 0);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("other".into()), window, cx)
    });
    assert_eq!(client.message_subscriptions(), 0);
    // Pushed outcomes change display state without re-reading the immutable body.
    assert!(
        client
            .engine_calls()
            .iter()
            .all(|(method, _)| method != "owner_prompt_chunk")
    );
}
