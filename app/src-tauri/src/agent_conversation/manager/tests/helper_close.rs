use super::*;
use super::super::helper_close::HELPER_CLOSE_AFTER_MS;

#[tokio::test(flavor = "current_thread")]
async fn a_finished_helper_is_marked_done_once_and_the_rail_hears_it() {
    let fixture = fixture_manager_with_acp_session("multiplex").await;
    let (sender, mut events) = tokio::sync::mpsc::unbounded_channel();
    fixture.manager.set_emitter(Arc::new(move |event| { let _ = sender.send(event); }));
    let helper = fixture.manager.start_helper(&fixture.owned_id, AgentConversationProvider::Codex,
        None, "H", "Reply OK", None).await.unwrap();
    // The parent's completed reply turn follows delivery of the helper's finished report.
    while let Some(event) = events.recv().await {
        if event.owned_id == fixture.owned_id && matches!(event.payload,
            AgentConversationPayload::Turn { state: super::super::super::protocol::TurnState::Completed, .. }) { break; }
    }
    assert!(fixture.manager.store.pending_for(&fixture.owned_id).unwrap().is_none());
    let now = store_timestamp(timestamp_millis());
    assert_eq!(fixture.manager.close_finished_helpers(now + HELPER_CLOSE_AFTER_MS - 1000).unwrap(), 0);
    assert_eq!(fixture.manager.close_finished_helpers(now + HELPER_CLOSE_AFTER_MS + 1).unwrap(), 1);
    let row = fixture.manager.store.get_session(&helper).unwrap().unwrap();
    let stamp = serde_json::from_str::<Value>(&row.extra_json).unwrap()["railMeta"]["completedAt"].as_str().unwrap().to_string();
    let event = fixture.manager.snapshot(&helper).unwrap().unwrap().events.into_iter()
        .find(|event| serde_json::to_value(&event.payload).unwrap()["kind"] == "sessionCompleted").unwrap();
    assert_eq!(serde_json::to_value(&event.payload).unwrap()["completedAt"], stamp);
    let mut emitted = false;
    while let Ok(event) = events.try_recv() {
        if event.owned_id == helper && serde_json::to_value(&event.payload).unwrap()["kind"] == "sessionCompleted" { emitted = true; }
    }
    assert!(emitted, "the rail must hear completion without opening the helper");
    let mut meta = fixture.manager.sessions.lock().unwrap().get(&helper).unwrap().rail_meta.clone();
    meta.completed_at = None;
    fixture.manager.update_session_meta(UpdateAgentConversationSessionMetaRequest {
        owned_id: helper.clone(), model: None, effort: None, meta,
    }).unwrap();
    assert_eq!(fixture.manager.close_finished_helpers(now + 2 * HELPER_CLOSE_AFTER_MS).unwrap(), 0);
    let generation = fixture.manager.sessions.lock().unwrap().get(&helper).unwrap().generation;
    fixture.manager.close(&helper, generation).await.unwrap();
    fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}

#[tokio::test(flavor = "current_thread")]
async fn helper_close_reads_the_minutes_setting_each_sweep_and_zero_disables_it() {
    let fixture = fixture_manager_with_acp_session("multiplex").await;
    {
        let mut sessions = fixture.manager.sessions.lock().unwrap();
        let session = sessions.get_mut(&fixture.owned_id).unwrap();
        session.helper_of = Some("parent".into());
        persist_session(session).unwrap();
    }
    let now = store_timestamp(timestamp_millis());
    fixture.manager.write_app_setting("helper_auto_done_minutes", "0").unwrap();
    assert_eq!(fixture.manager.close_finished_helpers(now + 2 * HELPER_CLOSE_AFTER_MS).unwrap(), 0);
    fixture.manager.write_app_setting("helper_auto_done_minutes", "1440").unwrap();
    assert_eq!(fixture.manager.close_finished_helpers(now + 2 * HELPER_CLOSE_AFTER_MS).unwrap(), 0);
    fixture.manager.write_app_setting("helper_auto_done_minutes", "15").unwrap();
    assert_eq!(fixture.manager.close_finished_helpers(now + 14 * 60_000).unwrap(), 0);
    assert_eq!(fixture.manager.close_finished_helpers(now + 16 * 60_000).unwrap(), 1);
    fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}

