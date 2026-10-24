use super::*;

#[tokio::test(flavor = "current_thread")]
async fn checkout_preserves_native_id_and_resumes_each_provider_in_new_cwd() {
    for provider in [
        AgentConversationProvider::Codex,
        AgentConversationProvider::Claude,
        AgentConversationProvider::Antigravity,
    ] {
        let fixture = fixture_manager_with_provider("suspend_checkout", provider, None).await;
        let native_id = fixture.manager.list_sessions().unwrap()[0].native_session_id.clone();
        let cwd = fixture.root.join("second");
        fs::create_dir(&cwd).unwrap();
        let change = || ChangeAgentConversationCheckoutRequest {
            owned_id: fixture.owned_id.clone(),
            generation: fixture.generation,
            cwd: cwd.display().to_string(),
        };
        fixture.manager.sessions.lock().unwrap().get_mut(&fixture.owned_id).unwrap()
            .active_turn_id = Some("busy".into());
        assert!(fixture.manager.change_checkout(change()).await.unwrap_err().contains("quiescent"));
        fixture.manager.sessions.lock().unwrap().get_mut(&fixture.owned_id).unwrap()
            .active_turn_id = None;
        let moved = fixture.manager.change_checkout(change()).await.unwrap();
        assert!(moved.can_change_checkout);
        assert!(moved.suspended);
        assert_eq!(moved.native_session_id, native_id);
        assert_eq!(moved.cwd, cwd.display().to_string());
        fixture.manager.ensure_async(request(cwd.to_str().unwrap(), &fixture.owned_id, provider)).await.unwrap();
        let resumed = fixture.manager.activate(&fixture.owned_id, fixture.generation).await.unwrap();
        assert_eq!(resumed.native_session_id, native_id);
        let log = fs::read_to_string(fixture.root.join("suspend_checkout.jsonl")).unwrap();
        let resume = log.lines().filter_map(|line| serde_json::from_str::<Value>(line).ok())
            .find(|entry| entry["method"] == "session/resume").expect("same resume path");
        assert_eq!(resume["params"]["cwd"], cwd.display().to_string());
        assert_eq!(resume["params"]["sessionId"], native_id.unwrap());
        fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }
}
