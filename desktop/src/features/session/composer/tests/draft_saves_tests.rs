//! A local session's unsent draft comes back with its open and is saved as it changes (the old app's drafts).

use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};
use serde_json::{Value, json};

use super::Composer;
use super::draft_saves::SavedDraft;
use super::tests::{draft, open};
use crate::core::engine::SessionKey;
use crate::features::session::client::fake_client::FakeClient;

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

/// An open landing on `id` with the saved draft its answer carried.
fn land(
    composer: &Entity<Composer>,
    id: &str,
    draft: Option<SavedDraft>,
    cx: &mut VisualTestContext,
) {
    composer.update_in(cx, |composer, window, cx| {
        composer.offer_saved(&owned(id), draft);
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

#[gpui::test]
fn a_remote_sessions_open_brings_no_saved_draft_and_it_saves_nothing(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    land(&composer, "s1", None, cx);
    type_text(&composer, "kept in memory", cx);
    assert!(saves(&client).is_empty());
}
