use serde_json::json;

use super::{approval_words, is_deny, legend, option_label};
use crate::core::ui::ask::payload::{AskOption, AskPayload};

fn option(kind: &str, name: &str) -> AskOption {
    AskOption { option_id: kind.into(), name: name.into(), kind: kind.into() }
}

fn ask(title: Option<&str>, message: Option<&str>) -> AskPayload {
    serde_json::from_value(json!({
        "kind": "permission", "state": "open", "title": title, "message": message, "options": [],
    }))
    .unwrap()
}

#[test]
fn options_read_as_the_old_panel_has_them_and_a_lasting_refusal_never_reads_as_an_allow() {
    assert_eq!(option_label(&option("allow_once", "Yes")), "Approve once");
    assert_eq!(option_label(&option("allow_always", "Yes always")), "Always allow this session");
    assert_eq!(option_label(&option("reject_once", "No")), "Decline");
    assert_eq!(option_label(&option("reject_always", "Never")), "Never");
    assert_eq!(option_label(&option("other", "Plan first")), "Plan first");
    assert_eq!(option_label(&option("other", "")), "Continue");
    assert!(is_deny(&option("reject_always", "Never")));
    assert!(!is_deny(&option("allow_always", "Yes always")));
}

#[test]
fn the_action_takes_one_line_and_the_details_hold_what_it_leaves_out() {
    assert_eq!(approval_words(&ask(None, None)), ("Action requested".into(), None));
    assert_eq!(approval_words(&ask(Some("ls"), Some("ls"))), ("ls".into(), None));
    assert_eq!(
        approval_words(&ask(Some("ls"), Some("Lists files"))),
        ("ls".into(), Some("Lists files".into()))
    );
    assert_eq!(
        approval_words(&ask(Some("cat <<EOF\nhi\nEOF"), None)),
        ("cat <<EOF…".into(), Some("cat <<EOF\nhi\nEOF".into()))
    );
    assert_eq!(approval_words(&ask(None, Some("Run it?"))), ("Run it?".into(), None));
}

#[test]
fn a_field_that_can_be_left_out_says_so() {
    assert_eq!(legend("Zone", true), "Zone");
    assert_eq!(legend("Zone", false), "Zone (optional)");
}
