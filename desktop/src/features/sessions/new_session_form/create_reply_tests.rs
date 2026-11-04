use super::CreateReply;
use serde_json::json;
#[test]
fn initial_checkout_binding_is_read_verbatim_and_unknown_state_is_never_a_confirmation() {
    let operation = "8b37d022-bad2-42d0-86b8-9739c08df285";
    let reply:CreateReply=serde_json::from_value(json!({"projectId":null,"sessionId":"accepted","checkoutOperationId":operation,"checkoutState":null,"state":"idle"})).unwrap();
    let accepted = reply.accepted(true).unwrap().unwrap();
    assert_eq!(accepted.session_id, "accepted");
    assert_eq!(accepted.initial_operation_id.as_deref(), Some(operation));
    assert!(reply.checkout_state.is_none());
    let missing:CreateReply=serde_json::from_value(json!({"projectId":null,"sessionId":"accepted","checkoutOperationId":null,"checkoutState":"ready","state":"idle"})).unwrap();
    assert!(missing.accepted(true).is_err());
}
#[test]
fn an_ordinary_no_checkout_start_does_not_invent_an_operation_and_only_an_empty_lookup_allows_retry()
 {
    let ordinary:CreateReply=serde_json::from_value(json!({"projectId":null,"sessionId":"ordinary","checkoutOperationId":null,"checkoutState":"ready","state":"idle"})).unwrap();
    assert!(
        ordinary
            .accepted(false)
            .unwrap()
            .unwrap()
            .initial_operation_id
            .is_none()
    );
    let missing:CreateReply=serde_json::from_value(json!({"projectId":null,"sessionId":null,"checkoutOperationId":null,"checkoutState":null,"state":null})).unwrap();
    assert!(missing.accepted(true).unwrap().is_none());
    let contradictory:CreateReply=serde_json::from_value(json!({"projectId":null,"sessionId":null,"checkoutOperationId":null,"checkoutState":"ready","state":"idle"})).unwrap();
    assert!(contradictory.accepted(false).is_err());
    assert!(serde_json::from_value::<CreateReply>(json!({})).is_err());
    assert!(
        serde_json::from_value::<CreateReply>(json!({"projectId":null,"sessionId":null})).is_err()
    );
}

#[test]
fn replay_reports_the_accepted_project_and_never_relabels_session_a_as_requested_project_b() {
    let reply:CreateReply=serde_json::from_value(json!({"sessionId":"session-a","projectId":"project-a","checkoutOperationId":null,"checkoutState":"ready","state":"idle"})).unwrap();
    let accepted = reply.accepted(false).unwrap().unwrap();
    assert_eq!(accepted.session_id, "session-a");
    assert_eq!(accepted.project_id.as_deref(), Some("project-a"));
    assert!(accepted.matches_project(Some("project-a")));
    assert!(!accepted.matches_project(Some("project-b")));
    assert!(!accepted.matches_project(None));
    let missing:CreateReply=serde_json::from_value(json!({"sessionId":"session-a","projectId":null,"checkoutOperationId":null,"checkoutState":"ready","state":"idle"})).unwrap();
    assert!(
        !missing
            .accepted(false)
            .unwrap()
            .unwrap()
            .matches_project(Some("project-a"))
    );
    assert!(
        serde_json::from_value::<CreateReply>(
            json!({"sessionId":"session-a","checkoutOperationId":null})
        )
        .is_err()
    );
}
