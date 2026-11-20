//! A local session's unsent draft comes back with its open and is saved as it changes (the old app's drafts).

use gpui::TestAppContext;
use gpui_kit::{AppContext as _, Entity, VisualTestContext};
use serde_json::{Value, json};

use super::Composer;
use super::draft_saves::SavedDraft;
use super::tests::{draft, open};
use crate::core::engine::SessionKey;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::client::session_client::CallError;

fn owned(id: &str) -> SessionKey {
    SessionKey {
        machine: "owner".into(),
        id: id.into(),
    }
}

fn saved(text: &str) -> Option<SavedDraft> {
    Some(SavedDraft {
        text: text.into(),
        attachments: Vec::new(),
    })
}

fn local_source() -> ExpectedRowSource {
    ExpectedRowSource {
        machine: "owner".into(),
        generation: 0,
        route_machine: None,
    }
}

/// An open landing on `id` with the saved draft its answer carried.
fn land(
    composer: &Entity<Composer>,
    id: &str,
    draft: Option<SavedDraft>,
    cx: &mut VisualTestContext,
) {
    composer.update_in(cx, |composer, window, cx| {
        composer.offer_saved(&owned(id), draft, local_source());
        composer.set_session(Some(id.into()), window, cx)
    });
}

fn type_text(composer: &Entity<Composer>, text: &str, cx: &mut VisualTestContext) {
    composer.update_in(cx, |composer, window, cx| {
        composer.type_text(text, window, cx)
    });
}

/// Each `drafts/set` sent, as (session, text).
fn saves(client: &FakeClient) -> Vec<(String, String)> {
    let text = |args: &Value, key: &str| args[key].as_str().unwrap_or_default().to_string();
    (client.calls().into_iter())
        .filter(|(call, _)| call == "drafts/set")
        .map(|(_, args)| (text(&args, "session"), text(&args, "text")))
        .collect()
}

#[gpui::test]
fn switching_sessions_and_back_shows_each_sessions_saved_draft(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    land(&composer, "s1", saved("one, saved"), cx);
    assert_eq!(
        draft(&composer, cx),
        "one, saved",
        "the open brings s1's saved draft"
    );
    land(&composer, "s2", saved("two, saved"), cx);
    assert_eq!(draft(&composer, cx), "two, saved");
    type_text(&composer, "two, edited", cx);
    land(&composer, "s1", saved("one, as read again"), cx);
    assert_eq!(
        draft(&composer, cx),
        "one, saved",
        "this run's draft stands; a later open's copy is no newer"
    );
    land(&composer, "s2", saved("two, as read again"), cx);
    assert_eq!(draft(&composer, cx), "two, edited");
    assert_eq!(
        saves(&client).last(),
        Some(&("s2".to_string(), "two, edited".to_string())),
        "the edit was saved for s2"
    );
}

#[gpui::test]
fn text_typed_while_a_picked_session_opens_joins_its_saved_draft(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    land(&composer, "s1", saved(""), cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.park("s2".into(), None, window, cx)
    });
    type_text(&composer, "xyz", cx);
    assert!(
        saves(&client).iter().all(|(session, _)| session != "s2"),
        "nothing is saved for s2 before its saved draft has come"
    );
    land(&composer, "s2", saved("saved"), cx);
    assert_eq!(
        draft(&composer, cx),
        "saved\nxyz",
        "typed text goes after the saved draft"
    );
    assert_eq!(
        saves(&client).last(),
        Some(&("s2".to_string(), "saved\nxyz".to_string())),
        "and the joined draft is what s2 saves"
    );
}

#[gpui::test]
fn one_save_goes_at_a_time_and_the_newest_text_follows_it(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    land(&composer, "s1", saved(""), cx);
    let first = client.queue_pending();
    type_text(&composer, "a", cx);
    type_text(&composer, "ab", cx);
    type_text(&composer, "abc", cx);
    assert_eq!(saves(&client), [("s1".to_string(), "a".to_string())]);
    let finished = composer.update(cx, |composer, _| composer.saves_finished());
    assert!(
        finished.is_some(),
        "a window closing now waits for the saves"
    );
    client.queue_answer(Ok(json!({ "ok": true })));
    first.send(Ok(json!({ "ok": true }))).unwrap();
    cx.run_until_parked();
    assert_eq!(
        saves(&client),
        [
            ("s1".to_string(), "a".to_string()),
            ("s1".to_string(), "abc".to_string())
        ],
        "only the newest text follows"
    );
    assert!(
        composer
            .update(cx, |composer, _| composer.saves_finished())
            .is_none()
    );
}

// An older remote's open carries no draft field: the box opens empty and nothing is saved.
#[gpui::test]
fn a_remote_sessions_open_brings_no_saved_draft_and_it_saves_nothing(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    land(&composer, "s1", None, cx);
    type_text(&composer, "kept in memory", cx);
    assert!(saves(&client).is_empty());
}

