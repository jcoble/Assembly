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

#[test]
fn explicit_discard_refuses_a_draft_changed_after_the_confirmation_started() {
    let mut docs = DocumentStore::default();
    let key = DocumentKey {
        machine: "m".into(),
        checkout_root: "/checkout".into(),
        path: "a".into(),
    };
    docs.open(key.clone(), "r".into(), "disk".into()).unwrap();
    docs.replace(&key, 0, "first draft".into()).unwrap();
    docs.replace(&key, 1, "newer draft".into()).unwrap();
    assert_eq!(docs.discard(&key, 1), Err(DraftFailure::Stale));
    assert_eq!(docs.snapshot(&key).unwrap().text, "newer draft");
    assert!(docs.is_dirty(&key));
    docs.discard(&key, 2).unwrap();
    assert_eq!(docs.snapshot(&key).unwrap_err(), DraftFailure::NotLoaded);
}

#[test]
fn rename_moves_only_the_confirmed_owners_matching_keys_and_keeps_dirty_versions() {
    let mut docs = DocumentStore::default();
    let source = key("host", "dir/a");
    let other = key("other-host", "dir/a");
    docs.open(source.clone(), "r1".into(), "disk".into())
        .unwrap();
    docs.open(other.clone(), "r2".into(), "other".into())
        .unwrap();
    docs.replace(&source, 0, "draft".into()).unwrap();
    docs.stash_view(
        &source,
        DocumentView {
            selection: 1..3,
            scroll: (0.0, -20.0),
        },
    );
    docs.rename_paths("host", "/checkout", "dir", "renamed")
        .unwrap();
    let target = key("host", "renamed/a");
    let snapshot = docs.snapshot(&target).unwrap();
    assert_eq!(snapshot.version, 1);
    assert_eq!(snapshot.base_revision, "r1");
    assert_eq!(snapshot.text, "draft");
    assert!(docs.is_dirty(&target));
    assert_eq!(docs.view_state(&target).unwrap().selection, 1..3);
    assert_eq!(docs.snapshot(&source).unwrap_err(), DraftFailure::NotLoaded);
    assert_eq!(docs.snapshot(&other).unwrap().text, "other");
}

#[test]
fn rename_target_draft_collision_refuses_the_entire_metadata_change() {
    let mut docs = DocumentStore::default();
    let source = key("host", "dir/a");
    let target = key("host", "target/a");
    docs.open(source.clone(), "r1".into(), "source".into())
        .unwrap();
    docs.open(target.clone(), "r2".into(), "target".into())
        .unwrap();
    docs.replace(&target, 0, "dirty target".into()).unwrap();
    assert!(
        docs.rename_paths("host", "/checkout", "dir", "target")
            .is_err()
    );
    assert_eq!(docs.snapshot(&source).unwrap().text, "source");
    assert_eq!(docs.snapshot(&target).unwrap().text, "dirty target");
    assert!(docs.is_dirty(&target));
}

#[test]
fn pending_rename_detects_a_newer_source_draft_without_copying_its_contents() {
    let mut docs = DocumentStore::default();
    let source = key("host", "a");
    docs.open(source.clone(), "r".into(), "disk".into())
        .unwrap();
    let before = docs.versions_under("host", "/checkout", "a");
    docs.replace(&source, 0, "newer draft".into()).unwrap();
    assert_ne!(before, docs.versions_under("host", "/checkout", "a"));
    assert_eq!(docs.snapshot(&source).unwrap().text, "newer draft");
}

#[test]
fn restoring_drafts_retains_exact_bytes_and_never_overwrites_a_newer_live_buffer() {
    let mut docs = DocumentStore::default();
    let key = key("host", "a");
    let restore = || RestoredDraft {
        key: key.clone(),
        base_revision: "real-base".into(),
        text: "é\r\nlast".into(),
        view: DocumentView {
            selection: 2..2,
            scroll: (0.0, -5.0),
        },
    };
    docs.restore_drafts(vec![restore()]).unwrap();
    assert_eq!(docs.snapshot(&key).unwrap().text, "é\r\nlast");
    assert!(docs.is_dirty(&key));
    docs.replace(&key, 1, "newer live draft".into()).unwrap();
    docs.restore_drafts(vec![restore()]).unwrap();
    assert_eq!(docs.snapshot(&key).unwrap().text, "newer live draft");
    assert_eq!(docs.snapshot(&key).unwrap().version, 2);
}
#[test]
fn a_restored_batch_over_the_dirty_budget_changes_none_of_its_targets() {
    let mut docs = DocumentStore::default();
    let incoming = (0..9)
        .map(|i| RestoredDraft {
            key: key("host", &format!("file{i}")),
            base_revision: "r".into(),
            text: "draft".into(),
            view: DocumentView::default(),
        })
        .collect();
    assert_eq!(docs.restore_drafts(incoming), Err(DraftFailure::OverLimit));
    assert!(docs.dirty_keys("host", "/checkout").is_empty());
}

#[test]
fn draft_restore_preflight_is_pure_and_uses_the_same_atomic_budget() {
    use super::{DocumentView, RestoredDraft};
    let mut docs = DocumentStore::default();
    let drafts = (0..9)
        .map(|index| RestoredDraft {
            key: key("machine", &format!("restore-{index}")),
            base_revision: "actual-base".into(),
            text: "unsaved λ\r\n".into(),
            view: DocumentView::default(),
        })
        .collect::<Vec<_>>();
    assert_eq!(
        docs.can_restore_drafts(&drafts),
        Err(DraftFailure::OverLimit)
    );
    assert!(docs.dirty_keys("machine", "/checkout").is_empty());
    assert_eq!(docs.restore_drafts(drafts), Err(DraftFailure::OverLimit));
    assert!(docs.dirty_keys("machine", "/checkout").is_empty());
}