#[tokio::test(flavor = "current_thread")]
async fn helper_close_preserves_a_candidate_that_has_new_live_activity() {
    let fixture = fixture_manager_with_acp_session("multiplex").await;
    let now = store_timestamp(timestamp_millis());
    {
        let mut sessions = fixture.manager.sessions.lock().unwrap();
        let session = sessions.get_mut(&fixture.owned_id).unwrap();
        session.helper_of = Some("parent".into());
        persist_session(session).unwrap();
        // The SQL selection saw the quiet row before this new activity arrived.
        session.last_activity_ms = (now + 2 * HELPER_CLOSE_AFTER_MS) as u128;
    }
    assert_eq!(fixture.manager.close_finished_helpers(now + HELPER_CLOSE_AFTER_MS + 1).unwrap(), 0);
    assert!(fixture.manager.sessions.lock().unwrap().get(&fixture.owned_id).unwrap().rail_meta.completed_at.is_none());
    fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}

#[test]
fn helper_close_restores_metadata_when_the_completion_write_fails() {
    let root = temp_root();
    let database = root.join("sessions.db");
    let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
    let mut extra: Value = serde_json::from_str(&imported_session_extra(AgentConversationProvider::Codex).unwrap()).unwrap();
    extra["helperOf"] = json!("parent");
    manager.store.upsert_session(&SessionRow {
        owned_id: "helper".into(), native_session_id: None, provider: "codex".into(),
        model: None, effort: None, cwd: root.to_string_lossy().into_owned(), worktree: None,
        branch: None, title: None, title_source: None, project: None, project_id: None,
        state: "ready".into(), suspended: true, created_at_ms: 0, last_activity_at_ms: 0,
        extra_json: extra.to_string(),
    }).unwrap();
    let connection = rusqlite::Connection::open(&database).unwrap();
    connection.execute_batch("CREATE TRIGGER reject_completion BEFORE INSERT ON events
        WHEN json_extract(NEW.payload,'$.payload.kind')='sessionCompleted'
        BEGIN SELECT RAISE(ABORT,'completion write rejected'); END;").unwrap();
    assert_eq!(manager.close_finished_helpers(HELPER_CLOSE_AFTER_MS + 1).unwrap(), 0);
    assert!(manager.sessions.lock().unwrap().get("helper").unwrap().rail_meta.completed_at.is_none());
    assert!(manager.store.finished_helpers_due(HELPER_CLOSE_AFTER_MS + 1, HELPER_CLOSE_AFTER_MS).unwrap().contains(&"helper".into()));
    connection.execute_batch("DROP TRIGGER reject_completion;").unwrap();
    assert_eq!(manager.close_finished_helpers(HELPER_CLOSE_AFTER_MS + 1).unwrap(), 1);
    drop(connection);
    drop(manager);
    fs::remove_dir_all(root).unwrap();
}

#[tokio::test(flavor = "current_thread")]
async fn helper_close_waits_for_a_running_native_sub_agent() {
    let fixture = fixture_manager_with_acp_session("multiplex").await;
    {
        let mut sessions = fixture.manager.sessions.lock().unwrap();
        let session = sessions.get_mut(&fixture.owned_id).unwrap();
        session.helper_of = Some("parent".into());
        session.codex_children.insert("child".into(), CodexChildRollout {
            child_id: "child".into(), label: "probe".into(),
            state: "running".into(), latest_activity: "Running".into(),
        });
        persist_session(session).unwrap();
    }
    let now = store_timestamp(timestamp_millis()) + HELPER_CLOSE_AFTER_MS + 1;
    assert_eq!(fixture.manager.close_finished_helpers(now).unwrap(), 0);
    fixture.manager.sessions.lock().unwrap().get_mut(&fixture.owned_id).unwrap()
        .codex_children.get_mut("child").unwrap().state = "finished".into();
    assert_eq!(fixture.manager.close_finished_helpers(now).unwrap(), 1);
    fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}
