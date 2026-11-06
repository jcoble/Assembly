//! Fixtures follow what `DisplayQueries.cs` sends. Nothing absent is made up.
use serde_json::{Value, json};

use super::{DAY_MS, NeedItem, NeedsState, age, asked_by, card_state, title};
use crate::core::engine::EngineItem;
use crate::core::engine::EngineKey;

fn need(mut value: Value) -> NeedItem {
    value["entityKind"] = json!("needs_me");
    value["machine"] = json!("m");
    value["id"] = json!("n");
    value["version"] = json!(1);
    value["sessionId"] = json!("s");
    if value.get("kind").is_none() {
        value["kind"] = json!("question");
    }
    NeedItem::of(&EngineItem {
        key: EngineKey {
            machine: "m".into(),
            kind: "needs_me".into(),
            id: "n".into(),
        },
        version: 1,
        value,
    })
    .unwrap()
}

#[test]
fn a_number_or_short_title_shows_only_when_recorded() {
    assert_eq!(
        title(&need(json!({"state":"open","text":"Ship it?\nMore lines"}))),
        "Ship it?"
    );
    assert_eq!(
        title(&need(
            json!({"state":"open","text":"Ship it?","displayNumber":12})
        )),
        "Q12 · Ship it?"
    );
    assert_eq!(
        title(&need(json!({"state":"open"}))),
        "Loading the question…"
    );
}

#[test]
fn a_question_closed_by_a_revision_says_why_on_the_board() {
    let closed = need(json!({"state":"superseded","text":"Ship it?"}));
    assert!(matches!(
        card_state(&closed),
        NeedsState::Answered { summary, delivery: None } if summary.as_ref() == "Closed: the work it follows is being redone"
    ));
}

#[test]
fn age_reads_minutes_hours_then_the_stale_cue() {
    assert_eq!(age(0, 30_000), ("just now".to_owned(), false));
    assert_eq!(age(0, 240_000), ("4m ago".to_owned(), false));
    assert_eq!(age(0, 2 * 3_600_000), ("2h ago".to_owned(), false));
    assert_eq!(age(0, DAY_MS), ("asked 1 day ago".to_owned(), true));
    assert_eq!(age(0, 2 * DAY_MS), ("asked 2 days ago".to_owned(), true));
    assert!(!age(10, 0).1, "a clock ahead of this one isn't stale");
}

#[test]
fn the_answered_line_says_only_what_the_engine_recorded() {
    let answered = |origin: Value, by: Value| {
        need(
            json!({"state":"answered","answerText":"Fix both","answeredOrigin":origin,"answeredBy":by}),
        )
    };
    let delivered = |state| match state {
        NeedsState::Answered { summary, delivery } => {
            (summary.to_string(), delivery.map(|text| text.to_string()))
        }
        NeedsState::Open => panic!("open"),
    };
    assert_eq!(
        delivered(card_state(&answered(Value::Null, Value::Null))),
        ("Fix both".to_owned(), Some("Answered elsewhere".to_owned()))
    );
    assert_eq!(
        delivered(card_state(&answered(
            json!({"channel":"phone"}),
            Value::Null
        )))
        .1
        .as_deref(),
        Some("Answered on phone")
    );
    assert_eq!(
        delivered(card_state(&answered(
            json!({"channel":"desktop","surface":"rail"}),
            Value::Null
        )))
        .1
        .as_deref(),
        Some("Answered in the rail")
    );
    assert_eq!(
        delivered(card_state(&answered(Value::Null, json!("owner"))))
            .1
            .as_deref(),
        Some("Answered by owner")
    );
}

#[test]
fn a_cancelled_need_reads_session_ended_and_a_dismissed_one_dismissed() {
    assert_eq!(
        card_state(&need(json!({"state":"cancelled"}))),
        NeedsState::Answered {
            summary: "Session ended".into(),
            delivery: None
        }
    );
    assert_eq!(
        card_state(&need(json!({"state":"dismissed"}))),
        NeedsState::Answered {
            summary: "Dismissed".into(),
            delivery: None
        }
    );
    assert_eq!(card_state(&need(json!({"state":"open"}))), NeedsState::Open);
}

#[test]
fn a_child_question_says_who_its_for() {
    assert_eq!(
        asked_by("Worker", "This Mac", Some("Main orchestrator")),
        "Worker · This Mac · for Main orchestrator"
    );
    assert_eq!(
        asked_by("Main", "Workbox Test", None),
        "Main · Workbox Test"
    );
}
