//! A remote machine may only route ids that are its own: never one of this Mac's
//! sessions, and never one another machine already routes.
use std::sync::Arc;

use mcb_core::session_store::{SessionRow, SessionStore};

use super::connection_tests::session;
use super::RemoteConnectionManager;

fn local_row(owned_id: &str) -> SessionRow {
    SessionRow {
        owned_id: owned_id.into(), native_session_id: None, provider: "codex".into(), model: None, effort: None,
        cwd: "/work/project".into(), worktree: None, branch: None, title: None, title_source: None, project: None,
        project_id: None, state: "idle".into(), suspended: false, created_at_ms: 0, last_activity_at_ms: 0,
        extra_json: "{}".into(),
    }
}

fn connections(store: Arc<SessionStore>) -> RemoteConnectionManager {
    RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store).unwrap()
}

#[test]
fn a_remote_listing_that_claims_a_local_session_id_is_ignored() {
    let store = Arc::new(SessionStore::open_in_memory().unwrap());
    store.upsert_session(&local_row("local-1")).unwrap();
    let manager = connections(store.clone());

    manager.replace_cached_profile_sessions("workbox", &[session("local-1", "workbox", 5), session("remote-1", "workbox", 4)]).unwrap();
    manager.remember("local-1", "workbox");

    assert!(!manager.owns("local-1"), "a local session must keep routing locally");
    assert!(manager.owns("remote-1"));
    let cached = manager.cached_sessions().into_iter().map(|session| session.owned_id).collect::<Vec<_>>();
    assert_eq!(cached, vec!["remote-1"]);
    // A saved claim from before the fix does not come back on restart either.
    store.upsert_app_setting(super::REMOTE_SESSION_PROJECTIONS_SETTING_KEY,
        &serde_json::to_string(&[session("local-1", "workbox", 5)]).unwrap()).unwrap();
    let restarted = connections(store);
    assert!(!restarted.owns("local-1"));
    assert!(restarted.cached_sessions().is_empty());
}

#[test]
fn a_remote_listing_cannot_take_a_session_another_machine_routes() {
    let manager = connections(Arc::new(SessionStore::open_in_memory().unwrap()));
    manager.replace_cached_profile_sessions("one", &[session("shared", "one", 2)]).unwrap();

    manager.replace_cached_profile_sessions("two", &[session("shared", "two", 3)]).unwrap();
    manager.remember("shared", "two");

    assert_eq!(manager.profile_for_owned_id("shared").unwrap(), "one");
    let cached = manager.cached_sessions();
    assert_eq!(cached.len(), 1);
    assert_eq!(cached[0].remote_profile_id.as_deref(), Some("one"));
}
