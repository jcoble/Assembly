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
                ComposerEvent::Sending => "sending".to_string(),
                ComposerEvent::Sent { session, run_id } => format!("sent {session} {run_id}"),
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

pub(super) fn draft(composer: &Entity<Composer>, cx: &mut VisualTestContext) -> String {
    cx.update(|_, cx| composer.read(cx).draft.read(cx).value().to_string())
}

// Pins: g0-session#22
#[gpui::test]
fn a_send_goes_out_for_the_session_clears_the_draft_and_says_its_run(cx: &mut TestAppContext) {
    let (composer, client, (events, _heard), cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    client.queue_answer(Ok(json!({ "runId": "r7" })));
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
    assert_eq!(*events.borrow(), ["sending", "sent s1 r7"]);
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
    assert_eq!(*events.borrow(), ["sending"]);
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
        [("send/stop".to_string(), json!({ "runId": "r7" }))]
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
    client.queue_answer(Ok(json!({ "runId": "r1" })));
    composer.update_in(cx, |composer, window, cx| composer.send(window, cx));
    cx.run_until_parked();
    client.queue_answer(Ok(json!({ "runId": "r2" })));
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
        client.queue_answer(Ok(json!({ "runId": "r1" })));
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
    client.queue_answer(Ok(json!({ "runId": "r2" })));
    composer.update(cx, |composer, cx| composer.set_offline(None, cx));
    type_and_send(&composer, "hello", cx);
    assert_eq!(client.calls()[0].0, "send/send");
}
