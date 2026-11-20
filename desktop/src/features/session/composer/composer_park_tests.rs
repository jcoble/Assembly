//! A picked session's composer while its open is still on the way: what is typed is the picked session's draft and
//! never sends to the one left; an open that doesn't land gives the session on screen back.

use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};
use serde_json::json;

use super::Composer;
use super::tests::{draft, open, type_and_send};

fn owned(id: &str) -> crate::core::engine::SessionKey {
    crate::core::engine::SessionKey {
        machine: "owner".into(),
        id: id.into(),
    }
}

fn type_in(composer: &Entity<Composer>, text: &str, cx: &mut VisualTestContext) {
    composer.update_in(cx, |composer, window, cx| {
        composer.draft.update(cx, |draft, cx| {
            draft.set_value(text.to_string(), window, cx)
        })
    });
}

#[gpui::test]
fn text_typed_while_a_picked_session_opens_is_its_draft_and_never_sends_to_the_one_left(
    cx: &mut TestAppContext,
) {
    let (composer, client, _, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    type_in(&composer, "for one", cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.park("s2".into(), Some(owned("s2")), window, cx)
    });
    assert_eq!(
        draft(&composer, cx),
        "",
        "the picked session's draft is on screen at once"
    );
    type_and_send(&composer, "for two", cx);
    assert!(
        client.calls().is_empty(),
        "nothing sends before the open lands"
    );
    assert_eq!(
        draft(&composer, cx),
        "for two",
        "and the press keeps the text"
    );
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s2".into()), window, cx)
    });
    assert_eq!(
        draft(&composer, cx),
        "for two",
        "the open lands on what was typed"
    );
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    assert_eq!(
        draft(&composer, cx),
        "for one",
        "the session left keeps only its own"
    );
}

#[gpui::test]
fn an_open_that_does_not_land_gives_the_session_on_screen_its_draft_back(cx: &mut TestAppContext) {
    let (composer, client, _, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    type_in(&composer, "for one", cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.park("s2".into(), Some(owned("s2")), window, cx)
    });
    type_in(&composer, "for two", cx);
    composer.update_in(cx, |composer, window, cx| composer.unpark(window, cx));
    assert_eq!(draft(&composer, cx), "for one");
    client.queue_owner_accepted();
    composer.update_in(cx, |composer, window, cx| composer.send(window, cx));
    cx.run_until_parked();
    let calls = client.calls();
    assert_eq!(calls.len(), 1, "the session on screen sends again");
    assert_eq!(
        (&calls[0].1["session"], &calls[0].1["text"]),
        (&json!("s1"), &json!("for one"))
    );
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s2".into()), window, cx)
    });
    assert_eq!(
        draft(&composer, cx),
        "for two",
        "what was typed for the pick stays its draft"
    );
}

/// A session `id` on owner `machine`, and the source its rows come from.
fn on(
    machine: &str,
    id: &str,
) -> (
    crate::core::engine::SessionKey,
    crate::core::socket::row_source::ExpectedRowSource,
) {
    let key = crate::core::engine::SessionKey {
        machine: machine.into(),
        id: id.into(),
    };
    let source = crate::core::socket::row_source::ExpectedRowSource {
        machine: machine.into(),
        generation: 0,
        route_machine: None,
    };
    (key, source)
}

fn bind(composer: &Entity<Composer>, machine: &str, id: &str, cx: &mut VisualTestContext) {
    let (key, source) = on(machine, id);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_target(Some(key), Some(source), window, cx)
    });
}

#[gpui::test]
fn a_rail_pick_guesses_no_draft_by_id_and_what_is_typed_joins_the_named_owners_draft(
    cx: &mut TestAppContext,
) {
    let (composer, client, _, cx) = open(cx);
    // The same session id on two owners, each with its own draft.
    bind(&composer, "a", "same", cx);
    type_in(&composer, "alpha", cx);
    bind(&composer, "b", "same", cx);
    type_in(&composer, "beta", cx);
    bind(&composer, "owner", "s1", cx);
    type_in(&composer, "for one", cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.park("same".into(), None, window, cx)
    });
    assert_eq!(draft(&composer, cx), "", "neither owner's draft is guessed");
    type_in(&composer, "more", cx);
    composer.update_in(cx, |composer, window, cx| composer.send(window, cx));
    assert!(client.calls().is_empty());
    composer.update(cx, |composer, _| composer.parked_owner(on("b", "same").0));
    bind(&composer, "b", "same", cx);
    assert_eq!(
        draft(&composer, cx),
        "beta\nmore",
        "typed joins the named owner's draft"
    );
    bind(&composer, "a", "same", cx);
    assert_eq!(
        draft(&composer, cx),
        "alpha",
        "the other owner's draft is untouched"
    );
    bind(&composer, "owner", "s1", cx);
    assert_eq!(draft(&composer, cx), "for one");
}

#[gpui::test]
fn a_send_that_fails_while_another_session_opens_goes_back_to_its_own_draft(
    cx: &mut TestAppContext,
) {
    let (composer, _, _, cx) = open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx);
        composer
            .draft
            .update(cx, |draft, cx| draft.set_value("for one", window, cx));
        composer.send(window, cx);
        composer.park("s2".into(), Some(owned("s2")), window, cx);
    });
    cx.run_until_parked();
    assert_eq!(
        draft(&composer, cx),
        "",
        "the picked session's box stays its own"
    );
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s2".into()), window, cx)
    });
    assert_eq!(draft(&composer, cx), "");
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    assert_eq!(draft(&composer, cx), "for one");
}
