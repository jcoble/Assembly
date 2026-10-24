use super::AgentRuntimeManager;
use super::super::{protocol::AgentConversationSessionRecord, remote::RemoteConnectionManager};
use mcb_core::session_store::SessionStore;

pub(crate) fn stored_session_id(owned_id: &str, profile: Option<&str>) -> String {
    profile.map(|profile| serde_json::json!([profile, owned_id]).to_string()).unwrap_or_else(|| owned_id.into())
}

pub(crate) fn apply_saved_projects(store: &SessionStore, sessions: &mut [AgentConversationSessionRecord]) -> Result<(), String> {
    let ids: Vec<_> = sessions.iter().map(|session| stored_session_id(&session.owned_id, session.remote_profile_id.as_deref())).collect();
    let ids = serde_json::to_string(&ids).map_err(|error| error.to_string())?;
    let projects = store.session_projects(&ids).map_err(|error| error.to_string())?;
    for (session, project) in sessions.iter_mut().zip(projects) {
        if project.is_some() { session.project_id = project; }
    }
    Ok(())
}

/// The project this desktop filed a remote session under, kept while its stale cached copy is dropped.
pub(crate) fn saved_project(store: &SessionStore, profile: &str, owned_id: &str) -> Result<Option<(String, String)>, String> {
    let key = stored_session_id(owned_id, Some(profile));
    let saved = store.session_projects(&serde_json::json!([key]).to_string()).map_err(|error| error.to_string())?;
    Ok(saved.into_iter().next().flatten().map(|project| (key, project)))
}

#[tauri::command]
pub async fn set_session_project(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    project_id: String,
) -> Result<(), String> {
    let profile = if remote.owns(&owned_id) { Some(remote.profile_for_owned_id(&owned_id)?) } else { None };
    manager.move_session_project(&stored_session_id(&owned_id, profile.as_deref()), &project_id)
}

impl AgentRuntimeManager {
    fn move_session_project(&self, owned_id: &str, project_id: &str) -> Result<(), String> {
        let mut sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        if !self.store.move_session_project(owned_id, project_id).map_err(|error| error.to_string())? {
            return Err("The session and destination project must exist on the same machine.".into());
        }
        // Later connection snapshots read this resident value; no ACP operation is needed.
        if let Some(session) = sessions.get_mut(owned_id) { session.project_id = Some(project_id.into()); }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use mcb_core::session_store::{ProjectRow, SessionRow};

    fn setup(machine: &str) -> (AgentRuntimeManager, SessionRow) {
        let manager = AgentRuntimeManager::default();
        for (id, machine) in [("a", machine), ("b", machine), ("other", "other-machine")] {
            manager.store.insert_or_get_project(&ProjectRow {
                id: id.into(), machine: machine.into(), root_path: format!("/projects/{id}"),
                title: id.into(), repo_key: String::new(), created_at_ms: 0,
                group_key: String::new(), pinned_at_ms: None, last_used_ms: None,
            }).unwrap();
        }
        let row = SessionRow {
            owned_id: stored_session_id("owned", (machine != "local").then_some(machine)), native_session_id: Some("native".into()),
            provider: "codex".into(), model: None, effort: None, cwd: "/original".into(),
            worktree: None, branch: None, title: None, title_source: None, project: None,
            project_id: Some("a".into()), state: "suspended".into(), suspended: true,
            created_at_ms: 0, last_activity_at_ms: 0, extra_json: "{}".into(),
        };
        if machine == "local" {
            manager.store.upsert_session(&row).unwrap();
        } else {
            manager.store.cache_remote_events(machine, &row, &[]).unwrap();
        }
        (manager, row)
    }

    #[test]
    fn session_project_move_overwrites_and_survives_upserts() {
        for machine in ["local", "box"] {
            let (manager, stale) = setup(machine);
            manager.move_session_project(&stale.owned_id, "b").unwrap();
            let moved = manager.store.get_session(&stale.owned_id).unwrap().unwrap();
            assert_eq!(moved.project_id.as_deref(), Some("b"));
            manager.store.upsert_session(&stale).unwrap();
            if machine != "local" {
                manager.store.cache_remote_events(machine, &stale, &[]).unwrap();
                manager.store.file_remote_sessions(machine, &serde_json::json!([[stale.owned_id, "a", 1, {}]]).to_string()).unwrap();
            }
            let saved = manager.store.get_session(&stale.owned_id).unwrap().unwrap();
            assert_eq!(saved.project_id.as_deref(), Some("b"));
            assert_eq!(manager.store.session_projects(&serde_json::json!([stale.owned_id]).to_string()).unwrap(), vec![Some("b".into())]);
            assert_eq!(saved.cwd, stale.cwd);
            assert_eq!(saved.native_session_id, stale.native_session_id);
        }
    }

    #[test]
    fn session_project_move_rejects_other_machine() {
        for machine in ["local", "box"] {
            let (manager, stale) = setup(machine);
            assert!(manager.move_session_project(&stale.owned_id, "other").is_err());
            assert!(manager.move_session_project(&stale.owned_id, "missing").is_err());
            assert!(manager.move_session_project("missing", "b").is_err());
            assert_eq!(manager.store.get_session(&stale.owned_id).unwrap().unwrap().project_id.as_deref(), Some("a"));
        }
    }
}
