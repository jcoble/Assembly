use super::*;
#[test]
fn cancelling_reverts_only_the_draft_without_requesting_a_save() {
    let mut state = EditState::new("original".to_string());
    state.edit();
    state.change("draft".into());
    assert!(state.cancel());
    assert_eq!(state.committed(), "original");
    assert_eq!(state.draft(), "original");
    assert!(!state.editing());
    assert!(!state.pending());
}
#[test]
fn failed_save_keeps_draft_and_error_then_retry_waits_for_confirmation() {
    let mut state = EditState::new("original".to_string());
    state.edit();
    state.change("draft".into());
    let (request, value) = state.save().unwrap();
    assert_eq!(value, "draft");
    assert!(state.pending());
    assert_eq!(state.committed(), "original");
    assert!(!state.cancel());
    assert!(!state.change("discarded".into()));
    assert!(state.complete(request, Err("Refused by caller".into())));
    assert_eq!(state.draft(), "draft");
    assert_eq!(state.error(), Some("Refused by caller"));
    let (retry, _) = state.save().unwrap();
    assert_ne!(request, retry);
    assert!(state.complete(retry, Ok("normalized".into())));
    assert_eq!(state.committed(), "normalized");
    assert!(!state.editing());
}
#[test]
fn superseded_completion_does_not_overwrite_newer_external_value_or_draft() {
    let mut state = EditState::new("initial".to_string());
    state.edit();
    state.change("draft".into());
    let (old, _) = state.save().unwrap();
    state.set_committed("external".into());
    assert_eq!(state.committed(), "external");
    assert_eq!(state.draft(), "draft");
    assert!(!state.complete(old, Ok("stale".into())));
    assert!(!state.complete(old, Err("stale error".into())));
    assert_eq!(state.error(), None);
    state.cancel();
    assert_eq!(state.draft(), "external");
}
#[test]
fn unchanged_save_returns_to_display_without_a_commit_request() {
    let mut state = EditState::new(42);
    state.edit();
    assert!(state.save().is_none());
    assert!(!state.editing());
    assert_eq!(*state.committed(), 42);
}
#[test]
fn external_value_updates_display_without_opening_editor() {
    let mut state = EditState::new(None::<String>);
    state.set_committed(Some("new".into()));
    assert_eq!(state.committed(), state.draft());
    assert!(!state.editing());
    assert!(!state.change(None));
}
