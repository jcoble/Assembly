//! These fixtures use current producer display rows, not provider/runtime proof.
use super::{EngineCursor, EngineKey, EngineScope, EngineStore};
use serde_json::json;

fn scope(machine: &str, topic: &str) -> EngineScope {
    EngineScope {
        machine: machine.into(),
        topic: topic.into(),
        key: None,
    }
}
fn row(machine: &str, kind: &str, id: &str, version: i64) -> serde_json::Value {
    json!({"machine":machine,"entityKind":kind,"id":id,"version":version,"state":"idle"})
}
fn cursor(through: i64) -> EngineCursor {
    EngineCursor { through, at: None }
}

#[test]
fn source_collision_and_scope_reset_preserve_other_items() {
    let mut store = EngineStore::default();
    let local = scope("local-id", "sessions");
    let remote = scope("remote-id", "sessions");
    store
        .snapshot(
            local.clone(),
            cursor(1),
            vec![row("local-id", "session", "same", 1)],
        )
        .unwrap();
    store
        .snapshot(
            remote.clone(),
            cursor(2),
            vec![row("remote-id", "session", "same", 2)],
        )
        .unwrap();
    store.snapshot(remote.clone(), cursor(3), vec![]).unwrap();
    assert_eq!(store.items(&local)[0].version, 1);
    assert!(store.items(&remote).is_empty());
    assert_eq!(store.cursor(&local).unwrap().through, 1);
}

#[test]
fn a_scope_reset_preserves_rows_still_owned_by_another_scope() {
    let mut store = EngineStore::default();
    let all = scope("owner", "sessions");
    let one = EngineScope {
        key: Some("s".into()),
        ..all.clone()
    };
    store
        .snapshot(
            all.clone(),
            cursor(1),
            vec![row("owner", "session", "s", 1)],
        )
        .unwrap();
    store
        .snapshot(
            one.clone(),
            cursor(1),
            vec![row("owner", "session", "s", 1)],
        )
        .unwrap();
    store.snapshot(all, cursor(2), vec![]).unwrap();
    assert_eq!(store.items(&one).len(), 1);
}

#[test]
fn an_invalid_snapshot_does_not_partially_replace_held_state() {
    let mut store = EngineStore::default();
    let scope = scope("owner", "sessions");
    store
        .snapshot(
            scope.clone(),
            cursor(1),
            vec![row("owner", "session", "old", 1)],
        )
        .unwrap();
    assert!(
        store
            .snapshot(
                scope.clone(),
                cursor(2),
                vec![
                    row("owner", "session", "new", 2),
                    row("another", "session", "foreign", 2)
                ]
            )
            .is_err()
    );
    assert_eq!(store.items(&scope)[0].key.id, "old");
    assert_eq!(store.cursor(&scope).unwrap().through, 1);
}

#[test]
fn sparse_events_identify_rows_without_pretending_to_be_display_or_advancing_cursor() {
    let mut store = EngineStore::default();
    let scope = scope("owner", "sessions");
    store
        .snapshot(
            scope.clone(),
            cursor(1),
            vec![row("owner", "session", "s", 1)],
        )
        .unwrap();
    let events = vec![
        json!({"machine":"owner","seq":2,"entityKind":"session", "entityId":"s",
        "version":2,"data":{"state":"working"}}),
    ];
    let changed = store.event_refs(&scope, &cursor(2), &events).unwrap();
    let key = EngineKey {
        machine: "owner".into(),
        kind: "session".into(),
        id: "s".into(),
    };
    assert_eq!(changed, [key.clone()]);
    assert_eq!(store.item(&key).unwrap().value["state"], "idle");
    assert_eq!(store.cursor(&scope).unwrap().through, 1);
    assert_eq!(
        store.event_refs(&scope, &cursor(2), &events).unwrap(),
        [key]
    );
}

#[test]
fn an_older_overlapping_snapshot_does_not_replace_newer_display_facts() {
    let mut store = EngineStore::default();
    let scope = scope("owner", "sessions");
    store
        .snapshot(
            scope.clone(),
            cursor(4),
            vec![row("owner", "session", "s", 4)],
        )
        .unwrap();
    let one = EngineScope {
        key: Some("s".into()),
        ..scope
    };
    store
        .snapshot(
            one.clone(),
            cursor(3),
            vec![row("owner", "session", "s", 3)],
        )
        .unwrap();
    assert_eq!(store.items(&one)[0].version, 4);
}

#[test]
fn hydration_commits_display_and_stream_cursor_together_after_all_rows_validate() {
    let mut store = EngineStore::default();
    let scope = scope("owner", "sessions");
    store
        .snapshot(
            scope.clone(),
            cursor(1),
            vec![row("owner", "session", "s", 1)],
        )
        .unwrap();
    let events =
        vec![json!({"machine":"owner","seq":2,"entityKind":"session","entityId":"s","version":2})];
    assert_eq!(
        store.event_refs(&scope, &cursor(2), &events).unwrap().len(),
        1
    );
    assert_eq!(store.cursor(&scope).unwrap().through, 1);
    assert!(
        store
            .apply_event_rows(
                &scope,
                cursor(2),
                &events,
                vec![row("foreign", "session", "s", 2)]
            )
            .is_err()
    );
    assert_eq!(store.cursor(&scope).unwrap().through, 1);
    assert_eq!(store.items(&scope)[0].version, 1);
    let mut current = row("owner", "session", "s", 3);
    current["state"] = json!("working");
    store
        .apply_event_rows(&scope, cursor(2), &events, vec![current])
        .unwrap();
    assert_eq!(store.items(&scope)[0].value["state"], "working");
    assert_eq!(store.items(&scope)[0].version, 3);
    assert_eq!(store.cursor(&scope).unwrap().through, 2);
}

#[test]
fn hydration_uses_qualified_refs_and_omitted_items_instead_of_array_positions() {
    let mut store = EngineStore::default();
    let scope = scope("owner", "sessions");
    store
        .snapshot(
            scope.clone(),
            cursor(1),
            vec![row("owner", "session", "gone", 1)],
        )
        .unwrap();
    let events = vec![
        json!({"machine":"owner","seq":2,"entityKind":"session","entityId":"gone","version":2}),
        json!({"machine":"owner","seq":3,"entityKind":"session","entityId":"new","version":1}),
    ];
    store
        .apply_event_rows(
            &scope,
            cursor(3),
            &events,
            vec![row("owner", "session", "new", 1)],
        )
        .unwrap();
    assert_eq!(store.items(&scope).len(), 1);
    assert_eq!(store.items(&scope)[0].key.id, "new");
    assert_eq!(store.cursor(&scope).unwrap().through, 3);
}

#[test]
fn hydration_refuses_stale_or_unrequested_rows_without_acknowledging_the_page() {
    let mut store = EngineStore::default();
    let scope = scope("owner", "sessions");
    store
        .snapshot(
            scope.clone(),
            cursor(1),
            vec![row("owner", "session", "s", 1)],
        )
        .unwrap();
    let events =
        vec![json!({"machine":"owner","seq":2,"entityKind":"session","entityId":"s","version":2})];
    for rows in [
        vec![row("owner", "session", "s", 1)],
        vec![row("owner", "session", "other", 2)],
        vec![
            row("owner", "session", "s", 2),
            row("owner", "session", "s", 2),
        ],
    ] {
        assert!(
            store
                .apply_event_rows(&scope, cursor(2), &events, rows)
                .is_err()
        );
        assert_eq!(store.cursor(&scope).unwrap().through, 1);
        assert_eq!(store.items(&scope)[0].version, 1);
    }
}
