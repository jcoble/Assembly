use super::*;

#[tokio::test(flavor = "current_thread")]
async fn respond_agent_conversation_permission_round_trips_through_fake_agent() {
    let fixture = fixture_manager_with_acp_session("permission_midturn").await;
    let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
    let sink = Arc::clone(&seen);
    fixture
        .manager
        .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
    fixture
        .manager
        .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
        .await
        .expect("prompt starts");
    wait_until(|| {
        seen.lock().unwrap().iter().any(|event| {
            matches!(
                event.payload,
                AgentConversationPayload::Approval {
                    state: super::super::super::protocol::ApprovalState::Requested,
                    ..
                }
            )
        })
    })
    .await;
    let request_id = seen
        .lock()
        .unwrap()
        .iter()
        .find_map(|event| match &event.payload {
            AgentConversationPayload::Approval { request_id, .. } => Some(request_id.clone()),
            _ => None,
        })
        .unwrap();
    fixture
        .manager
        .respond_legacy_approval(
            &fixture.owned_id,
            fixture.generation,
            request_id,
            super::super::super::protocol::ApprovalDecision::Accept,
        )
        .await
        .expect("permission response");
    wait_until(|| {
        seen.lock().unwrap().iter().any(|event| {
            matches!(
                event.payload,
                AgentConversationPayload::Turn {
                    state: super::super::super::protocol::TurnState::Completed,
                    ..
                }
            )
        })
    })
    .await;

    assert!(seen.lock().unwrap().iter().any(|event| {
        matches!(
            event.payload,
            AgentConversationPayload::Approval {
                state: ApprovalState::Accepted,
                ..
            }
        )
    }));
    assert!(seen.lock().unwrap().iter().any(|event| {
        matches!(
            &event.payload,
            AgentConversationPayload::AssistantDelta { delta, .. }
                if delta == "continued after approval"
        )
    }));
    let fixture_log = fs::read_to_string(fixture.root.join("permission_midturn.jsonl"))
        .expect("permission fixture log");
    assert!(
        fixture_log.contains(r#""outcome":{"outcome":"selected","optionId":"allow""#),
        "permission response must select a valid allow option: {fixture_log}"
    );

    fixture
        .manager
        .close(&fixture.owned_id, fixture.generation)
        .await
        .unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}

#[tokio::test(flavor = "current_thread")]
async fn a_workflow_policy_declines_a_denied_command_without_asking() {
    let fixture = fixture_manager_with_acp_session("permission_midturn").await;
    fixture
        .manager
        .set_guard_policy(&fixture.owned_id, crate::workflow::policy::GuardPolicy::default())
        .expect("policy set");
    let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
    let sink = Arc::clone(&seen);
    fixture
        .manager
        .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
    fixture
        .manager
        .prompt(&fixture.owned_id, fixture.generation, test_prompt("merge it"))
        .await
        .expect("prompt starts");
    wait_until(|| {
        seen.lock().unwrap().iter().any(|event| {
            matches!(
                event.payload,
                AgentConversationPayload::Turn {
                    state: super::super::super::protocol::TurnState::Completed,
                    ..
                }
            )
        })
    })
    .await;
    let approvals: Vec<ApprovalState> = seen
        .lock()
        .unwrap()
        .iter()
        .filter_map(|event| match &event.payload {
            AgentConversationPayload::Approval { state, .. } => Some(*state),
            _ => None,
        })
        .collect();
    assert_eq!(approvals, vec![ApprovalState::Declined], "nobody is asked about a denied command");
    assert!(fixture.manager.pending_permissions().is_empty());
    let fixture_log = fs::read_to_string(fixture.root.join("permission_midturn.jsonl"))
        .expect("permission fixture log");
    assert!(
        fixture_log.contains(r#""outcome":{"outcome":"selected","optionId":"reject""#),
        "a denied command must be rejected: {fixture_log}"
    );
    fixture
        .manager
        .close(&fixture.owned_id, fixture.generation)
        .await
        .unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}
