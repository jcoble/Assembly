use super::*;

    #[tokio::test(flavor = "current_thread")]
    async fn missing_codex_rollout_with_stored_user_history_starts_fresh() {
        let root = temp_root();
        let log = root.join("resume-failure-codex.jsonl");
        let manifest = super::super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "resume_failure",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let mut ensure = request(
            root.to_str().unwrap(),
            "owned-resume-failure-codex",
            AgentConversationProvider::Codex,
        );
        // No rollout by this name exists under the native Codex session roots.
        ensure.native_session_id = Some("tsk-1508-codex-rollout-never-written".into());
        let connection = manager.ensure_inner(ensure).unwrap().0;
        {
            let mut sessions = manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&connection.owned_id).unwrap();
            record_payload_for_session(
                session,
                AgentConversationPayload::UserMessage {
                    item_id: "stored-user-message".into(),
                    text: "Earlier message".into(),
                    completed: true,
                    attachment_ids: Vec::new(),
                },
            )
            .unwrap();
        }

        let resumed = manager
            .activate(&connection.owned_id, connection.generation)
            .await
            .expect("a session Codex no longer holds starts fresh");

        assert_eq!(resumed.native_session_id.as_deref(), Some("new-session"));
        let snapshot = manager.snapshot(&connection.owned_id).unwrap().unwrap();
        assert!(!snapshot.suspended);
        assert!(!snapshot.events.iter().any(|event| matches!(
            &event.payload,
            AgentConversationPayload::Error { code, .. } if code == "session-resume-failed"
        )));
        let requests = fs::read_to_string(&log).unwrap();
        assert!(requests.contains(r#""method":"session/resume""#));
        assert!(requests.contains(r#""method":"session/new""#));
        manager
            .close(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        fs::remove_dir_all(root).unwrap();
    }


    #[tokio::test(flavor = "current_thread")]
    async fn resume_failure_with_user_history_stays_suspended() {
        let root = temp_root();
        let log = root.join("resume-failure-with-history.jsonl");
        let manifest = super::super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "resume_failure",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let mut ensure = request(
            root.to_str().unwrap(),
            "owned-resume-failure-with-history",
            AgentConversationProvider::Codex,
        );
        let native_id = format!("tsk-1508-history-{}", uuid::Uuid::new_v4());
        let native_root = transcript::home_dir().unwrap().join(".codex/sessions").join(&native_id);
        fs::create_dir_all(&native_root).unwrap();
        fs::write(native_root.join(format!("rollout-{native_id}.jsonl")),
            r#"{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Earlier message"}]}}"#).unwrap();
        ensure.native_session_id = Some(native_id);
        let connection = manager.ensure_inner(ensure).unwrap().0;
        {
            let mut sessions = manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&connection.owned_id).unwrap();
            record_payload_for_session(
                session,
                AgentConversationPayload::UserMessage {
                    item_id: "stored-user-message".into(),
                    text: "Earlier message".into(),
                    completed: true,
                    attachment_ids: Vec::new(),
                },
            )
            .unwrap();
        }

        assert!(manager
            .activate(&connection.owned_id, connection.generation)
            .await
            .unwrap_err()
            .contains("could not be resumed"));
        let snapshot = manager.snapshot(&connection.owned_id).unwrap().unwrap();
        assert!(snapshot.suspended);
        // The card has to name the cause, not just report that a resume failed.
        let resume_error = snapshot
            .events
            .iter()
            .find_map(|event| match &event.payload {
                AgentConversationPayload::Error { code, message, .. }
                    if code == "session-resume-failed" =>
                {
                    Some(message.clone())
                }
                _ => None,
            })
            .expect("resume failure is recorded");
        assert!(resume_error.starts_with("The stored provider session could not be resumed: "));
        assert!(resume_error.len() > "The stored provider session could not be resumed: ".len());
        let requests = fs::read_to_string(&log).unwrap();
        assert!(requests.contains(r#""method":"session/resume""#));
        assert!(!requests.contains(r#""method":"session/new""#));
        fs::remove_dir_all(native_root).unwrap();
        fs::remove_dir_all(root).unwrap();
    }
