//! Source-derived display fixtures, not a provider/native acceptance run.
use super::engine_items::{NeedAnswerDisplay, NeedItem};
use super::{EngineItem, EngineKey};
use serde_json::json;

fn item(machine: &str) -> EngineItem {
    EngineItem {
        key: EngineKey {
            machine: machine.into(),
            kind: "needs_me".into(),
            id: "same".into(),
        },
        version: 3,
        value: json!({"machine":machine,"entityKind":"needs_me","id":"same","version":3,
            "sessionId":"session-same","kind":"question","state":"open","text":"Actual question",
            "displayNumber":7,"title":"Question","recommendation":null,"inputMode":"text",
            "schema":null,"options":[],"freeText":true,"answer":null,"answerComplete":null,
            "answerUtf8Bytes":null,"answerText":null,"answeredOrigin":null}),
    }
}

#[test]
fn need_identity_and_session_remain_qualified_by_actual_source() {
    let local = NeedItem::of(&item("local-engine")).unwrap();
    let remote = NeedItem::of(&item("remote-engine")).unwrap();
    assert_ne!(local.key, remote.key);
    assert_ne!(local.session, remote.session);
    assert_eq!(local.display_number, Some(7));
    assert_eq!(local.presentation.as_ref().unwrap().input_mode, "text");
    assert_eq!(local.answer_display(), NeedAnswerDisplay::Pending);
    let mut invalid = item("local-engine");
    invalid.value["machine"] = json!("remote-engine");
    assert!(NeedItem::of(&invalid).is_none());
}

#[test]
fn absent_or_oversized_presentation_disables_actions_without_inventing_metadata() {
    let mut row = item("owner");
    for key in [
        "title",
        "recommendation",
        "inputMode",
        "schema",
        "options",
        "freeText",
    ] {
        row.value[key] = serde_json::Value::Null;
    }
    row.value["optionsJson"] = json!("[\"Approve\"]");
    let need = NeedItem::of(&row).unwrap();
    assert!(need.presentation.is_none());
    assert_eq!(need.text.as_deref(), Some("Actual question"));
    assert_eq!(
        need.presentation_notice(),
        Some("Answer controls are unavailable for this item.")
    );
}

#[test]
fn an_omitted_large_answer_is_explicit_and_never_presented_as_empty_or_complete() {
    let mut row = item("owner");
    row.value["state"] = json!("answered");
    row.value["answerComplete"] = json!(false);
    row.value["answerUtf8Bytes"] = json!(2_097_152);
    let need = NeedItem::of(&row).unwrap();
    assert_eq!(
        need.answer_display(),
        NeedAnswerDisplay::Omitted {
            utf8_bytes: 2_097_152
        }
    );
    assert_eq!(
        need.answer_notice().as_deref(),
        Some("Answer exceeds the display limit (2097152 bytes).")
    );
}

#[test]
fn complete_answers_and_trusted_origin_keep_the_original_values() {
    let mut row = item("owner");
    row.value["state"] = json!("answered");
    row.value["answer"] = json!("  Exact answer  ");
    row.value["answerText"] = json!("  Exact answer  ");
    row.value["answerComplete"] = json!(true);
    row.value["answerUtf8Bytes"] = json!(18);
    row.value["answeredOrigin"] =
        json!({"channel":"phone","deviceId":"device-real","surface":null});
    let need = NeedItem::of(&row).unwrap();
    assert_eq!(
        need.answer_display(),
        NeedAnswerDisplay::Complete {
            answer: json!("  Exact answer  "),
            text: Some("  Exact answer  ".into())
        }
    );
    assert_eq!(
        need.answered_origin.unwrap().device_id.as_deref(),
        Some("device-real")
    );
}

#[test]
fn engine_session_projection_keeps_recorded_working_folder_and_operational_fields() {
    let row = EngineItem {
        key: EngineKey {
            machine: "owner".into(),
            kind: "session".into(),
            id: "child".into(),
        },
        version: 4,
        value: json!({"machine":"owner","entityKind":"session","id":"child","version":4,
            "provider":"configured-provider","model":null,"folder":"/actual/owner/worktree","title":"Worker",
            "state":"idle","activityState":"idle","epoch":12,"parentId":"parent","parentMachine":"parent-owner","runId":null}),
    };
    let session = super::engine_items::SessionItem::of(&row).unwrap();
    assert_eq!(session.folder, "/actual/owner/worktree");
    assert_eq!(session.epoch, 12);
    assert_eq!(session.parent_id.as_deref(), Some("parent"));
    assert_eq!(session.parent_machine.as_deref(), Some("parent-owner"));
    assert_eq!(session.key.machine, "owner");
}

#[test]
fn only_a_recorded_steer_grant_lets_a_session_take_a_message_mid_turn() {
    let row = |steer: Option<bool>| {
        let mut value = json!({"machine":"owner","entityKind":"session","id":"s","version":1,
            "provider":"any","model":null,"folder":"/f","title":null,"state":"running",
            "activityState":"running","epoch":1,"parentId":null,"parentMachine":null,"runId":"r"});
        if let Some(steer) = steer {
            value["canSteer"] = json!(steer);
        }
        EngineItem {
            key: EngineKey {
                machine: "owner".into(),
                kind: "session".into(),
                id: "s".into(),
            },
            version: 1,
            value,
        }
    };
    let can = |steer| {
        super::engine_items::SessionItem::of(&row(steer))
            .unwrap()
            .can_steer
    };
    assert!(can(Some(true)));
    assert!(!can(Some(false)));
    assert!(!can(None));
}

#[test]
fn need_addressee_requires_its_actual_exported_owner() {
    let mut row = item("child-owner");
    row.value["addresseeSessionId"] = json!("same");
    assert!(NeedItem::of(&row).unwrap().addressee().is_none());
    row.value["addresseeMachine"] = json!("parent-owner");
    assert_eq!(
        NeedItem::of(&row).unwrap().addressee(),
        Some(super::SessionKey {
            machine: "parent-owner".into(),
            id: "same".into()
        })
    );
    row.value["addresseeMachine"] = json!("");
    assert!(NeedItem::of(&row).unwrap().addressee().is_none());
}
