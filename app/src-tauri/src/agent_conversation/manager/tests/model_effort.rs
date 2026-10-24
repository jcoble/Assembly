use super::*;

#[tokio::test(flavor = "current_thread")]
async fn saved_claude_model_aliases_still_open_after_a_restart() {
    for model in ["opus", "claude-opus-5", "claude-fable-5"] {
        let root = temp_root();
        let database = root.join("sessions.db");
        let log = root.join("saved-model.jsonl");
        let manifest = super::super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "suspend_saved_model",
        );
        let providers = || {
            ProviderRegistry::new([(AgentConversationProvider::Claude, manifest.clone())]).unwrap()
        };
        let manager = AgentRuntimeManager::open(providers(), &database).unwrap();
        let connection = manager
            .ensure_async(request(
                root.to_str().unwrap(),
                "saved-model",
                AgentConversationProvider::Claude,
            ))
            .await
            .unwrap();
        manager
            .activate(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        manager
            .update_session_meta(UpdateAgentConversationSessionMetaRequest {
                owned_id: connection.owned_id.clone(),
                model: Some(model.into()),
                effort: None,
                meta: renamed_meta("Saved model"),
            })
            .unwrap();
        manager
            .suspend_if_quiescent(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        drop(manager);
        let recovered = AgentRuntimeManager::open(providers(), &database).unwrap();
        assert_eq!(
            recovered
                .conversation_config(&connection.owned_id)
                .unwrap()
                .model
                .as_deref(),
            Some(model)
        );
        recovered
            .activate(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        let frames = fs::read_to_string(&log).unwrap();
        assert!(
            frames.contains("session/resume"),
            "the saved native session must resume: {frames}"
        );
        assert!(
            frames.contains(&format!("\"model\":\"{model}\"")),
            "the saved model must reach the adapter: {frames}"
        );
        recovered
            .close(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        fs::remove_dir_all(root).unwrap();
    }
}

#[test]
fn sessions_without_an_adapter_do_not_invent_effort_choices() {
    for provider in [
        AgentConversationProvider::Codex,
        AgentConversationProvider::Claude,
        AgentConversationProvider::Antigravity,
    ] {
        let root = temp_root();
        let manager = AgentRuntimeManager::new(ProviderRegistry::default());
        let connection = manager
            .ensure_inner(request(root.to_str().unwrap(), "draft", provider))
            .unwrap()
            .0;
        assert!(
            connection.config.available_efforts.is_empty(),
            "{provider:?} must wait for its adapter"
        );
        fs::remove_dir_all(root).unwrap();
    }
}

#[tokio::test(flavor = "current_thread")]
async fn absence_of_a_mutable_effort_control_is_provider_neutral() {
    let mut results = Vec::new();
    for provider in [
        AgentConversationProvider::Codex,
        AgentConversationProvider::Claude,
        AgentConversationProvider::Antigravity,
    ] {
        let fixture = fixture_manager_with_provider("standard_config", provider, None).await;
        let config = fixture
            .manager
            .conversation_config(&fixture.owned_id)
            .unwrap();
        let error = fixture
            .manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: fixture.owned_id.clone(),
                generation: fixture.generation,
                model: None,
                reasoning_effort: Some("high".into()),
                approval_policy: None,
            })
            .await
            .unwrap_err();
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
        results.push((provider, config, error));
    }
    for (provider, config, error) in results {
        assert!(
            config.available_efforts.is_empty(),
            "{provider:?} must not invent a catalogue"
        );
        assert_eq!(
            error, "Effort is set when the session starts. Start a new session to change it.",
            "{provider:?}"
        );
    }
}

#[tokio::test(flavor = "current_thread")]
async fn metadata_updates_do_not_add_effort_choices() {
    let fixture =
        fixture_manager_with_provider("standard_config", AgentConversationProvider::Claude, None)
            .await;
    fixture
        .manager
        .sessions
        .lock()
        .unwrap()
        .get_mut(&fixture.owned_id)
        .unwrap()
        .config
        .available_efforts
        .clear();
    fixture
        .manager
        .update_session_meta(UpdateAgentConversationSessionMetaRequest {
            owned_id: fixture.owned_id.clone(),
            model: None,
            effort: Some("medium".into()),
            meta: renamed_meta("Saved"),
        })
        .unwrap();
    let config = fixture
        .manager
        .conversation_config(&fixture.owned_id)
        .unwrap();
    fixture
        .manager
        .close(&fixture.owned_id, fixture.generation)
        .await
        .unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
    assert_eq!(config.reasoning_effort.as_deref(), Some("medium"));
    assert!(config.available_efforts.is_empty());
}
