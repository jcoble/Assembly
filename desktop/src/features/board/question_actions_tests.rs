//! Fixtures follow what `DisplayQueries.cs` sends.
use serde_json::{Value, json};

use super::*;
use crate::core::engine::{EngineItem, EngineKey};

fn item(kind: &str, id: &str, machine: &str, mut value: Value) -> EngineItem {
    value["entityKind"] = json!(kind);
    value["machine"] = json!(machine);
    value["id"] = json!(id);
    value["version"] = json!(3);
    EngineItem {
        key: EngineKey {
            machine: machine.into(),
            kind: kind.into(),
            id: id.into(),
        },
        version: 3,
        value,
    }
}

fn need() -> NeedItem {
    NeedItem::of(&item(
        "needs_me",
        "need-1",
        "remote",
        json!({"sessionId":"asker","kind":"question","state":"open"}),
    ))
    .unwrap()
}

fn session(id: &str, machine: &str) -> SessionItem {
    SessionItem::of(&item("session", id, machine, json!({"provider":"codex","folder":"/w","state":"running","activityState":"working","epoch":9}))).unwrap()
}

#[test]
fn explain_more_names_the_exact_need_and_sends_only_a_message() {
    let command = explain_more(&need(), &session("asker", "remote")).unwrap();
    assert_eq!(command.command["kind"], "send_message");
    assert_eq!(command.command["toSessionId"], "asker");
    assert_eq!(command.command["mode"], "prompt");
    assert!(command.command["text"].as_str().unwrap().contains("need-1"));
    assert_eq!((command.expected_version, command.epoch), (0, 9));
    assert_eq!(command.machine.as_deref(), Some("remote"));
    assert_ne!(
        command.entity_id, "need-1",
        "the message is its own entity, so the need stays open"
    );
}

#[test]
fn the_same_session_id_on_another_machine_is_not_the_asker() {
    assert!(explain_more(&need(), &session("asker", "local")).is_err());
    assert!(explain_more(&need(), &session("other", "remote")).is_err());
}
