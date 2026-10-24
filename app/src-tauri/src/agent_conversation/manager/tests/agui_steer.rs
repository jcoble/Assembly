//! TSK-1592 rule 1: nothing is sent while the agent waits on an answer. The
//! steer path refuses, so the phone, the relay and the desktop all hold.
use super::*;

#[tokio::test(flavor = "current_thread")]
async fn a_steer_is_refused_while_a_permission_waits_and_nothing_is_recorded() {
    let fixture = fixture_manager_with_acp_session("steering").await;
    {
        let mut sessions = fixture.manager.sessions.lock().unwrap();
        let session = sessions.get_mut(&fixture.owned_id).unwrap();
        session.active_turn_id = Some("turn-running".into());
        session.state = AgentRuntimeState::WaitingApproval;
        let payload = AgentConversationPayload::Approval {
            request_id: "permission-1".into(), state: ApprovalState::Requested, summary: "Write a file".into(), options: Vec::new(),
        };
        session.permission_requests.insert("permission-1".into(), PendingPermission {
            wire_id: json!(1), options: Vec::new(), summary: "Write a file".into(), command: None, child_scoped: false,
            event: pending_test_event(&fixture.owned_id, fixture.generation, payload),
        });
    }
    let error = fixture.manager.prompt(&fixture.owned_id, fixture.generation, test_prompt("also do this")).await.unwrap_err();
    assert!(error.contains("waiting"), "{error}");
    assert!(!fixture.manager.list_events(&fixture.owned_id, 0).unwrap().iter()
        .any(|event| matches!(event.payload, AgentConversationPayload::UserMessage { .. })));
    let log = fs::read_to_string(fixture.root.join("steering.jsonl")).unwrap_or_default();
    assert!(!log.contains("_session/steering"), "{log}");
    fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}
