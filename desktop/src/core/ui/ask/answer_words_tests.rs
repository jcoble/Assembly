use super::answer_words::{answered_words, choice_words, is_deny, option_words};
use super::payload::AskOption;
use super::presentation::NeedOption;

fn option(kind: &str, name: &str) -> AskOption {
    AskOption {
        option_id: kind.into(),
        name: name.into(),
        kind: kind.into(),
    }
}

fn choice(label: &str) -> NeedOption {
    NeedOption {
        id: label.into(),
        label: label.into(),
        kind: "choice".into(),
    }
}

#[test]
fn a_providers_options_read_by_kind_and_a_lasting_refusal_never_reads_as_an_allow() {
    assert_eq!(option_words(&option("allow_once", "Yes")), "Allow once");
    assert_eq!(
        option_words(&option("allow_always", "Yes always")),
        "Always allow this session"
    );
    assert_eq!(option_words(&option("reject_once", "No")), "Deny");
    assert_eq!(option_words(&option("reject_always", "Never")), "Never");
    assert_eq!(option_words(&option("other", "Plan first")), "Plan first");
    assert_eq!(option_words(&option("other", "")), "Continue");
    assert!(is_deny(&option("reject_always", "Never")));
    assert!(!is_deny(&option("allow_always", "Yes always")));
}

#[test]
fn a_workflows_bare_ids_read_as_words_and_what_an_asker_wrote_stays() {
    assert_eq!(choice_words(&choice("allow_once")), "Allow once");
    assert_eq!(choice_words(&choice("deny")), "Deny");
    assert_eq!(choice_words(&choice("approve")), "Approve");
    assert_eq!(choice_words(&choice("Ship it")), "Ship it");
    assert_eq!(answered_words("allow_once"), "Allow once");
    assert_eq!(answered_words("deny"), "Deny");
    assert_eq!(answered_words("Fix both"), "Fix both");
}
