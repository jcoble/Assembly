use super::helper_stop::{command, refusal_text};
use crate::core::engine::{EngineKey, SessionKey, engine_items::SessionItem};
fn child(machine: &str) -> SessionItem {
    SessionItem {
        key: EngineKey {
            machine: machine.into(),
            kind: "session".into(),
            id: "child".into(),
        },
        version: 8,
        parent_id: Some("parent".into()),
        parent_machine: Some("parent-owner".into()),
        run_id: None,
        title: None,
        provider: "provider".into(),
        model: None,
        folder: "/recorded-display".into(),
        state: "running".into(),
        activity_state: "working".into(),
        epoch: 12,
        can_steer: false,
    }
}
#[test]
fn helper_stop_uses_the_existing_engine_command_with_actual_child_owner_version_and_epoch() {
    let parent = SessionKey {
        machine: "parent-owner".into(),
        id: "parent".into(),
    };
    let command = command(&parent, &[child("child-owner")], "child").unwrap();
    assert_eq!(command.machine.as_deref(), Some("child-owner"));
    assert_eq!(command.entity_id, "child");
    assert_eq!(command.expected_version, 8);
    assert_eq!(command.epoch, 12);
    assert_eq!(command.command, serde_json::json!({"kind":"stop_session"}));
    assert!(
        !serde_json::to_value(command)
            .unwrap()
            .to_string()
            .contains("stopTask")
    );
}
#[test]
fn missing_foreign_parent_provenance_or_ambiguous_child_identity_refuses() {
    let parent = SessionKey {
        machine: "parent-owner".into(),
        id: "parent".into(),
    };
    let mut missing = child("child-owner");
    missing.parent_machine = None;
    assert!(command(&parent, &[missing], "child").is_err());
    assert!(command(&parent, &[child("a"), child("b")], "child").is_err());
    assert!(command(&parent, &[child("a")], "different").is_err());
}
#[test]
fn a_helper_still_moving_says_so_in_words() {
    assert_eq!(
        refusal_text(Some("version_conflict".into())),
        "The helper just changed. Try Stop again."
    );
    assert_eq!(refusal_text(Some("busy".into())), "busy");
    assert_eq!(refusal_text(None), "The helper Stop was refused.");
}
