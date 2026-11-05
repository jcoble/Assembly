use serde_json::json;

use super::{approval_words, legend};
use crate::core::ui::ask::payload::AskPayload;

fn ask(title: Option<&str>, message: Option<&str>) -> AskPayload {
    serde_json::from_value(json!({
        "kind": "permission", "state": "open", "title": title, "message": message, "options": [],
    }))
    .unwrap()
}

#[test]
fn the_action_takes_one_line_and_the_details_hold_what_it_leaves_out() {
    assert_eq!(
        approval_words(&ask(None, None)),
        ("Action requested".into(), None)
    );
    assert_eq!(
        approval_words(&ask(Some("ls"), Some("ls"))),
        ("ls".into(), None)
    );
    assert_eq!(
        approval_words(&ask(Some("ls"), Some("Lists files"))),
        ("ls".into(), Some("Lists files".into()))
    );
    assert_eq!(
        approval_words(&ask(Some("cat <<EOF\nhi\nEOF"), None)),
        ("cat <<EOF…".into(), Some("cat <<EOF\nhi\nEOF".into()))
    );
    assert_eq!(
        approval_words(&ask(None, Some("Run it?"))),
        ("Run it?".into(), None)
    );
}

#[test]
fn a_field_that_can_be_left_out_says_so() {
    assert_eq!(legend("Zone", true), "Zone");
    assert_eq!(legend("Zone", false), "Zone (optional)");
}
