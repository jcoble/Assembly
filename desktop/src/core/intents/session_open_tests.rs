use super::session_open::{SessionOpenRequest, SessionOpenTarget};
use crate::core::engine::SessionKey;

fn parent() -> SessionKey {
    SessionKey {
        machine: "actual-engine".into(),
        id: "assembly-parent".into(),
    }
}
fn native() -> SessionOpenRequest {
    SessionOpenRequest {
        request_id: "request".into(),
        key: parent(),
        target: SessionOpenTarget::Native {
            parent: parent(),
            item_id: "subagent:recorded-acp-id".into(),
            seq: 17,
            payload_session_id: "assembly-parent".into(),
        },
    }
}

#[test]
fn native_request_carries_the_actual_parent_row_instead_of_claiming_a_child_key() {
    let request = native();
    assert!(request.validate_shape().is_ok());
    assert_eq!(request.key, parent());
    let SessionOpenTarget::Native {
        payload_session_id,
        item_id,
        seq,
        ..
    } = request.target
    else {
        panic!()
    };
    assert_eq!(payload_session_id, "assembly-parent");
    assert_eq!(item_id, "subagent:recorded-acp-id");
    assert_eq!(seq, 17);
}

#[test]
fn contradictory_source_or_parent_reference_is_refused_before_any_read() {
    let mut request = native();
    request.key.machine = "different-engine".into();
    assert!(request.validate_shape().is_err());
    let mut request = native();
    let SessionOpenTarget::Native { parent, .. } = &mut request.target else {
        panic!()
    };
    parent.id = "different-parent".into();
    assert!(request.validate_shape().is_err());
}

#[test]
fn historical_is_an_explicit_request_and_never_a_fallback_from_engine_not_found() {
    let request = SessionOpenRequest {
        key: parent(),
        request_id: "history-request".into(),
        target: SessionOpenTarget::Historical,
    };
    assert!(request.validate_shape().is_ok());
    assert_ne!(request.target, SessionOpenTarget::Engine);
}

#[test]
fn a_legacy_payload_value_is_carried_exactly_for_backend_record_validation() {
    let mut request = native();
    let SessionOpenTarget::Native {
        payload_session_id, ..
    } = &mut request.target
    else {
        panic!()
    };
    *payload_session_id = "persisted-legacy-value".into();
    // Shape validation grants no navigation: the backend must compare the exact persisted value and binding.
    assert!(request.validate_shape().is_ok());
}
