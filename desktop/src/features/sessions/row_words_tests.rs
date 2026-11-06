use super::row_words::{chip, oldest_open_preview};
use crate::core::engine::engine_items::NeedItem;
use crate::core::engine::{EngineItem, EngineKey};
use serde_json::json;

fn need(kind: &str, number: i64, text: &str) -> NeedItem {
    NeedItem::of(&EngineItem {key:EngineKey {machine:"owner".into(),kind:"needs_me".into(),id:format!("n{number}")},version:1,
        value:json!({"entityKind":"needs_me","machine":"owner","id":format!("n{number}"),"version":1,
            "sessionId":"s","kind":kind,"state":"open","text":text,"displayNumber":number})}).unwrap()
}

#[test]
fn chip_says_questions_approvals_or_need_you_from_actual_open_items() {
    assert!(chip(&[]).is_none());
    assert_eq!(
        chip(&[need("question", 1, "Q")]).unwrap().as_ref(),
        "1 question"
    );
    assert_eq!(
        chip(&[need("question", 1, "Q"), need("choice", 2, "Pick")])
            .unwrap()
            .as_ref(),
        "2 questions"
    );
    assert_eq!(
        chip(&[need("permission", 1, "Allow")]).unwrap().as_ref(),
        "1 approval"
    );
    assert_eq!(
        chip(&[need("confirmation", 1, "Confirm")])
            .unwrap()
            .as_ref(),
        "1 approval"
    );
    assert_eq!(
        chip(&[need("question", 1, "Q"), need("permission", 2, "Allow")])
            .unwrap()
            .as_ref(),
        "2 need you"
    );
    let mut settled = need("question", 3, "Old");
    settled.state = "answered".into();
    assert!(chip(&[settled]).is_none());
}

#[test]
fn waiting_preview_is_the_oldest_numbered_open_question_and_preserves_actual_text() {
    let mut answered = need("question", 1, "Answered");
    answered.state = "answered".into();
    let held = [
        need("permission", 4, "  Actual oldest open text  "),
        need("question", 9, "Later"),
        answered,
    ];
    assert_eq!(
        oldest_open_preview(&held),
        Some("  Actual oldest open text  ")
    );
    let mut unnumbered = need("question", 5, "No authoritative order");
    unnumbered.display_number = None;
    assert_eq!(oldest_open_preview(&[unnumbered]), None);
}
