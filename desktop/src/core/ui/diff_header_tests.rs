use super::path_parts;

#[test]
fn a_path_splits_at_its_last_slash_and_a_bare_name_has_no_folder() {
    assert_eq!(path_parts("src/ui/a.rs"), ("src/ui/", "a.rs"));
    assert_eq!(path_parts("a.rs"), ("", "a.rs"));
}
