use super::*;

/// The rail's Done, Archive (settled) and Pin stamps and the row's branch are
/// saved with the session and come back after a restart, and a change made
/// after the restart (reopen and unpin) is saved too, so the row does not jump
/// back on the next start. Protects 29d78d9d2 (pin) and afa45ca9a (status
/// actions saved to rail metadata).
#[test]
fn rail_status_stamps_survive_restarts_and_changes_made_after_one() {
    let root = temp_root();
    let database = root.join("sessions.db");
    let meta = |stamp: Option<&str>| AgentConversationSessionMeta {
        branch: Some("tsk-1-rail".into()),
        completed_at: stamp.map(|stamp| format!("{stamp}T10:00:00.000Z")),
        settled_at: stamp.map(|stamp| format!("{stamp}T11:00:00.000Z")),
        pinned_at: stamp.map(|stamp| format!("{stamp}T12:00:00.000Z")),
        ..AgentConversationSessionMeta::default()
    };
    let save = |manager: &AgentRuntimeManager, meta| {
        manager.update_session_meta(UpdateAgentConversationSessionMetaRequest {
            owned_id: "owned-rail".into(), model: None, effort: None, meta,
        }).expect("save the rail row");
    };
    let stamps = |manager: &AgentRuntimeManager| {
        let meta = manager.list_sessions().unwrap().remove(0).meta;
        (meta.completed_at, meta.settled_at, meta.pinned_at, meta.branch)
    };

    let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
    manager.ensure_inner(request(root.to_str().unwrap(), "owned-rail", AgentConversationProvider::Codex)).unwrap();
    save(&manager, meta(Some("2026-10-01")));
    drop(manager);

    let reopened = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
    let saved = meta(Some("2026-10-01"));
    assert_eq!(stamps(&reopened), (saved.completed_at, saved.settled_at, saved.pinned_at, saved.branch));
    save(&reopened, meta(None));
    drop(reopened);

    let reopened = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
    assert_eq!(stamps(&reopened), (None, None, None, Some("tsk-1-rail".into())), "reopen and unpin stay saved");
    drop(reopened);
    fs::remove_dir_all(root).unwrap();
}
