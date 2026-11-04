//! Delivery through the test platform. A real Mac's banner is proved in the native acceptance, not here.
use gpui::{SystemNotificationResponse, TestAppContext};
use gpui_kit::Entity;
use serde_json::{Value, json};

use super::{OpenNeed, OwnerNotify, PREVIEW_CHARS, preview, tag};
use crate::core::engine::{EngineFeed, EngineKey, EngineScope};

fn need(machine: &str, id: &str, state: &str, extra: Value) -> Value {
    let mut value = json!({"entityKind":"needs_me","machine":machine,"id":id,"version":1,"sessionId":"s","kind":"question","state":state,"text":format!("Question {id}")});
    value
        .as_object_mut()
        .unwrap()
        .extend(extra.as_object().unwrap().clone());
    value
}

fn start(cx: &mut TestAppContext) -> (Entity<EngineFeed>, Entity<OwnerNotify>) {
    cx.update(|cx| {
        cx.set_app_identity("test.assembly", "Assembly");
        let feed = EngineFeed::global(cx);
        let notify = OwnerNotify::start(feed.clone(), cx);
        (feed, notify)
    })
}

fn cut(feed: &Entity<EngineFeed>, machine: &str, rows: Vec<Value>, cx: &mut TestAppContext) {
    let scope = EngineScope {
        machine: machine.into(),
        topic: "needs_me".into(),
        key: None,
    };
    feed.update(cx, |feed, cx| feed.test_snapshot(scope, rows, cx));
    cx.run_until_parked();
}

/// The machine's settings as its runs topic carries them. With no backend here, the mute read falls back to these.
fn settings(feed: &Entity<EngineFeed>, machine: &str, until: Option<i64>, cx: &mut TestAppContext) {
    let scope = EngineScope {
        machine: machine.into(),
        topic: "runs".into(),
        key: None,
    };
    let row = json!({"entityKind":"workflow_settings","id":"workflow-settings","machine":machine,"version":1,"mutedUntil":until});
    feed.update(cx, |feed, cx| feed.test_snapshot(scope, vec![row], cx));
    cx.run_until_parked();
}

fn key(machine: &str, id: &str) -> EngineKey {
    EngineKey {
        machine: machine.into(),
        kind: "needs_me".into(),
        id: id.into(),
    }
}

#[gpui::test]
async fn what_was_already_open_stays_quiet_and_a_new_question_posts_once(cx: &mut TestAppContext) {
    let (feed, _notify) = start(cx);
    cut(&feed, "m", vec![need("m", "old", "open", json!({}))], cx);
    assert!(
        cx.shown_system_notifications().is_empty(),
        "the first cut is the baseline"
    );
    cut(
        &feed,
        "m",
        vec![
            need("m", "old", "open", json!({})),
            need("m", "new", "open", json!({})),
        ],
        cx,
    );
    let posted = cx.shown_system_notifications();
    assert_eq!(posted.len(), 1);
    assert_eq!(posted[0].tag.as_ref(), tag(&key("m", "new")));
    assert_eq!(posted[0].body.as_ref(), "Question new");
    cut(
        &feed,
        "m",
        vec![
            need("m", "old", "open", json!({})),
            need("m", "new", "open", json!({"version":2})),
        ],
        cx,
    );
    assert_eq!(
        cx.shown_system_notifications().len(),
        1,
        "a repeat doesn't post again"
    );
}

#[gpui::test]
async fn a_question_for_a_parent_session_never_posts(cx: &mut TestAppContext) {
    let (feed, _notify) = start(cx);
    cut(&feed, "m", vec![], cx);
    cut(
        &feed,
        "m",
        vec![need(
            "m",
            "child",
            "open",
            json!({"addresseeSessionId":"parent"}),
        )],
        cx,
    );
    assert!(cx.shown_system_notifications().is_empty());
}

