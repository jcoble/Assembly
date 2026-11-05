//! The session pane publishing the selected session's live facts for the Context tab, on the fake client. Declared
//! from session_pane.rs so it can open sessions the way the pane's own tests do.

use std::cell::Cell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::{Entity, Subscription, VisualTestContext};
use serde_json::{Value, json};

use super::SessionPane;
use super::tests::{jump, live, page, row, show, user};
use crate::core::engine::SessionKey;
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::context::live_facts::LiveFacts;
use crate::features::session::model::session_view::ContextReport;

fn open(cx: &mut TestAppContext) -> (Entity<SessionPane>, FakeClient, &mut VisualTestContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        LiveFacts::init(cx);
    });
    let client = FakeClient::default();
    let fake = client.clone();
    let (pane, cx) =
        cx.add_window_view(move |window, cx| SessionPane::new(Rc::new(fake), window, cx));
    (pane, client, cx)
}

fn select(session: &str, cx: &mut VisualTestContext) {
    cx.update(|_, cx| {
        SelectedSession::global(cx)
            .update(cx, |selected, cx| selected.select(Some(session.into()), cx))
    });
    cx.run_until_parked();
}

fn facts(cx: &mut VisualTestContext) -> Option<LiveFacts> {
    cx.update(|_, cx| LiveFacts::try_global(cx).unwrap().read(cx).clone())
}

/// How many times the value notifies from now on, while the subscription is held.
fn notifications(cx: &mut VisualTestContext) -> (Rc<Cell<usize>>, Subscription) {
    let count = Rc::new(Cell::new(0));
    let counted = count.clone();
    let heard = cx.update(|_, cx| {
        let global = LiveFacts::try_global(cx).unwrap();
        cx.observe(&global, move |_, _| counted.set(counted.get() + 1))
    });
    (count, heard)
}

fn stats(seq: i64, run: &str, started: i64, input: i64, output: i64) -> Value {
    row(
        seq,
        "stats",
        None,
        json!({ "runId": run, "status": "completed", "startedAt": started, "toolCalls": 0,
                "inputTokens": input, "outputTokens": output, "model": "opus" }),
    )
}

fn context(seq: i64, used: i64) -> Value {
    row(
        seq,
        "context",
        Some("context:r1"),
        json!({ "used": used, "size": 200_000 }),
    )
}

/// The selected session showing, with a prompt, a context report and one ended turn's figures.
fn shown(cx: &mut TestAppContext) -> (Entity<SessionPane>, FakeClient, &mut VisualTestContext) {
    let (pane, client, cx) = open(cx);
    select("s1", cx);
    show(&pane, "s1", cx);
    let rows = vec![
        user(2, "hi"),
        context(3, 50_000),
        stats(4, "r1", 100, 40_000, 10_000),
    ];
    client.queue_answer(Ok(page(rows, false, None)));
    jump(&client, 4, cx);
    (pane, client, cx)
}

#[gpui::test]
fn the_selected_sessions_pane_publishes_its_report_and_stats(cx: &mut TestAppContext) {
    let (_, client, cx) = shown(cx);
    let published = facts(cx).expect("the selected session's facts");
    assert_eq!(published.session, "s1");
    assert_eq!(published.key, None);
    assert_eq!(
        published.context,
        Some(ContextReport {
            seq: 3,
            used: 50_000,
            size: 200_000,
            input_tokens: None,
            output_tokens: None
        })
    );
    assert_eq!(
        (published.input_tokens, published.output_tokens),
        (Some(40_000), Some(10_000))
    );
    assert_eq!(published.model.as_deref(), Some("opus"), "the turn's model");
    assert_eq!(
        (published.newest_prompt_seq, published.turns_ended),
        (Some(2), 1)
    );
    live(&client, user(5, "again"), cx);
    assert_eq!(facts(cx).unwrap().newest_prompt_seq, Some(5), "live");
}

