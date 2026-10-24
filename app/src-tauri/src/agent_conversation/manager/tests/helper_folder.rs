use super::*;

#[tokio::test(flavor = "current_thread")]
async fn start_session_folder_sets_the_helpers_folder_and_project() {
    let fixture = fixture_manager_with_acp_session("multiplex").await;
    let folder_root = fixture.root.join("helper-project");
    let folder = folder_root.join("sub");
    fs::create_dir_all(&folder).unwrap();
    let store = &fixture.manager.store;
    store.insert_or_get_project(&mcb_core::session_store::ProjectRow {
        id: "helper-project".into(), machine: "local".into(),
        root_path: folder_root.to_string_lossy().into_owned(), title: "Helper project".into(),
        repo_key: String::new(), created_at_ms: 1, group_key: String::new(),
        pinned_at_ms: None, last_used_ms: None,
    }).unwrap();
    let endpoint = super::super::super::messaging::MessagingEndpoint::start().unwrap();
    let caller = endpoint.issue(&fixture.owned_id);
    endpoint.bind_store(&caller.token, store, Some(fixture.manager.clone()));
    let result = endpoint.handle(&caller.token, json!({"name":"start_session","arguments":{
        "provider":"codex","name":"H","prompt":"Check the patch","folder":folder}})).await.unwrap();
    let started: Value = serde_json::from_str(result["content"][0]["text"].as_str().unwrap()).unwrap();
    let helper = started["owned_id"].as_str().unwrap();
    let row = store.get_session(helper).unwrap().unwrap();
    assert_eq!(row.cwd, folder.to_str().unwrap());
    assert_eq!(row.project_id.as_deref(), Some("helper-project"));
    assert_eq!(row.project, None, "the matched project supplies the label in the list query");
    let generation = fixture.manager.sessions.lock().unwrap().get(helper).unwrap().generation;
    fixture.manager.close(helper, generation).await.unwrap();
    fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}

#[tokio::test(flavor = "current_thread")]
async fn start_session_rejects_a_folder_that_does_not_exist() {
    let fixture = fixture_manager_with_acp_session("multiplex").await;
    let endpoint = super::super::super::messaging::MessagingEndpoint::start().unwrap();
    let caller = endpoint.issue(&fixture.owned_id);
    endpoint.bind_store(&caller.token, &fixture.manager.store, Some(fixture.manager.clone()));
    for folder in [json!("/nonexistent/tsk-1538"), json!("relative"), json!(7)] {
        let error = endpoint.handle(&caller.token, json!({"name":"start_session","arguments":{
            "provider":"codex","name":"H","prompt":"Check the patch","folder":folder}})).await.unwrap_err();
        assert!(error.contains(if folder.is_string() { "existing folder" } else { "absolute path" }), "{error}");
        assert!(fixture.manager.store.resolve_session_name("H").is_err());
    }
    fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}