#[test]
fn a_long_preview_stops_at_its_bound_without_cutting_a_character() {
    assert_eq!(preview("Ship\n  it?"), "Ship it?");
    let long = preview(&"é".repeat(PREVIEW_CHARS + 5));
    assert_eq!(long.chars().count(), PREVIEW_CHARS);
    assert!(long.ends_with('…'));
}

#[gpui::test]
async fn answering_takes_its_notification_away(cx: &mut TestAppContext) {
    let (feed, _notify) = start(cx);
    cut(&feed, "m", vec![], cx);
    cut(&feed, "m", vec![need("m", "n", "open", json!({}))], cx);
    assert!(cx.dismissed_system_notifications().is_empty());
    cut(
        &feed,
        "m",
        vec![need("m", "n", "answered", json!({"version":2}))],
        cx,
    );
    assert_eq!(
        cx.dismissed_system_notifications()
            .iter()
            .map(|tag| tag.to_string())
            .collect::<Vec<_>>(),
        [tag(&key("m", "n"))]
    );
}

#[gpui::test]
async fn a_workflow_gate_reaches_the_owner_though_it_names_a_session(cx: &mut TestAppContext) {
    let (feed, _notify) = start(cx);
    cut(&feed, "m", vec![], cx);
    let addressed = json!({"addresseeSessionId":"owner"});
    let mut gate = addressed.clone();
    gate["kind"] = json!("workflow_gate");
    cut(
        &feed,
        "m",
        vec![
            need("m", "gate", "open", gate),
            need("m", "for-parent", "open", addressed),
        ],
        cx,
    );
    let shown = cx.shown_system_notifications();
    assert_eq!(shown.len(), 1, "only the gate is the owner's");
    assert_eq!(shown[0].tag.to_string(), tag(&key("m", "gate")));
}

#[gpui::test]
async fn a_muted_machine_stays_quiet_with_no_replay_and_others_still_post(cx: &mut TestAppContext) {
    let (feed, _notify) = start(cx);
    cut(&feed, "remote", vec![], cx);
    cut(&feed, "local", vec![], cx);
    settings(&feed, "remote", Some(i64::MAX), cx);
    cut(
        &feed,
        "remote",
        vec![need("remote", "quiet", "open", json!({}))],
        cx,
    );
    assert!(cx.shown_system_notifications().is_empty());
    cut(
        &feed,
        "local",
        vec![need("local", "heard", "open", json!({}))],
        cx,
    );
    assert_eq!(
        cx.shown_system_notifications().len(),
        1,
        "another machine isn't muted"
    );
    settings(&feed, "remote", None, cx);
    cut(
        &feed,
        "remote",
        vec![need("remote", "quiet", "open", json!({"version":2}))],
        cx,
    );
    assert_eq!(
        cx.shown_system_notifications().len(),
        1,
        "the mute ending replays nothing"
    );
}

#[gpui::test]
async fn a_click_asks_for_its_exact_question_and_an_unknown_tag_does_nothing(
    cx: &mut TestAppContext,
) {
    let (feed, notify) = start(cx);
    let opened = std::rc::Rc::new(std::cell::RefCell::new(Vec::new()));
    let hold = cx.update(|cx| {
        let opened = opened.clone();
        cx.subscribe(&notify, move |_, OpenNeed(key): &OpenNeed, _| {
            opened.borrow_mut().push(key.clone())
        })
    });
    cut(&feed, "remote", vec![], cx);
    cut(
        &feed,
        "remote",
        vec![need("remote", "n", "open", json!({}))],
        cx,
    );
    let posted = cx.shown_system_notifications();
    cx.simulate_system_notification_response(SystemNotificationResponse {
        tag: "assembly:need:other:thing".into(),
        action_id: None,
    });
    cx.run_until_parked();
    assert!(opened.borrow().is_empty());
    cx.simulate_system_notification_response(SystemNotificationResponse {
        tag: posted[0].tag.clone(),
        action_id: None,
    });
    cx.run_until_parked();
    assert_eq!(*opened.borrow(), [key("remote", "n")]);
    drop(hold);
}
