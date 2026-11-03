//! These fixtures use current producer display rows, not provider/runtime proof.
use serde_json::json;
use super::{EngineScope, EngineKey, EngineCursor, EngineStore};

fn scope(machine: &str, topic: &str) -> EngineScope {
    EngineScope { machine: machine.into(), topic: topic.into(), key: None }
}
fn row(machine: &str, kind: &str, id: &str, version: i64) -> serde_json::Value {
    json!({"machine":machine,"entityKind":kind,"id":id,"version":version,"state":"idle"})
}
fn cursor(through: i64) -> EngineCursor { EngineCursor { through, at: None } }

#[test]
fn source_collision_and_scope_reset_preserve_other_items() {
    let mut store = EngineStore::default();
    let local = scope("local-id", "sessions");
    let remote = scope("remote-id", "sessions");
    store.snapshot(local.clone(), cursor(1), vec![row("local-id", "session", "same", 1)]).unwrap();
    store.snapshot(remote.clone(), cursor(2), vec![row("remote-id", "session", "same", 2)]).unwrap();
    store.snapshot(remote.clone(), cursor(3), vec![]).unwrap();
    assert_eq!(store.items(&local)[0].version, 1);
    assert!(store.items(&remote).is_empty());
    assert_eq!(store.cursor(&local).unwrap().through, 1);
}

#[test]
fn a_scope_reset_preserves_rows_still_owned_by_another_scope() {
    let mut store = EngineStore::default();
    let all = scope("owner", "sessions");
    let one = EngineScope { key: Some("s".into()), ..all.clone() };
    store.snapshot(all.clone(), cursor(1), vec![row("owner", "session", "s", 1)]).unwrap();
    store.snapshot(one.clone(), cursor(1), vec![row("owner", "session", "s", 1)]).unwrap();
    store.snapshot(all, cursor(2), vec![]).unwrap();
    assert_eq!(store.items(&one).len(), 1);
}

#[test]
fn an_invalid_snapshot_does_not_partially_replace_held_state() {
    let mut store = EngineStore::default();
    let scope = scope("owner", "sessions");
    store.snapshot(scope.clone(), cursor(1), vec![row("owner", "session", "old", 1)]).unwrap();
    assert!(store.snapshot(scope.clone(), cursor(2), vec![
        row("owner", "session", "new", 2), row("another", "session", "foreign", 2)
    ]).is_err());
    assert_eq!(store.items(&scope)[0].key.id, "old");
    assert_eq!(store.cursor(&scope).unwrap().through, 1);
}

#[test]
fn sparse_events_invalidate_without_pretending_to_be_display_rows() {
    let mut store = EngineStore::default();
    let scope = scope("owner", "sessions");
    store.snapshot(scope.clone(), cursor(1), vec![row("owner", "session", "s", 1)]).unwrap();
    let events = vec![json!({"machine":"owner","seq":2,"entityKind":"session", "entityId":"s",
        "version":2,"data":{"state":"working"}})];
    let changed = store.events(&scope, cursor(2), &events).unwrap();
    let key = EngineKey { machine:"owner".into(), kind:"session".into(), id:"s".into() };
    assert_eq!(changed, [key.clone()]);
    assert_eq!(store.item(&key).unwrap().value["state"], "idle");
    assert_eq!(store.pending(&key), Some(2));
    assert!(store.events(&scope, cursor(2), &events).unwrap().is_empty());
}

#[test]
fn an_older_overlapping_snapshot_does_not_replace_newer_display_facts() {
    let mut store = EngineStore::default();
    let scope = scope("owner", "sessions");
    store.snapshot(scope.clone(), cursor(4), vec![row("owner", "session", "s", 4)]).unwrap();
    let one = EngineScope { key: Some("s".into()), ..scope };
    store.snapshot(one.clone(), cursor(3), vec![row("owner", "session", "s", 3)]).unwrap();
    assert_eq!(store.items(&one)[0].version, 4);
}
