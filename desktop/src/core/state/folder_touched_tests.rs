use super::can_change_folder;

#[test]
fn only_a_tool_that_can_write_touches_the_folder() {
    for kind in ["read", "search", "think", "fetch"] {
        assert!(!can_change_folder(Some(kind)), "{kind}");
    }
    for kind in ["edit", "delete", "move", "execute", "other"] {
        assert!(can_change_folder(Some(kind)), "{kind}");
    }
    assert!(can_change_folder(None));
}
