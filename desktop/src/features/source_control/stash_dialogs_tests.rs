use super::DropApproval;
#[test]
fn an_assembly_stash_needs_confirmation_and_a_foreign_stash_needs_two() {
    let mut own = DropApproval::new("actual-sha".into(), true);
    assert!(!own.sent());
    assert_eq!(own.confirm().as_deref(), Some("actual-sha"));
    assert!(own.confirm().is_none());
    let mut foreign = DropApproval::new("foreign-sha".into(), false);
    assert!(!foreign.sent());
    assert!(foreign.confirm().is_none());
    assert!(!foreign.sent());
    assert_eq!(foreign.confirm().as_deref(), Some("foreign-sha"));
    assert!(foreign.confirm().is_none());
}
