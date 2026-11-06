use super::remote_args;
#[test]
fn remote_input_keeps_literal_name_and_url_and_never_submits_blank_fields() {
    assert!(remote_args("s", " ", "ssh://host/repo").is_err());
    assert!(remote_args("s", "upstream", " ").is_err());
    let args = remote_args("actual-session", "upstream", "ssh://host/repo with space").unwrap();
    assert_eq!(args["session"], "actual-session");
    assert_eq!(args["name"], "upstream");
    assert_eq!(args["url"], "ssh://host/repo with space");
}