#[gpui::test]
fn retrying_a_failed_prompt_keeps_a_different_draft_saved_after_the_retrys_send(
    cx: &mut TestAppContext,
) {
    let (composer, client, _, cx) = open(cx);
    land(&composer, "s1", saved(""), cx);
    type_text(&composer, "first try", cx);
    // The send saves the emptied box first, then the send itself fails.
    client.queue_answer(Err(CallError::Aborted));
    client.queue_answer(Err(CallError::Aborted));
    composer.update_in(cx, |composer, window, cx| composer.send(window, cx));
    cx.run_until_parked();
    let client_id = (client.calls().into_iter())
        .find(|(call, _)| call == "send/send")
        .and_then(|(_, args)| args["clientId"].as_str().map(str::to_string))
        .expect("the first send");
    type_text(&composer, "a different draft", cx);
    client.queue_owner_accepted();
    composer.update_in(cx, |composer, window, cx| {
        composer.retry_pending(&client_id, window, cx)
    });
    cx.run_until_parked();
    let calls = client.calls();
    let retry = calls.iter().rposition(|(call, _)| call == "send/send");
    let last_save = calls.iter().rposition(|(call, _)| call == "drafts/set");
    assert!(
        last_save > retry,
        "the kept draft is saved after the retry, whose accept clears the saved draft"
    );
    let saved_text = calls[last_save.unwrap()].1["text"]
        .as_str()
        .unwrap()
        .to_string();
    assert_eq!(saved_text, "a different draft");
    // A new composer, as after a restart, opening s1 with the copy that was saved.
    let fresh = client.clone();
    let reopened =
        cx.update(|window, cx| cx.new(|cx| Composer::new(std::rc::Rc::new(fresh), window, cx)));
    land(&reopened, "s1", saved(&saved_text), cx);
    assert_eq!(
        draft(&reopened, cx),
        "a different draft",
        "and comes back from the saved copy"
    );
}

#[gpui::test]
fn a_remote_sessions_draft_comes_with_its_open_and_saves_through_its_source(
    cx: &mut TestAppContext,
) {
    let (composer, client, _, cx) = open(cx);
    let key = SessionKey {
        machine: "workbox-engine".into(),
        id: "r1".into(),
    };
    let source = ExpectedRowSource {
        machine: "workbox-engine".into(),
        generation: 3,
        route_machine: Some("workbox-test".into()),
    };
    composer.update_in(cx, |composer, window, cx| {
        composer.offer_saved(&key, saved("kept on the remote"), source.clone());
        composer.set_target(Some(key.clone()), Some(source.clone()), window, cx)
    });
    assert_eq!(draft(&composer, cx), "kept on the remote");
    type_text(&composer, "kept on the remote, edited", cx);
    let (_, args) = (client.calls().into_iter())
        .rfind(|(call, _)| call == "drafts/set")
        .expect("a save");
    assert_eq!(args["session"], json!("r1"));
    assert_eq!(args["text"], json!("kept on the remote, edited"));
    assert_eq!(
        args["expectedSource"],
        json!(source),
        "it goes where the session runs"
    );
}

// The old app's ConversationSurface.svelte:494: a send that fails puts its text back and saves it, so the emptied
// copy its send saved first doesn't stand in for it after a restart.
#[gpui::test]
fn a_failed_send_saves_the_text_it_puts_back_for_a_local_and_a_remote_session(
    cx: &mut TestAppContext,
) {
    let (composer, client, _, cx) = open(cx);
    let remote = ExpectedRowSource {
        machine: "workbox-engine".into(),
        generation: 3,
        route_machine: Some("workbox-test".into()),
    };
    let sessions = [
        (owned("s1"), local_source()),
        (
            SessionKey {
                machine: "workbox-engine".into(),
                id: "r1".into(),
            },
            remote,
        ),
    ];
    for (key, source) in sessions {
        composer.update_in(cx, |composer, window, cx| {
            composer.offer_saved(&key, saved(""), source.clone());
            composer.set_target(Some(key.clone()), Some(source.clone()), window, cx)
        });
        type_text(&composer, "not sent yet", cx);
        // The emptied box is saved, then the send fails.
        client.queue_answer(Ok(json!({ "ok": true })));
        client.queue_answer(Err(CallError::Aborted));
        composer.update_in(cx, |composer, window, cx| composer.send(window, cx));
        cx.run_until_parked();
        assert_eq!(draft(&composer, cx), "not sent yet", "the text is back");
        let (_, args) = (client.calls().into_iter())
            .rfind(|(call, _)| call == "drafts/set")
            .expect("a save");
        assert_eq!(args["session"], json!(key.id));
        assert_eq!(
            args["text"],
            json!("not sent yet"),
            "and saved after the emptied copy"
        );
        assert_eq!(args["expectedSource"], json!(source));
    }
}
