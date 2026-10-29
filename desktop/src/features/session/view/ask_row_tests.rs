use serde_json::{Value, json};

use super::{asked, outcome, settled};
use crate::features::session::rows::ask::AskPayload;

fn ask(state: &str, answer: Option<Value>) -> AskPayload {
    serde_json::from_value(json!({
        "kind": "permission",
        "state": state,
        "options": [
            { "optionId": "allow", "name": "Allow once", "kind": "allow_once" },
            { "optionId": "deny", "name": "No", "kind": "reject_once" },
        ],
        "answer": answer,
    }))
    .unwrap()
}

// Pins: g0-session#16
#[test]
fn a_settled_card_shows_what_it_was_answered_with() {
    assert_eq!(
        outcome(&ask("answered", Some(json!({ "optionId": "allow" })))),
        "Allow once"
    );
    assert_eq!(
        outcome(&ask("answered", Some(json!({ "optionId": "gone" })))),
        "gone"
    );
    assert_eq!(outcome(&ask("cancelled", None)), "Cancelled");
    assert_eq!(
        outcome(&ask("answered", Some(json!({ "action": "decline" })))),
        "Skipped"
    );
    let form = json!({ "action": "accept", "content": { "color": "red", "sizes": ["s", "m"], "color_custom": "" } });
    assert_eq!(outcome(&ask("answered", Some(form))), "red; s, m");
    assert_eq!(
        outcome(&ask(
            "answered",
            Some(json!({ "action": "accept", "content": {} }))
        )),
        "Answered"
    );
}

// Pins: g0-session#16
#[test]
fn submit_sends_what_was_picked_and_typed_leaving_out_the_empty_fields() {
    use super::AskForm;
    let mut picked = serde_json::Map::new();
    picked.insert("zone".into(), json!("east"));
    picked.insert("tags".into(), json!([]));
    let texts = vec![
        ("note".to_string(), "  hi ".to_string()),
        ("zone_custom".to_string(), " ".to_string()),
    ];
    assert_eq!(
        AskForm::content(&picked, texts),
        json!({ "action": "accept", "content": { "zone": "east", "note": "hi" } })
    );
}

// Opened again once answered, a question shows what it asked: each field, its hint and its choices.
// Pins: g0-session#16
#[test]
fn a_settled_question_still_says_what_it_asked() {
    let question: AskPayload = serde_json::from_value(json!({
        "kind": "question",
        "state": "answered",
        "options": [],
        "answer": { "action": "accept", "content": { "color": "red" } },
        "schema": { "type": "object", "properties": {
            "color": { "type": "string", "title": "Color", "description": "Pick one", "enum": ["red", "blue"] },
            "note": { "type": "string" }
        } }
    }))
    .unwrap();
    assert_eq!(
        asked(&question),
        [
            (
                "Color".to_string(),
                Some("Pick one".to_string()),
                "red, blue".to_string()
            ),
            ("note".to_string(), None, String::new()),
        ]
    );
    assert!(
        asked(&ask("answered", None)).is_empty(),
        "a permission has no fields"
    );
}

// Pins: g0-session#16
#[test]
fn an_answered_permission_reads_allowed_or_denied() {
    let allowed = ask("answered", Some(json!({ "optionId": "allow" })));
    assert_eq!(settled(&allowed), "Allowed");
    let denied = ask("answered", Some(json!({ "optionId": "deny" })));
    assert_eq!(settled(&denied), "Denied");
    assert_eq!(settled(&ask("cancelled", None)), "Cancelled");
    let skipped = ask("answered", Some(json!({ "action": "decline" })));
    assert_eq!(settled(&skipped), "Skipped");
}