#[gpui::test]
fn a_newer_turn_with_zeros_keeps_the_earlier_figures(cx: &mut TestAppContext) {
    let (_, client, cx) = shown(cx);
    live(&client, stats(5, "r2", 200, 0, 0), cx);
    let published = facts(cx).unwrap();
    assert_eq!(
        (published.input_tokens, published.output_tokens),
        (Some(40_000), Some(10_000))
    );
    assert_eq!(published.turns_ended, 2, "the cancelled turn still ended");
}

#[gpui::test]
fn a_pane_not_on_the_selected_session_publishes_nothing(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    select("s2", cx);
    show(&pane, "s1", cx);
    client.queue_answer(Ok(page(vec![context(3, 50_000)], false, None)));
    jump(&client, 3, cx);
    assert_eq!(facts(cx), None);
    select("s1", cx);
    assert_eq!(facts(cx).unwrap().session, "s1", "selecting it publishes");
    select("s2", cx);
    assert_eq!(facts(cx), None, "selecting another clears its own");
}

#[gpui::test]
fn another_owners_session_with_the_same_id_does_not_clear_it(cx: &mut TestAppContext) {
    let (_, _, cx) = shown(cx);
    let key = |machine: &str| SessionKey {
        machine: machine.into(),
        id: "s1".into(),
    };
    cx.update(|_, cx| {
        let mut owned = LiveFacts::try_global(cx).unwrap().read(cx).clone().unwrap();
        owned.key = Some(key("owner"));
        LiveFacts::publish(owned, cx);
        LiveFacts::clear("s1", Some(&key("remote")), cx);
        LiveFacts::clear("s1", None, cx);
    });
    assert_eq!(facts(cx).unwrap().key, Some(key("owner")));
    cx.update(|_, cx| LiveFacts::clear("s1", Some(&key("owner")), cx));
    assert_eq!(facts(cx), None, "its own clears it");
}

#[gpui::test]
fn a_draft_image_shows(cx: &mut TestAppContext) {
    let (pane, _, cx) = shown(cx);
    let composer = cx.update(|_, cx| pane.read(cx).composer.clone());
    cx.update(|_, cx| {
        composer.update(cx, |composer, cx| {
            composer.attach_saved("shot.png", "/tmp/a/shot.png", cx)
        })
    });
    cx.run_until_parked();
    let drafts = facts(cx).unwrap().drafts;
    assert_eq!(drafts.len(), 1);
    assert_eq!(
        (
            drafts[0].name.as_ref(),
            drafts[0].mime,
            drafts[0].saved_path.as_deref()
        ),
        ("shot.png", Some("image/png"), Some("/tmp/a/shot.png"))
    );
}

#[gpui::test]
fn publishing_the_same_value_does_not_notify(cx: &mut TestAppContext) {
    let (_, _, cx) = shown(cx);
    let (count, _heard) = notifications(cx);
    let same = facts(cx).unwrap();
    cx.update(|_, cx| LiveFacts::publish(same.clone(), cx));
    cx.run_until_parked();
    assert_eq!(count.get(), 0);
    cx.update(|_, cx| {
        LiveFacts::publish(
            LiveFacts {
                turns_ended: 9,
                ..same
            },
            cx,
        )
    });
    cx.run_until_parked();
    assert_eq!(count.get(), 1, "a real change notifies");
}

#[gpui::test]
fn typing_in_the_composer_leaves_the_value_alone(cx: &mut TestAppContext) {
    let (pane, _, cx) = shown(cx);
    let before = facts(cx);
    let (count, _heard) = notifications(cx);
    let composer = cx.update(|_, cx| pane.read(cx).composer.clone());
    for text in ["h", "he", "hel"] {
        composer.update_in(cx, |composer, window, cx| {
            composer.type_text(text, window, cx)
        });
        cx.run_until_parked();
    }
    assert_eq!(facts(cx), before);
    assert_eq!(count.get(), 0);
}
