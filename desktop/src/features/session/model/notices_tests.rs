//! How a failed run reads, ported from app/test/notices.test.ts.

use super::notices::Notice;

const NO_WORDS: &str = "The agent reported an error without saying what went wrong.";

// Pins: g0-session#19
#[test]
fn a_relay_error_row_becomes_a_notice() {
    assert_eq!(
        Notice::of_error("API Error: 529 Overloaded", Some("overloaded")),
        Some(Notice {
            label: "Conversation error".into(),
            message: "API Error: 529 Overloaded".into(),
            hint: None
        })
    );
}

// Pins: g0-session#19
#[test]
fn a_provider_error_wrapped_in_json_is_unwrapped_and_a_full_context_says_what_to_do() {
    let wrapped = r#"{"code":-32603,"message":"Internal error: Prompt is too long"}"#;
    assert_eq!(
        Notice::of_error(wrapped, None),
        Some(Notice {
            label: "Conversation is full".into(),
            message: "This conversation has outgrown the model's context window, so nothing more can be sent to it.".into(),
            hint: Some("Send /compact if this agent offers it, or start a new session and bring what you need with you.".into()),
        })
    );
    let nested = r#"{"error":{"message":"rate limited"}}"#;
    assert_eq!(
        Notice::of_error(nested, None).unwrap().message,
        "rate limited"
    );
}

// Pins: g0-session#19
#[test]
fn an_error_with_no_words_gets_a_sentence_and_a_cancelled_run_gets_no_notice() {
    assert_eq!(Notice::of_error("", None).unwrap().message, NO_WORDS);
    assert_eq!(Notice::of_error("  ", None).unwrap().message, NO_WORDS);
    assert_eq!(Notice::of_error("run cancelled", Some("cancelled")), None);
}
