use super::open_target::{build_args, parse_response};
use crate::core::{
    engine::SessionKey,
    intents::session_open::{SessionOpenRequest, SessionOpenTarget},
};
use serde_json::{Value, json};

fn request(target: SessionOpenTarget) -> SessionOpenRequest {
    SessionOpenRequest {
        key: SessionKey {
            machine: "actual-owner".into(),
            id: "parent".into(),
        },
        request_id: "external-request".into(),
        target,
    }
}
fn response() -> Value {
    json!({"kind":"engine","machine":"actual-owner","generation":0,"routeMachine":null,
        "sessionId":"parent","parentSessionId":null,"itemId":null,"seq":null,"payloadSessionId":null,
        "readOnly":false,"hasHistoryRows":false,"originalKey":{"machine":"actual-owner","id":"parent"},
        "requestId":9,"checkoutRoot":null,"expectedSource":{"machine":"actual-owner","generation":0,"routeMachine":null},"transcriptOnly":true})
}
#[test]
fn wire_uses_the_numeric_request_generation_without_rewriting_external_correlation() {
    let request = request(SessionOpenTarget::Engine);
    assert_eq!(
        build_args(&request, 9).unwrap(),
        json!({"kind":"engine","machine":"actual-owner","session":"parent","requestId":9})
    );
    let target = parse_response(response(), &request, 9).unwrap();
    assert_eq!(target.key, request.key);
    assert_eq!(request.request_id, "external-request");
    assert!(
        !target.has_history_rows,
        "an empty Engine session is prepared through its real empty first page"
    );
}
#[test]
fn mismatched_original_reference_echo_or_source_cannot_admit_a_target() {
    let request = request(SessionOpenTarget::Engine);
    for (field, value) in [
        ("requestId", json!(10)),
        ("originalKey", json!({"machine":"other","id":"parent"})),
        (
            "expectedSource",
            json!({"machine":"other","generation":0,"routeMachine":null}),
        ),
    ] {
        let mut read = response();
        read[field] = value;
        assert!(parse_response(read, &request, 9).is_err());
    }
}
#[test]
fn a_remote_source_keeps_the_exact_captured_generation_and_route() {
    let request = request(SessionOpenTarget::Engine);
    let mut read = response();
    read["generation"] = json!(17);
    read["routeMachine"] = json!("captured-route");
    read["expectedSource"] =
        json!({"machine":"actual-owner","generation":17,"routeMachine":"captured-route"});
    let target = parse_response(read.clone(), &request, 9).unwrap();
    assert_eq!(target.source.generation, 17);
    assert_eq!(
        target.source.route_machine.as_deref(),
        Some("captured-route")
    );
    read["expectedSource"]["generation"] = json!(18);
    assert!(parse_response(read, &request, 9).is_err());
}
#[test]
fn native_uses_the_persisted_parent_row_and_exact_legacy_payload_value() {
    let parent = SessionKey {
        machine: "actual-owner".into(),
        id: "parent".into(),
    };
    let request = request(SessionOpenTarget::Native {
        parent,
        item_id: "subagent:recorded-id".into(),
        seq: 7,
        payload_session_id: "legacy-child".into(),
    });
    let args = build_args(&request, 9).unwrap();
    assert_eq!(args["session"], "parent");
    assert_eq!(args["payloadSessionId"], "legacy-child");
    assert_eq!(
        args["parent"],
        json!({"machine":"actual-owner","id":"parent"})
    );
    let mut read = response();
    read["kind"] = json!("native");
    read["sessionId"] = json!("recorded-child");
    read["parentSessionId"] = json!("parent");
    read["itemId"] = json!("subagent:recorded-id");
    read["seq"] = json!(7);
    read["payloadSessionId"] = json!("legacy-child");
    read["readOnly"] = json!(true);
    read["hasHistoryRows"] = json!(true);
    assert_eq!(
        parse_response(read.clone(), &request, 9).unwrap().key.id,
        "recorded-child"
    );
    read["hasHistoryRows"] = json!(false);
    assert!(parse_response(read, &request, 9).is_err());
}
#[test]
fn recorded_checkout_metadata_does_not_grant_file_authority_or_make_history_writable() {
    let request = request(SessionOpenTarget::Historical);
    let mut read = response();
    read["kind"] = json!("historical");
    read["readOnly"] = json!(true);
    read["checkoutRoot"] = json!("/recorded-only");
    read["transcriptOnly"] = json!(false);
    let target = parse_response(read, &request, 9).unwrap();
    assert!(target.read_only);
    assert_eq!(
        target.recorded_checkout_root.as_deref(),
        Some("/recorded-only")
    );
}
