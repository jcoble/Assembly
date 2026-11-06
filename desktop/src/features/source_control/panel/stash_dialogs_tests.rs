use super::{DropApproval, Stash};
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

fn stash(message: &str) -> Stash {
    Stash {
        sha: "s".into(),
        message: message.into(),
        branch: None,
        created_ms: 0,
        made_by_assembly: message.starts_with("assembly "),
    }
}
#[test]
fn a_stash_is_called_by_its_words_or_when_it_was_made_never_by_its_session() {
    assert_eq!(
        stash("assembly view:aa:bb 2026-11-05T09:15: wip").description(),
        "wip"
    );
    assert_eq!(
        stash("assembly s1 2026-11-05T09:15: ").description(),
        "2026-11-05T09:15"
    );
    assert_eq!(
        stash("assembly view 2026-11-05T09:15:").description(),
        "2026-11-05T09:15"
    );
    assert_eq!(stash("on the side").description(), "on the side");
    let mut listed = stash("second idea");
    listed.branch = Some("trunk".into());
    assert_eq!(listed.description(), "On trunk: second idea");
}
