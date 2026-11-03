use super::super::git_status::GitStatus;
use super::request;
fn status() -> GitStatus {
    GitStatus {
        branch: Some("feature/ü".into()),
        head: Some("a".repeat(40)),
        ahead: 1,
        behind: 2,
        has_upstream: true,
        files: vec![],
        total_files: 0,
        total_staged: Some(0),
        fingerprint: "snapshot".into(),
    }
}
#[test]
fn sync_uses_the_actual_branch_and_publish_requires_an_explicit_known_remote() {
    let current = status();
    assert_eq!(
        request("pull", Some(&current), None, &[]).unwrap()["expectedBranch"],
        "feature/ü"
    );
    assert_eq!(
        request("push", Some(&current), None, &[]).unwrap()["expectedBranch"],
        "feature/ü"
    );
    assert!(request("publish", Some(&current), None, &["upstream".into()]).is_err());
    assert!(
        request(
            "publish",
            Some(&current),
            Some("origin"),
            &["upstream".into()]
        )
        .is_err()
    );
    let mut unpublished = current.clone();
    unpublished.has_upstream = false;
    let publish = request(
        "publish",
        Some(&unpublished),
        Some("upstream"),
        &["upstream".into()],
    )
    .unwrap();
    assert_eq!(publish["remote"], "upstream");
    assert_eq!(publish["expectedBranch"], "feature/ü");
}
#[test]
fn detached_unborn_or_unobserved_branches_cannot_be_pushed_and_fetch_does_not_invent_a_branch() {
    assert!(request("push", None, None, &[]).is_err());
    assert!(
        request(
            "push",
            Some(&GitStatus {
                branch: None,
                ..status()
            }),
            None,
            &[]
        )
        .is_err()
    );
    assert!(
        request(
            "publish",
            Some(&GitStatus {
                head: None,
                ..status()
            }),
            Some("remote"),
            &["remote".into()]
        )
        .is_err()
    );
    assert_eq!(
        request("fetch", None, None, &["actual-remote".into()]).unwrap(),
        serde_json::json!({})
    );
    assert_eq!(request("fetch", None, None, &[]).unwrap_err(), "no remote");
    assert_eq!(
        request(
            "push",
            Some(&GitStatus {
                has_upstream: false,
                ..status()
            }),
            None,
            &[]
        )
        .unwrap_err(),
        "no upstream: publish first"
    );
    assert_eq!(
        request(
            "push",
            Some(&GitStatus {
                branch: None,
                ..status()
            }),
            None,
            &[]
        )
        .unwrap_err(),
        "detached HEAD"
    );
    assert!(request("forcePush", Some(&status()), None, &[]).is_err());
}
