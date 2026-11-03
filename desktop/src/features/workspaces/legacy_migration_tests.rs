use super::*;
fn owner(session: &str, root: &str) -> WorkspaceKey {
    WorkspaceKey {
        machine: "actual-owner".into(),
        session_id: session.into(),
        checkout_root: root.into(),
    }
}
fn capture(alias: &str, path: &str, key: Option<WorkspaceKey>) -> LegacyFileCapture {
    LegacyFileCapture {
        legacy_session: alias.into(),
        path: path.into(),
        owner: key,
        document: None,
        version: None,
        entry: json!({"path":path}),
        active: false,
    }
}
#[test]
fn grouping_uses_real_returned_owner_identity_and_keeps_checkouts_separate() {
    let mut first = capture(
        "old-alias",
        "src/λ.rs",
        Some(owner("real-source-session", "/a")),
    );
    first.active = true;
    let plan = prepare(
        vec![
            first,
            capture(
                "old-alias",
                "b.rs",
                Some(owner("real-source-session", "/b")),
            ),
        ],
        &HashMap::new(),
    )
    .unwrap();
    assert_eq!(plan.groups.len(), 2);
    assert_eq!(plan.groups[0].key, owner("real-source-session", "/a"));
    assert_eq!(plan.groups[0].snapshot["files"][0]["path"], "src/λ.rs");
    assert_eq!(plan.groups[0].snapshot["activePath"], "src/λ.rs");
    assert_eq!(plan.groups[1].key.checkout_root, "/b");
}
#[test]
fn missing_real_owner_duplicate_alias_and_canonical_collisions_refuse_before_writes() {
    assert!(prepare(vec![capture("bare", "a", None)], &HashMap::new()).is_err());
    assert!(
        prepare(
            vec![
                capture("old", "a", Some(owner("s", "/a"))),
                capture("old", "a", Some(owner("s", "/a")))
            ],
            &HashMap::new()
        )
        .is_err()
    );
    assert!(
        prepare(
            vec![
                capture("alias1", "a", Some(owner("s", "/a"))),
                capture("alias2", "a", Some(owner("s", "/a")))
            ],
            &HashMap::new()
        )
        .is_err()
    );
}
#[test]
fn resolved_ownership_must_match_the_actual_document_source_root_path_and_version() {
    let mut captured = capture("old", "a", None);
    captured.document = Some(crate::features::files::document_store::DocumentKey {
        machine: "other-owner".into(),
        checkout_root: "/a".into(),
        path: "a".into(),
    });
    captured.version = Some(3);
    let resolved = HashMap::from([("old".into(), owner("s", "/a"))]);
    assert!(prepare(vec![captured.clone()], &resolved).is_err());
    captured.document.as_mut().unwrap().machine = "actual-owner".into();
    let plan = prepare(vec![captured.clone()], &resolved).unwrap();
    assert_eq!(plan.groups[0].key.session_id, "s");
    captured.version = None;
    assert!(prepare(vec![captured], &resolved).is_err());
}
#[test]
fn existing_canonical_drafts_are_never_overwritten_and_exact_restart_reconciliation_is_allowed() {
    let planned =
        json!({"version":1,"files":[{"path":"a","baseRevision":"r","draft":"exact λ\r\n"}]});
    assert_eq!(canonical(0, None, &planned).unwrap(), Canonical::Missing);
    assert_eq!(
        canonical(1, Some(&planned), &planned).unwrap(),
        Canonical::Confirmed(1)
    );
    assert_eq!(
        canonical(4, Some(&planned), &planned).unwrap(),
        Canonical::Confirmed(4)
    );
    assert!(
        canonical(
            1,
            Some(&json!({"version":1,"files":[{"path":"a","draft":"someone else's newer draft"}]})),
            &planned
        )
        .is_err()
    );
    assert!(canonical(0, Some(&planned), &planned).is_err());
    assert!(canonical(2, None, &planned).is_err());
}
