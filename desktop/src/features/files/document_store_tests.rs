use super::*;

fn key(machine: &str, path: &str) -> DocumentKey {
    DocumentKey {
        machine: machine.into(),
        checkout_root: "/checkout".into(),
        path: path.into(),
    }
}

#[test]
fn a_save_completion_cleans_only_the_version_that_was_sent() {
    let mut docs = DocumentStore::default();
    let key = key("local", "a.rs");
    docs.open(key.clone(), "r1".into(), "old".into()).unwrap();
    let sent = docs.replace(&key, 0, "first".into()).unwrap();
    docs.replace(&key, sent, "second".into()).unwrap();
    docs.saved(&key, sent, "r2".into());
    assert_eq!(docs.snapshot(&key).unwrap().text, "second");
    assert_eq!(docs.snapshot(&key).unwrap().base_revision, "r2");
    assert!(docs.is_dirty(&key));
}

#[test]
fn a_stale_second_target_leaves_the_whole_batch_unchanged() {
    let mut docs = DocumentStore::default();
    let a = key("local", "a");
    let b = key("local", "b");
    docs.open(a.clone(), "r1".into(), "alpha".into()).unwrap();
    docs.open(b.clone(), "r2".into(), "beta".into()).unwrap();
    let edits = [
        DraftEdit {
            key: a.clone(),
            expected_version: 0,
            edits: vec![TextEdit {
                start: 0,
                end: 5,
                replacement: "new".into(),
            }],
        },
        DraftEdit {
            key: b.clone(),
            expected_version: 9,
            edits: vec![],
        },
    ];
    assert_eq!(docs.apply_batch(&edits), Err(DraftFailure::Stale));
    assert_eq!(docs.snapshot(&a).unwrap().text, "alpha");
    assert!(!docs.is_dirty(&a));
}

#[test]
fn unicode_boundaries_and_owner_identity_are_preserved() {
    let mut docs = DocumentStore::default();
    let local = key("local", "a");
    let remote = key("workbox", "a");
    docs.open(local.clone(), "r1".into(), "é\r\n".into())
        .unwrap();
    docs.open(remote.clone(), "r2".into(), "remote".into())
        .unwrap();
    assert_eq!(
        docs.apply_batch(&[DraftEdit {
            key: local.clone(),
            expected_version: 0,
            edits: vec![TextEdit {
                start: 1,
                end: 2,
                replacement: "x".into()
            }]
        }]),
        Err(DraftFailure::InvalidRange)
    );
    assert_eq!(docs.snapshot(&local).unwrap().text, "é\r\n");
    assert_eq!(docs.snapshot(&remote).unwrap().text, "remote");
    assert_eq!(
        docs.open(key("local", "../escape"), "r".into(), String::new()),
        Err(DraftFailure::OutsideRoot)
    );
}

#[test]
fn dirty_budget_refusal_keeps_all_existing_drafts() {
    let mut docs = DocumentStore::default();
    for i in 0..8 {
        let key = key("local", &i.to_string());
        docs.open(key.clone(), "r".into(), String::new()).unwrap();
        docs.replace(&key, 0, "draft".into()).unwrap();
    }
    let ninth = key("local", "ninth");
    docs.open(ninth.clone(), "r".into(), String::new()).unwrap();
    assert_eq!(
        docs.replace(&ninth, 0, "new draft".into()),
        Err(DraftFailure::OverLimit)
    );
    assert!(docs.is_dirty(&key("local", "0")));
    assert_eq!(docs.snapshot(&ninth).unwrap().text, "");
}

#[test]
fn renderer_view_state_survives_while_a_dirty_draft_cannot_be_released() {
    let mut docs = DocumentStore::default();
    let key = key("m", "a");
    docs.open(key.clone(), "r".into(), "draft".into()).unwrap();
    docs.replace(&key, 0, "new".into()).unwrap();
    docs.stash_view(
        &key,
        DocumentView {
            selection: 1..2,
            scroll: (20., -40.),
        },
    );
    assert!(!docs.release_clean(&key));
    assert_eq!(docs.view_state(&key).unwrap().selection, 1..2);
    docs.saved(&key, 1, "r2".into());
    assert!(docs.release_clean(&key));
    assert_eq!(docs.snapshot(&key).unwrap_err(), DraftFailure::NotLoaded);
}

#[test]
fn reloading_disk_is_atomic_and_cannot_discard_a_newer_draft() {
    let mut docs = DocumentStore::default();
    let key = key("m", "a");
    docs.open(key.clone(), "old-revision".into(), "old".into())
        .unwrap();
    let requested = docs.replace(&key, 0, "draft".into()).unwrap();
    docs.replace(&key, requested, "newer".into()).unwrap();
    assert_eq!(
        docs.reload(&key, requested, "disk-revision".into(), "disk".into()),
        Err(DraftFailure::Stale)
    );
    assert_eq!(docs.snapshot(&key).unwrap().text, "newer");
    assert!(docs.is_dirty(&key));
    let version = docs.snapshot(&key).unwrap().version;
    docs.reload(&key, version, "disk-revision".into(), "disk\r\n".into())
        .unwrap();
    assert_eq!(docs.snapshot(&key).unwrap().text, "disk\r\n");
    assert!(!docs.is_dirty(&key));
}
