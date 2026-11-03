use super::*;

#[test]
fn overwrite_requires_the_compared_draft_version_and_a_real_disk_revision() {
    let read = |revision: &str| {
        serde_json::from_str::<ComparedFile>(&format!(r#"{{"diskRevision":{revision},"diff":{{"path":"a","staged":"","unstaged":"","binary":false,"truncated":false}}}}"#)).unwrap()
    };
    let compared = Comparison::new(7, read("\"actual-observed-revision\""));
    assert_eq!(
        compared.overwrite_revision(7).unwrap(),
        "actual-observed-revision"
    );
    assert!(compared.overwrite_revision(8).is_err());
    assert!(
        Comparison::new(7, read("null"))
            .overwrite_revision(7)
            .is_err()
    );
}
