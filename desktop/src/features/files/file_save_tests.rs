use super::*;

fn document() -> (DocumentStore, DocumentKey) {
    let mut docs = DocumentStore::default();
    let key = DocumentKey {
        machine: "m".into(),
        checkout_root: "/r".into(),
        path: "a".into(),
    };
    docs.open(key.clone(), "r1".into(), "old".into()).unwrap();
    docs.replace(&key, 0, "sent".into()).unwrap();
    (docs, key)
}

#[test]
fn confirmed_receipt_is_once_and_newer_typing_stays_dirty() {
    let (mut docs, key) = document();
    let mut save = FileSaveController::begin(docs.snapshot(&key).unwrap());
    docs.replace(&key, 1, "newer".into()).unwrap();
    let receipt = save.finish_confirmed("r2".into(), &mut docs).unwrap();
    assert_eq!(receipt.key, key);
    assert_eq!(receipt.saved_version, 1);
    assert_eq!(docs.snapshot(&key).unwrap().text, "newer");
    assert!(docs.is_dirty(&key));
    assert!(save.finish_confirmed("r2".into(), &mut docs).is_none());
}

#[test]
fn unknown_result_reconciles_only_equal_sent_bytes_without_another_commit() {
    let (mut docs, key) = document();
    let mut save = FileSaveController::begin(docs.snapshot(&key).unwrap());
    save.unknown();
    assert!(
        save.reconcile("different", "r-other".into(), &mut docs)
            .is_none()
    );
    assert_eq!(save.state, SaveState::Unknown);
    assert!(docs.is_dirty(&key));
    let id = save.save_id.clone();
    let receipt = save.reconcile("sent", "r2".into(), &mut docs).unwrap();
    assert_eq!(receipt.save_id, id);
    assert!(!docs.is_dirty(&key));
}
