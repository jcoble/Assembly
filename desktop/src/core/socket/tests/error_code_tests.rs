use super::ErrorCode;

#[test]
fn actual_uncertain_wire_code_preserves_the_captured_machine_and_is_not_failed() {
    assert_eq!(
        ErrorCode::from_wire("uncertain", Some("actual-owner".into())),
        ErrorCode::Uncertain {
            machine: "actual-owner".into()
        }
    );
    assert_eq!(ErrorCode::from_wire("failed", None), ErrorCode::Failed);
    assert_eq!(ErrorCode::from_wire("bad_args", None), ErrorCode::BadArgs);
    assert_eq!(ErrorCode::from_wire("not_found", None), ErrorCode::NotFound);
    assert_eq!(
        ErrorCode::from_wire("unknown-future-code", None),
        ErrorCode::Failed
    );
}

#[test]
fn a_refused_checkout_read_keeps_its_code() {
    for code in [
        "checkout_root_changed",
        "checkout_unavailable",
        "source_changed",
    ] {
        assert_eq!(
            ErrorCode::from_wire(code, None),
            ErrorCode::Refused { code: code.into() }
        );
    }
}

#[test]
fn an_actual_error_frame_keeps_uncertainty_distinct_from_definite_failure() {
    use crate::core::socket::{
        fake_backend::{FakeBackend, Reply, hello},
        outcome::Outcome,
        test_client::{TestClient, outcome},
    };
    use serde_json::json;
    let backend = FakeBackend::start(|frame| {
        if let Some(welcome) = hello(frame) {
            return welcome;
        }
        if frame["t"] == "call" {
            return vec![Reply::Frame(json!({
                "t":"error", "id":frame["id"], "code":"uncertain", "machine":"actual-owner",
                "message":"This may have completed. Refresh before trying again."
            }))];
        }
        vec![]
    });
    let client = TestClient::start(backend.port);
    let (_, call) = client.call("mutate", None);
    let Outcome::Failed(error) = outcome(call) else {
        panic!("uncertain frame must remain a typed failure")
    };
    assert_eq!(
        error.code,
        ErrorCode::Uncertain {
            machine: "actual-owner".into()
        }
    );
    assert_eq!(
        error.message,
        "This may have completed. Refresh before trying again."
    );
}

#[test]
fn actual_open_target_refusals_remain_definitive_and_keep_their_exact_codes() {
    for code in [
        "source_changed",
        "native_card_changed",
        "native_history_unavailable",
        "owner_validation_required",
        "unknown_engine_session",
        "unknown_session",
        "nonresumable",
        "operation_id_conflict",
        "install_busy",
        "operation_capacity",
        "operation_unknown",
    ] {
        assert_eq!(
            ErrorCode::from_wire(code, None),
            ErrorCode::Refused { code: code.into() }
        );
    }
    assert_eq!(
        ErrorCode::from_wire("not_a_known_reason", None),
        ErrorCode::Failed
    );
}
