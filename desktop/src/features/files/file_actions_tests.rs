use super::*;

#[test]
fn names_preserve_unicode_and_refuse_paths_in_a_name_prompt() {
    assert_eq!(named_target("dir", "é file.rs").unwrap(), "dir/é file.rs");
    assert_eq!(
        named_target("", " leading - name ").unwrap(),
        " leading - name "
    );
    for name in ["", ".", "..", "../outside", "a/b", "a\0b"] {
        assert!(named_target("dir", name).is_err());
    }
}

#[test]
fn an_unknown_action_cannot_be_replayed_and_only_exact_applied_paths_confirm() {
    let mut run = ActionRun::default();
    assert!(run.begin());
    run.unknown();
    assert!(!run.begin());
    assert_eq!(run.state, ActionState::Unknown);
    let mut run = ActionRun::default();
    assert!(run.begin());
    assert!(!run.complete(
        "new",
        ActionReply {
            state: "applied".into(),
            path: Some("other".into()),
            error: None
        }
    ));
    assert_eq!(run.state, ActionState::Unknown);
    let mut run = ActionRun::default();
    assert!(run.begin());
    assert!(run.complete(
        "new",
        ActionReply {
            state: "applied".into(),
            path: Some("new".into()),
            error: None
        }
    ));
    assert!(!run.begin());
}
