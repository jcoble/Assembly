use super::{GitFailure, LOST_REPLY, Recovery};
use crate::core::socket::{call_failure::CallFailure, error_code::ErrorCode};
fn failure(code: ErrorCode, message: &str) -> CallFailure {
    CallFailure {
        code,
        message: message.into(),
    }
}
// Pins: g9#10
#[test]
fn the_sentence_and_gits_text_split_at_the_first_blank_line_only() {
    let split = GitFailure::plain("Push was refused.\n\nerror: a\n\nerror: b");
    assert_eq!(split.sentence, "Push was refused.");
    assert_eq!(split.details.as_deref(), Some("error: a\n\nerror: b"));
    assert_eq!(GitFailure::plain("Only a sentence.").details, None);
}
#[test]
fn an_uncertain_answer_says_refresh_and_an_unreachable_machine_offers_connect() {
    let lost = GitFailure::from_call(&failure(
        ErrorCode::Uncertain {
            machine: "box".into(),
        },
        "ignored",
    ));
    assert_eq!(lost.sentence, LOST_REPLY);
    assert_eq!(lost.recovery, Some(Recovery::Refresh));
    let gone = GitFailure::from_call(&failure(
        ErrorCode::Unreachable {
            machine: "box".into(),
        },
        "box isn't connected",
    ));
    assert_eq!(gone.sentence, "box isn't connected");
    assert_eq!(gone.recovery, Some(Recovery::Connect("box".into())));
}
