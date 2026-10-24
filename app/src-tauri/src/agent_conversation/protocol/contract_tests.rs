use super::*;
use serde_json::json;

#[test]
fn project_id_is_optional_on_ensure_and_present_on_records() {
    let request: EnsureAgentConversationRequest = serde_json::from_value(json!({
        "ownedId": "owned-local",
        "provider": "codex",
        "cwd": "/tmp/project",
        "nativeSessionId": null
    }))
    .unwrap();
    assert_eq!(request.project_id, None);

    let record = AgentConversationSessionRecord {
        owned_id: "owned-local".into(),
        can_change_checkout: true,
        execution_environment: ExecutionEnvironment::Local,
        remote_profile_id: None,
        provider: AgentConversationProvider::Codex,
        model: None,
        effort: None,
        cwd: "/tmp/project".into(),
        state: AgentRuntimeState::Ready,
        suspended: false,
        created_at_ms: 1,
        last_activity_at_ms: 2,
        active_turn_id: None,
        pending_permission: false,
        pending_input: false,
        background_work: vec![BackgroundWorkItem {
            id: "background-task:task-1".into(),
            kind: BackgroundWorkKind::Command,
            label: "Run checks".into(),
            started_at_ms: 3,
        }],
        native_session_id: None,
        project_id: Some("p1".into()),
        handle: None,
        project_group_key: String::new(),
        project_group_label: String::new(),
        orchestrator_marked_at_ms: None,
        helper_of: None,
        run_id: None,
        run_label: None,
        run_lead: None,
        numbered_after: None,
        meta: AgentConversationSessionMeta::default(),
    };
    let value = serde_json::to_value(&record).unwrap();
    assert_eq!(value["projectId"], "p1");
    assert_eq!(value["canChangeCheckout"], true);
    assert_eq!(value["backgroundWork"][0]["id"], "background-task:task-1");
    assert_eq!(value["backgroundWork"][0]["kind"], "command");
    assert_eq!(value["backgroundWork"][0]["label"], "Run checks");
    assert_eq!(value["backgroundWork"][0]["startedAtMs"], 3);
    assert!(value.get("meta").is_none());
    // Ungrouped, as a remote server sends it: the protocol-13 wire has no group fields.
    assert!(value.get("projectGroupKey").is_none() && value.get("projectGroupLabel").is_none());
    let mut cached = value.clone();
    cached.as_object_mut().unwrap().remove("canChangeCheckout");
    let cached: AgentConversationSessionRecord = serde_json::from_value(cached)
        .expect("saved remote sessions predate checkout support");
    assert!(!cached.can_change_checkout);
    let read_back: AgentConversationSessionRecord = serde_json::from_value(value).unwrap();
    assert_eq!(read_back.project_id.as_deref(), Some("p1"));
    assert_eq!(read_back.background_work[0].kind, BackgroundWorkKind::Command);
    assert_eq!(read_back.project_group_key, "");
}

#[test]
fn omitted_execution_environment_means_this_mac() {
    let request: EnsureAgentConversationRequest = serde_json::from_value(json!({
        "ownedId": "owned-local",
        "provider": "codex",
        "cwd": "/tmp/project",
        "nativeSessionId": null
    }))
    .unwrap();

    assert_eq!(request.execution_environment, ExecutionEnvironment::Local);
}

#[test]
fn unknown_config_categories_round_trip_losslessly() {
    let value = json!({
        "revision": 9,
        "provider": "codex",
        "implementation": { "name": "fixture", "version": "1" },
        "session": { "list": true, "load": true, "resume": true, "close": true, "steering": true },
        "prompt": { "text": true, "image": true, "embeddedContext": true, "resourceLinks": true },
        "interaction": {
            "permissions": true, "structuredUserInput": true, "toolTerminals": true,
            "plans": true, "tasks": true, "subagents": true
        },
        "configOptions": [{
            "id": "future", "label": "Future", "category": "provider.future/category",
            "value": { "nested": ["kept", 3] },
            "providerMetadata": { "provider_field": "kept" }
        }],
        "commands": []
    });
    let capabilities: AgentCapabilities = serde_json::from_value(value.clone()).unwrap();
    assert_eq!(
        capabilities.config_options[0].category,
        "provider.future/category"
    );
    assert_eq!(serde_json::to_value(capabilities).unwrap(), value);
}

#[test]
fn canonical_event_round_trips_without_acp_frames() {
    let value = json!({
        "type": "content.delta",
        "ownedId": "owned-a",
        "provider": "claude",
        "providerInstanceId": "provider-a",
        "generation": 3,
        "sequence": 12,
        "timestampMs": 1000,
        "nativeSessionId": "native-a",
        "turnId": "turn-a",
        "itemId": "item-a",
        "payload": { "channel": "assistant", "text": "hello" },
        "providerMetadata": { "claude.example": "retained" },
        "rawFrameReference": { "id": "frame-a", "redacted": true }
    });
    let event: AgentEvent = serde_json::from_value(value.clone()).unwrap();
    assert_eq!(event.event_type, AgentEventType::ContentDelta);
    assert_eq!(serde_json::to_value(event).unwrap(), value);

    let plan = AgentConversationPayload::Plan {
        items: vec![PlanItem {
            text: "Inspect the change".to_string(),
            status: "pending".to_string(),
        }],
    };
    assert_eq!(
        serde_json::from_value::<AgentConversationPayload>(
            serde_json::to_value(&plan).unwrap()
        )
        .unwrap(),
        plan
    );
}

#[test]
fn conversation_config_contract_uses_camel_case_fields() {
    let config = AgentConversationConfigState {
        model_labels: Default::default(),
        model_efforts: Default::default(),
        model_default_efforts: Default::default(),
        model: Some("gpt-5.6-sol".into()),
        available_models: vec!["gpt-5.6-sol".into(), "gpt-5.6-terra".into()],
        reasoning_effort: Some("xhigh".into()),
        available_efforts: vec!["low".into(), "xhigh".into()],
        approval_policy: Some("on-request".into()),
        available_approval_policies: vec!["untrusted".into(), "on-request".into()],
    };
    assert_eq!(
        serde_json::to_value(config).unwrap(),
        json!({
            "model": "gpt-5.6-sol",
            "availableModels": ["gpt-5.6-sol", "gpt-5.6-terra"],
            "reasoningEffort": "xhigh",
            "availableEfforts": ["low", "xhigh"],
            "approvalPolicy": "on-request",
            "availableApprovalPolicies": ["untrusted", "on-request"]
        })
    );
}

#[test]
fn user_input_contract_defaults_old_events_but_rejects_old_commands() {
    let event: AgentConversationPayload = serde_json::from_value(json!({
        "kind": "userInputRequested",
        "requestId": "input-1",
        "title": "Input requested",
        "fields": []
    }))
    .unwrap();
    assert!(matches!(
        event,
        AgentConversationPayload::UserInputRequested {
            can_decline: false,
            ..
        }
    ));

    assert!(serde_json::from_value::<RespondAgentConversationInputRequest>(json!({
        "ownedId": "owned",
        "generation": 1,
        "requestId": "input-1",
        "values": {},
        "cancelled": false
    }))
    .is_err());
    let request: RespondAgentConversationInputRequest = serde_json::from_value(json!({
        "ownedId": "owned",
        "generation": 1,
        "requestId": "input-1",
        "action": "accept",
        "content": { "choice": "stable" }
    }))
    .unwrap();
    assert_eq!(request.action, AgentUserInputAction::Accept);
    assert_eq!(request.content["choice"], "stable");
}
